using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Restocker.Data;

namespace Restocker.Market;

public enum HistorySaleResult { Duplicate, MatchedListing, HistoryOnly }

/// <summary>Who else sells the same item (retainer name; the game does not send player names).</summary>
public sealed class CompetitorStat
{
    public string Seller { get; set; } = string.Empty;
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public long LowestPrice { get; set; }
    public int TimesSeen { get; set; }

    /// <summary>How many observations had this seller priced below our listing.</summary>
    public int TimesBelowUs { get; set; }

    /// <summary>Units this seller had up (all their listings summed) at the last observation.</summary>
    public int LastQuantity { get; set; }

    /// <summary>Most units this seller had up at once.</summary>
    public int MaxQuantity { get; set; }
}

/// <summary>One listing from the moment it is first seen on a retainer until it sells or disappears.</summary>
public sealed class ListingRecord
{
    public string Id { get; set; } = string.Empty;
    public string RetainerKey { get; set; } = string.Empty;
    public string RetainerName { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHq { get; set; }
    public int Quantity { get; set; }
    public int Slot { get; set; }

    public DateTime FirstSeenUtc { get; set; }
    public long FirstPrice { get; set; }
    public long LastPrice { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Price changes on this listing (by Restocky or by hand in game).</summary>
    public int RepriceCount { get; set; }

    /// <summary>Observations where at least one competitor was below our price.</summary>
    public int UndercutCount { get; set; }

    public Dictionary<string, CompetitorStat> Competitors { get; set; } = new();

    public DateTime? MissingSinceUtc { get; set; }
    public DateTime? EndedUtc { get; set; }

    /// <summary>active / sold (sale message seen) / gone (disappeared: sold offline or taken down).</summary>
    public string Outcome { get; set; } = ListingTracker.Active;

    /// <summary>Gil received according to the sale message (after fees). 0 = unknown.</summary>
    public long SoldTotalGil { get; set; }

    /// <summary>When Restocky received the sale message; distinct from EndedUtc for gone→sold recovery.</summary>
    public DateTime? SoldRecordedUtc { get; set; }

    /// <summary>First source to report the sale: game, allaganmarket, or history.</summary>
    public string SoldSource { get; set; } = string.Empty;

    // Fork: authoritative sale history and the transaction identity survive reloads.
    public string? HistoryKey { get; set; }
    public string? BuyerName { get; set; }
    public bool? IsMannequin { get; set; }
    public long? SoldGrossGil { get; set; }
    public DateTime? SaleTimeUtc { get; set; }
    public bool HistoryOnly { get; set; }

    // Fork: null cost identifies sales recorded before cost tracking was available.
    public decimal? ConsumedCost { get; set; }
    public int? UnknownCostUnits { get; set; }
    public bool StuckNotified { get; set; }

    [JsonIgnore]
    public DateTime? RecordedSaleUtc => SaleTimeUtc ?? SoldRecordedUtc ?? EndedUtc;
    [JsonIgnore]
    public decimal? RealizedProfit => Outcome == ListingTracker.Sold && (SoldTotalGil > 0 || HistoryKey != null)
        ? SoldTotalGil - ConsumedCost : null;
    public double? DaysListed => !HistoryOnly && EndedUtc is { } e ? (e - FirstSeenUtc).TotalDays : null;
}

public delegate (decimal ConsumedCost, int UnknownUnits) SaleCostConsumer(uint itemId, bool hq, int quantity, DateTime saleUtc);

/// <summary>
/// Fork: per-listing lifecycle + competitor bookkeeping. Pure logic with an injected clock;
/// the plugin feeds it from RetainerWatcher (listings), MarketWatcher (competitors), Executor
/// (applied reprices) and the RetainerSale chat message.
/// </summary>
public sealed class ListingTracker(SaleCostConsumer? consumeCost = null)
{
    public const string Active = "active";
    public const string Sold = "sold";
    public const string Gone = "gone";
    public const string GameSaleSource = "game";
    public const string AllaganMarketSaleSource = "allaganmarket";

    /// <summary>Retainer sale proceeds after the market's five-percent fee.</summary>
    public const decimal SaleNetRate = 0.95m;

    private static readonly TimeSpan GoneSaleWindow = TimeSpan.FromHours(72);
    private static readonly TimeSpan DuplicateSaleWindow = TimeSpan.FromMinutes(10);

    /// <summary>A listing must stay missing this long (across observations) before it is closed as gone.</summary>
    public TimeSpan GoneAfter { get; set; } = TimeSpan.FromSeconds(60);

    public List<ListingRecord> Records { get; set; } = new();

    /// <summary>Set by every mutation; the owner saves and clears it.</summary>
    public bool Dirty { get; set; }

    public IEnumerable<ListingRecord> ActiveFor(uint itemId, bool hq)
        => Records.Where(r => r.Outcome == Active && r.ItemId == itemId && r.IsHq == hq);

    // Fork: stuck age follows the listing through reprices and slot moves.
    public static bool IsStuck(ListingRecord listing, DateTime nowUtc, int thresholdDays)
        => listing.Outcome == Active && listing.MissingSinceUtc == null
            && (nowUtc - listing.FirstSeenUtc).TotalDays >= Math.Max(1, thresholdDays);

    public bool TryMarkStuckNotified(ListingRecord listing, DateTime nowUtc, int thresholdDays)
    {
        if (listing.StuckNotified || !IsStuck(listing, nowUtc, thresholdDays)) return false;
        listing.StuckNotified = true;
        Dirty = true;
        return true;
    }

    /// <summary>The retainer's sell list as read now. Only call with a loaded container.</summary>
    public void ObserveRetainer(
        string retainerKey,
        string retainerName,
        IReadOnlyList<LiveListing> live,
        Func<uint, string> itemName,
        DateTime nowUtc,
        bool authoritative = false)
    {
        var open = Records.Where(r => r.Outcome == Active && r.RetainerKey == retainerKey).ToList();
        var matched = new HashSet<ListingRecord>();

        foreach (var l in live)
        {
            var rec = open.FirstOrDefault(r => !matched.Contains(r) && r.Slot == l.Slot && Same(r, l))
                      ?? open.FirstOrDefault(r => !matched.Contains(r) && Same(r, l));
            if (rec == null)
            {
                rec = new ListingRecord
                {
                    Id = Guid.NewGuid().ToString("N")[..10],
                    RetainerKey = retainerKey,
                    RetainerName = retainerName,
                    ItemId = l.ItemId,
                    ItemName = itemName(l.ItemId),
                    IsHq = l.IsHQ,
                    Quantity = l.Quantity,
                    Slot = l.Slot,
                    FirstSeenUtc = nowUtc,
                    FirstPrice = l.UnitPrice,
                    LastPrice = l.UnitPrice,
                    LastSeenUtc = nowUtc,
                };
                Records.Add(rec);
                Dirty = true;
            }
            else
            {
                if (l.UnitPrice > 0 && l.UnitPrice != rec.LastPrice)
                {
                    rec.LastPrice = l.UnitPrice;
                    rec.RepriceCount++;
                }
                rec.Slot = l.Slot;
                rec.RetainerName = retainerName;
                rec.MissingSinceUtc = null;
                rec.LastSeenUtc = nowUtc;
                Dirty = true;
            }
            matched.Add(rec);
        }

        foreach (var rec in open)
        {
            if (matched.Contains(rec)) continue;
            rec.MissingSinceUtc ??= nowUtc;
            // An authoritative read (executor, after the sell list settled) or any non-empty read
            // is trusted: a listing that is not there is gone now. Only a passive EMPTY read waits,
            // because the container can briefly read empty while the list is still loading.
            if (authoritative || live.Count > 0 || nowUtc - rec.MissingSinceUtc.Value >= GoneAfter)
            {
                rec.Outcome = Gone;
                rec.EndedUtc = rec.MissingSinceUtc;
            }
            Dirty = true;
        }
    }

    private static bool Same(ListingRecord r, LiveListing l)
        => r.ItemId == l.ItemId && r.IsHq == l.IsHQ && r.Quantity == l.Quantity;

    /// <summary>A fresh market answer for (item, hq) from our own fetch.</summary>
    public void ObserveCompetitors(uint itemId, bool hq, IReadOnlyList<MarketOffer> offers, DateTime nowUtc)
    {
        foreach (var rec in ActiveFor(itemId, hq))
        {
            var undercut = false;
            // One observation per seller, even if they have several listings up.
            foreach (var g in offers.GroupBy(o => string.IsNullOrEmpty(o.Seller) ? "?" : o.Seller))
            {
                var name = g.Key;
                var lowest = g.Min(o => o.Price);
                var units = g.Sum(o => Math.Max(1, o.Quantity));
                if (!rec.Competitors.TryGetValue(name, out var st))
                {
                    st = new CompetitorStat { Seller = name, FirstSeenUtc = nowUtc, LowestPrice = lowest };
                    rec.Competitors[name] = st;
                }
                st.TimesSeen++;
                st.LastSeenUtc = nowUtc;
                st.LastQuantity = units;
                if (units > st.MaxQuantity) st.MaxQuantity = units;
                if (lowest < st.LowestPrice) st.LowestPrice = lowest;
                if (lowest < rec.LastPrice)
                {
                    st.TimesBelowUs++;
                    undercut = true;
                }
            }
            if (undercut) rec.UndercutCount++;
            Dirty = true;
        }
    }

    public void OnRepriceApplied(string retainerKey, int slot, uint itemId, bool hq, long newPrice)
    {
        var rec = Records.FirstOrDefault(r => r.Outcome == Active && r.RetainerKey == retainerKey
                                              && r.Slot == slot && r.ItemId == itemId && r.IsHq == hq);
        if (rec == null || rec.LastPrice == newPrice) return;
        rec.LastPrice = newPrice;
        rec.RepriceCount++;
        Dirty = true;
    }

    /// <summary>
    /// RetainerSale chat message. The message names the item and the gil received, not the
    /// retainer or the stack, so the active listing whose qty x price (minus ~5% tax) is closest wins.
    /// </summary>
    public ListingRecord? OnSale(uint itemId, bool hq, long totalGil, DateTime nowUtc)
    {
        if (IsDuplicateSale(new[] { itemId }, totalGil, nowUtc, GameSaleSource)) return null;
        var candidates = ActiveFor(itemId, hq).ToList();
        if (candidates.Count == 0) return null;
        var rec = totalGil > 0
            ? candidates.OrderBy(r => Difference(r, totalGil)).First()
            : candidates.First();
        MarkSold(rec, totalGil, nowUtc, keepEndedUtc: false, GameSaleSource);
        return rec;
    }

    /// <summary>
    /// AllaganMarket sale message. Item quality is unknown, but stack quantity is known. Active
    /// listings are preferred; a recent unclassified gone listing is the fallback.
    /// </summary>
    public ListingRecord? OnAllaganSale(
        IReadOnlyCollection<uint> candidateItemIds,
        int quantity,
        long netGil,
        DateTime nowUtc)
    {
        if (candidateItemIds.Count == 0 || quantity <= 0 || netGil <= 0) return null;
        var ids = candidateItemIds as HashSet<uint> ?? candidateItemIds.ToHashSet();
        if (IsDuplicateSale(ids, netGil, nowUtc, AllaganMarketSaleSource)) return null;

        var active = Records.Where(r => r.Outcome == Active && ids.Contains(r.ItemId) && r.Quantity == quantity);
        var rec = BestWithinTolerance(active, netGil);
        if (rec != null)
        {
            MarkSold(rec, netGil, nowUtc, keepEndedUtc: false, AllaganMarketSaleSource);
            return rec;
        }

        var gone = Records.Where(r => r.Outcome == Gone && r.SoldTotalGil == 0
            && ids.Contains(r.ItemId) && r.Quantity == quantity && r.EndedUtc is { } ended
            && nowUtc >= ended && nowUtc - ended <= GoneSaleWindow);
        rec = BestWithinTolerance(gone, netGil);
        if (rec == null) return null;
        MarkSold(rec, netGil, nowUtc, keepEndedUtc: true, AllaganMarketSaleSource);
        return rec;
    }

    public static long NetFromGross(long grossGil) => grossGil <= 0 ? 0 : (long)(grossGil * SaleNetRate);

    // Fork: include chat-only sales, whose receipt time is the best available timestamp.
    public IEnumerable<ListingRecord> GetSales() => Records.Where(r => r.Outcome == Sold);

    public IEnumerable<ListingRecord> GetSales(uint itemId, bool hq)
        => GetSales().Where(r => r.ItemId == itemId && r.IsHq == hq)
            .OrderByDescending(r => r.RecordedSaleUtc);

    public HistorySaleResult OnHistorySale(string retainerKey, string retainerName, RetainerHistorySale sale,
        Func<uint, string> itemName, Action<string>? log = null)
    {
        var key = FormattableString.Invariant($"{retainerKey}|{sale.ItemId}|{sale.IsHq}|{sale.Quantity}|{sale.NetGil}|{sale.UnixSeconds}|{sale.Buyer}");
        if (Records.Any(r => r.HistoryKey == key))
        {
            log?.Invoke($"[Restocky] history: OnHistorySale skipped duplicate item={sale.ItemId} qty={sale.Quantity} unix={sale.UnixSeconds}");
            return HistorySaleResult.Duplicate;
        }
        var time = DateTimeOffset.FromUnixTimeSeconds(sale.UnixSeconds).UtcDateTime;
        // Packet times have one-second precision. Never consume a newer relisting, or a
        // listing still observed live after the sale, just because the price looks right.
        var candidates = Records.Where(r => r.HistoryKey == null && r.RetainerKey == retainerKey
            && r.ItemId == sale.ItemId && r.IsHq == sale.IsHq && r.Quantity == sale.Quantity
            && r.FirstSeenUtc < time.AddSeconds(1));
        var reported = candidates.Where(r => r.Outcome == Sold && r.SoldRecordedUtc != null);
        var rec = reported.Where(r => (r.SoldRecordedUtc!.Value - time).Duration() <= DuplicateSaleWindow
                && Math.Abs(r.SoldTotalGil - sale.NetGil) <= Tolerance(sale.NetGil))
            .OrderBy(r => Math.Abs(r.SoldTotalGil - sale.NetGil))
            .ThenBy(r => (r.SoldRecordedUtc!.Value - time).Duration()).FirstOrDefault();
        // Fork: a game notice may have closed the stack even when its proceeds could not be parsed.
        if (rec == null)
        {
            var unpriced = reported.Where(r => r.SoldSource == GameSaleSource && r.SoldTotalGil == 0
                && (r.SoldRecordedUtc!.Value - time).Duration() <= DuplicateSaleWindow
                && Difference(r, sale.NetGil) <= Tolerance(sale.NetGil)).Take(2).ToList();
            if (unpriced.Count == 1) rec = unpriced[0];
        }
        var listings = candidates.Where(r => r.LastSeenUtc < time.AddSeconds(1)
                && (r.Outcome == Active || (r.Outcome == Gone && r.EndedUtc >= time)))
            .OrderByDescending(r => r.Outcome == Gone).ThenByDescending(r => r.LastSeenUtc).ToList();
        // A notice delivered on login may be days newer than the transaction. Reconcile
        // an exact, unambiguous amount inside its listing lifetime if no live/gone stack fits.
        if (rec == null && listings.Count == 0)
        {
            var delayed = reported.Where(r => r.SoldTotalGil == sale.NetGil
                && r.LastSeenUtc < time.AddSeconds(1) && r.EndedUtc >= time).Take(2).ToList();
            if (delayed.Count == 1) rec = delayed[0];
        }
        rec ??= BestWithinTolerance(listings, sale.NetGil);
        // A single eligible stack can have changed price since our last observation.
        if (rec == null && listings.Count == 1) rec = listings[0];
        var newRecord = rec == null;
        if (rec == null)
        {
            rec = new ListingRecord
            {
                Id = Guid.NewGuid().ToString("N")[..10], RetainerKey = retainerKey,
                RetainerName = retainerName, ItemId = sale.ItemId, ItemName = itemName(sale.ItemId),
                IsHq = sale.IsHq, Quantity = sale.Quantity, Slot = -1, HistoryOnly = true,
                FirstSeenUtc = time, LastSeenUtc = time,
            };
        }
        else if (rec.SoldGrossGil == null && NetFromGross(rec.LastPrice * rec.Quantity) == sale.NetGil)
        {
            rec.SoldGrossGil = rec.LastPrice * rec.Quantity;
        }

        // Keep the original chat source and receipt time for paired-notice deduplication.
        var source = rec.Outcome == Sold ? rec.SoldSource : "history";
        var receipt = rec.SoldRecordedUtc ?? time;
        MarkSold(rec, sale.NetGil, receipt, keepEndedUtc: false, source, saleUtc: time);
        rec.EndedUtc = time;
        rec.HistoryKey = key;
        rec.SaleTimeUtc = time;
        rec.BuyerName = sale.Buyer;
        rec.IsMannequin = sale.IsMannequin;
        if (newRecord) Records.Add(rec);
        return newRecord ? HistorySaleResult.HistoryOnly : HistorySaleResult.MatchedListing;
    }

    private static ListingRecord? BestWithinTolerance(IEnumerable<ListingRecord> candidates, long netGil)
    {
        var rec = candidates.OrderBy(r => Difference(r, netGil)).FirstOrDefault();
        return rec != null && Difference(rec, netGil) <= Tolerance(netGil) ? rec : null;
    }

    private bool IsDuplicateSale(IEnumerable<uint> itemIds, long netGil, DateTime nowUtc, string source)
    {
        if (netGil <= 0) return false;
        var ids = itemIds as ISet<uint> ?? itemIds.ToHashSet();
        return Records.Any(r => r.Outcome == Sold && ids.Contains(r.ItemId) && r.SoldTotalGil > 0
            && !string.IsNullOrEmpty(r.SoldSource) && r.SoldSource != source
            && r.SoldRecordedUtc is { } recorded && nowUtc >= recorded
            && nowUtc - recorded <= DuplicateSaleWindow
            && Math.Abs((decimal)r.SoldTotalGil - netGil) <= Tolerance(netGil));
    }

    private void MarkSold(ListingRecord rec, long totalGil, DateTime nowUtc, bool keepEndedUtc, string source,
        DateTime? saleUtc = null)
    {
        // Fork: history enrichment updates proceeds and timing on the same sold record.
        if (rec.Outcome != Sold && consumeCost != null)
            (rec.ConsumedCost, rec.UnknownCostUnits) = consumeCost(rec.ItemId, rec.IsHq, rec.Quantity, saleUtc ?? nowUtc);

        rec.Outcome = Sold;
        if (!keepEndedUtc) rec.EndedUtc = nowUtc;
        rec.SoldTotalGil = totalGil;
        rec.SoldRecordedUtc = nowUtc;
        rec.SoldSource = source;
        rec.MissingSinceUtc = null;
        Dirty = true;
    }

    private static decimal Difference(ListingRecord rec, long netGil)
        => Math.Abs(rec.LastPrice * rec.Quantity * SaleNetRate - netGil);

    private static decimal Tolerance(long netGil) => Math.Max(2m, netGil * 0.02m);

    // ---------------- persistence / export ----------------

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static ListingTracker Load(string path, SaleCostConsumer? consumeCost = null)
    {
        try
        {
            if (File.Exists(path))
            {
                var recs = JsonSerializer.Deserialize<List<ListingRecord>>(File.ReadAllText(path));
                if (recs != null) return new ListingTracker(consumeCost) { Records = recs };
            }
        }
        catch { /* corrupt file: start fresh, keep the old one aside */ TryBackup(path); }
        return new ListingTracker(consumeCost);
    }

    private static void TryBackup(string path)
    {
        try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
    }

    /// <summary>Writes listings.json, listings.csv and competitors.csv into <paramref name="dir"/>.</summary>
    public bool Save(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            WriteAtomic(Path.Combine(dir, "listings.json"), JsonSerializer.Serialize(Records, JsonOpts));
            WriteAtomic(Path.Combine(dir, "listings.csv"), ListingsCsv());
            WriteAtomic(Path.Combine(dir, "competitors.csv"), CompetitorsCsv());
            Dirty = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(true)); // BOM so Excel opens UTF-8 names correctly
        File.Move(tmp, path, overwrite: true);
    }

    public const string ListingsHeader =
        "listing_id,retainer,item_id,item_name,hq,qty,first_seen_utc,first_price,last_price,reprice_count,undercut_count,undercutters,competitors,ended_utc,outcome,sold_total_gil,days_listed,buyer,sale_time_utc,sold_gross_gil,is_mannequin,cost,profit";

    public string ListingsCsv()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(ListingsHeader);
        foreach (var r in Records.OrderBy(r => r.FirstSeenUtc))
        {
            // "Rival 4x" = seller and the most units they had up at once.
            var undercutters = string.Join(";", r.Competitors.Values.Where(c => c.TimesBelowUs > 0)
                .OrderByDescending(c => c.TimesBelowUs).Select(c => $"{c.Seller} {c.MaxQuantity}x"));
            var competitors = string.Join(";", r.Competitors.Values.OrderByDescending(c => c.TimesSeen)
                .Select(c => $"{c.Seller} {c.MaxQuantity}x"));
            sb.AppendLine(string.Join(",",
                r.Id,
                Csv(r.RetainerName),
                r.ItemId.ToString(inv),
                Csv(r.ItemName),
                r.IsHq ? "true" : "false",
                r.Quantity.ToString(inv),
                r.HistoryOnly ? string.Empty : Iso(r.FirstSeenUtc),
                r.HistoryOnly ? string.Empty : r.FirstPrice.ToString(inv),
                r.HistoryOnly ? string.Empty : r.LastPrice.ToString(inv),
                r.RepriceCount.ToString(inv),
                r.UndercutCount.ToString(inv),
                Csv(undercutters),
                Csv(competitors),
                r.EndedUtc is { } e ? Iso(e) : string.Empty,
                r.Outcome,
                r.SoldTotalGil.ToString(inv),
                r.DaysListed is { } d ? d.ToString("0.00", inv) : string.Empty,
                Csv(r.BuyerName ?? string.Empty),
                (r.SaleTimeUtc ?? r.SoldRecordedUtc) is { } soldAt ? Iso(soldAt) : string.Empty,
                r.SoldGrossGil?.ToString(inv) ?? string.Empty,
                r.IsMannequin?.ToString().ToLowerInvariant() ?? string.Empty,
                r.ConsumedCost?.ToString(inv) ?? string.Empty,
                r.RealizedProfit?.ToString(inv) ?? string.Empty));
        }
        return sb.ToString();
    }

