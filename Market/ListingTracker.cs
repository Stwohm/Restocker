using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Restocker.Data;

namespace Restocker.Market;

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

    public double? DaysListed => EndedUtc is { } e ? (e - FirstSeenUtc).TotalDays : null;
}

/// <summary>
/// Fork: per-listing lifecycle + competitor bookkeeping. Pure logic with an injected clock;
/// the plugin feeds it from RetainerWatcher (listings), MarketWatcher (competitors), Executor
/// (applied reprices) and the RetainerSale chat message.
/// </summary>
public sealed class ListingTracker
{
    public const string Active = "active";
    public const string Sold = "sold";
    public const string Gone = "gone";

    /// <summary>A listing must stay missing this long (across observations) before it is closed as gone.</summary>
    public TimeSpan GoneAfter { get; set; } = TimeSpan.FromSeconds(60);

    public List<ListingRecord> Records { get; set; } = new();

    /// <summary>Set by every mutation; the owner saves and clears it.</summary>
    public bool Dirty { get; set; }

    public IEnumerable<ListingRecord> ActiveFor(uint itemId, bool hq)
        => Records.Where(r => r.Outcome == Active && r.ItemId == itemId && r.IsHq == hq);

    /// <summary>The retainer's sell list as read now. Only call with a loaded container.</summary>
    public void ObserveRetainer(
        string retainerKey,
        string retainerName,
        IReadOnlyList<LiveListing> live,
        Func<uint, string> itemName,
        DateTime nowUtc)
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
            if (rec.MissingSinceUtc == null)
            {
                rec.MissingSinceUtc = nowUtc;
            }
            else if (nowUtc - rec.MissingSinceUtc.Value >= GoneAfter)
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
        var candidates = ActiveFor(itemId, hq).ToList();
        if (candidates.Count == 0) return null;
        var rec = totalGil > 0
            ? candidates.OrderBy(r => Math.Abs(r.LastPrice * r.Quantity * 0.95 - totalGil)).First()
            : candidates.First();
        rec.Outcome = Sold;
        rec.EndedUtc = nowUtc;
        rec.SoldTotalGil = totalGil;
        rec.MissingSinceUtc = null;
        Dirty = true;
        return rec;
    }

    // ---------------- persistence / export ----------------

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static ListingTracker Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var recs = JsonSerializer.Deserialize<List<ListingRecord>>(File.ReadAllText(path));
                if (recs != null) return new ListingTracker { Records = recs };
            }
        }
        catch { /* corrupt file: start fresh, keep the old one aside */ TryBackup(path); }
        return new ListingTracker();
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
        "listing_id,retainer,item_id,item_name,hq,qty,first_seen_utc,first_price,last_price,reprice_count,undercut_count,undercutters,competitors,ended_utc,outcome,sold_total_gil,days_listed";

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
                Iso(r.FirstSeenUtc),
                r.FirstPrice.ToString(inv),
                r.LastPrice.ToString(inv),
                r.RepriceCount.ToString(inv),
                r.UndercutCount.ToString(inv),
                Csv(undercutters),
                Csv(competitors),
                r.EndedUtc is { } e ? Iso(e) : string.Empty,
                r.Outcome,
                r.SoldTotalGil.ToString(inv),
                r.DaysListed is { } d ? d.ToString("0.00", inv) : string.Empty));
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