    public const string CompetitorsHeader =
        "item_id,item_name,hq,competitor,times_seen,times_below_us,lowest_price,last_qty,max_qty,first_seen_utc,last_seen_utc,listings_contested";

    /// <summary>One row per (item, hq, competitor), aggregated over every listing of ours.</summary>
    public string CompetitorsCsv()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(CompetitorsHeader);
        var rows = Records
            .SelectMany(r => r.Competitors.Values.Select(c => (r, c)))
            .GroupBy(x => (x.r.ItemId, x.r.IsHq, x.c.Seller))
            .Select(g => new
            {
                g.Key.ItemId,
                Name = g.First().r.ItemName,
                g.Key.IsHq,
                g.Key.Seller,
                Seen = g.Sum(x => x.c.TimesSeen),
                Below = g.Sum(x => x.c.TimesBelowUs),
                Lowest = g.Min(x => x.c.LowestPrice),
                LastQty = g.OrderByDescending(x => x.c.LastSeenUtc).First().c.LastQuantity,
                MaxQty = g.Max(x => x.c.MaxQuantity),
                First = g.Min(x => x.c.FirstSeenUtc),
                Last = g.Max(x => x.c.LastSeenUtc),
                Listings = g.Count(),
            })
            .OrderBy(x => x.Name).ThenByDescending(x => x.Below);
        foreach (var x in rows)
        {
            sb.AppendLine(string.Join(",",
                x.ItemId.ToString(inv), Csv(x.Name), x.IsHq ? "true" : "false", Csv(x.Seller),
                x.Seen.ToString(inv), x.Below.ToString(inv), x.Lowest.ToString(inv),
                x.LastQty.ToString(inv), x.MaxQty.ToString(inv),
                Iso(x.First), Iso(x.Last), x.Listings.ToString(inv)));
        }
        return sb.ToString();
    }

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string Csv(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
