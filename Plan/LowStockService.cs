using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Restocker.Data;
using Restocker.Market;

namespace Restocker.Plan;

/// <summary>
/// Fork: low-stock alerts and restock planning for the items in <see cref="Configuration.LowStock"/>.
/// "Listed" = units on the current character's retainer markets, from the listing tracker (a sale
/// message closes a listing immediately). "Available" = retainer inventories + bag + saddlebag
/// from the snapshots taken at the bell.
/// </summary>
public sealed class LowStockService : IDisposable
{
    private readonly IFramework framework;
    private readonly Configuration configuration;
    private readonly ListingTracker tracker;
    private readonly MarketCache marketCache;
    private readonly Dictionary<(uint, bool), bool> notified = new();
    private DateTime nextCheckUtc = DateTime.MinValue;

    public LowStockService(IFramework framework, Configuration configuration, ListingTracker tracker, MarketCache marketCache)
    {
        this.framework = framework;
        this.configuration = configuration;
        this.tracker = tracker;
        this.marketCache = marketCache;
        framework.Update += OnUpdate;
    }

    public void Dispose() => framework.Update -= OnUpdate;

    private static ulong Cid() => Plugin.PlayerState.ContentId;

    private IEnumerable<RetainerSnapshot> MyRetainers()
    {
        var cid = Cid();
        return configuration.Snapshots.Values.Where(s => cid == 0 || s.CharacterContentId == cid);
    }

    public int ListedUnits(uint itemId, bool hq)
    {
        var keys = MyRetainers().Select(s => s.Key).ToHashSet();
        return tracker.ActiveFor(itemId, hq).Where(r => keys.Contains(r.RetainerKey)).Sum(r => r.Quantity);
    }

    public List<RestockSource> Sources(uint itemId, bool hq)
    {
        var list = new List<RestockSource>();
        foreach (var s in MyRetainers())
        {
            var n = s.Inventory.Where(e => e.ItemId == itemId && e.IsHQ == hq).Sum(e => e.Quantity);
            if (n > 0) list.Add(new RestockSource(s.Key, s.Key, n));
        }
        if (configuration.Characters.TryGetValue(CharacterSnapshot.MakeKey(Cid()), out var ch))
        {
            var prefix = CharacterSnapshot.MakeKey(ch.CharacterContentId);
            var bag = ch.Bag.Where(e => e.ItemId == itemId && e.IsHQ == hq).Sum(e => e.Quantity);
            if (bag > 0) list.Add(new RestockSource(prefix + ".bag", null, bag));
            var saddle = ch.Saddlebag.Concat(ch.PremiumSaddlebag).Where(e => e.ItemId == itemId && e.IsHQ == hq).Sum(e => e.Quantity);
            if (saddle > 0) list.Add(new RestockSource(prefix + ".saddle", null, saddle));
        }
        return list;
    }

    public int PerListing(LowStockEntry e)
    {
        if (e.PerListing > 0) return e.PerListing;
        var qty = tracker.ActiveFor(e.ItemId, e.IsHq).GroupBy(r => r.Quantity).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        return qty > 0 ? qty : 1;
    }

    /// <summary>Entry price, else our current listing price, else market lowest - undercut, else 0.</summary>
    public long Price(LowStockEntry e)
    {
        if (e.Price > 0) return e.Price;
        var ours = tracker.ActiveFor(e.ItemId, e.IsHq).Select(r => r.LastPrice).Where(p => p > 0).DefaultIfEmpty(0).Min();
        if (ours > 0) return ours;
        var lowest = marketCache.GetLowest(e.ItemId, e.IsHq);
        return lowest > 0 ? Math.Max(1, lowest - configuration.UndercutGil) : 0;
    }

    public List<PlannedAction> PlanRestock(LowStockEntry e, int targetUnits)
    {
        var free = MyRetainers().ToDictionary(s => s.Key, s => Math.Max(0, Planner.MaxListingSlots - s.Listings.Count));
        return RestockPlanner.Plan(e.ItemId, e.IsHq, targetUnits, ListedUnits(e.ItemId, e.IsHq),
            PerListing(e), Price(e), Sources(e.ItemId, e.IsHq), free);
    }

    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextCheckUtc) return;
        nextCheckUtc = now + TimeSpan.FromSeconds(5);
        if (Cid() == 0 || configuration.LowStock.Count == 0) return;

        foreach (var e in configuration.LowStock)
        {
            var key = (e.ItemId, e.IsHq);
            var listed = ListedUnits(e.ItemId, e.IsHq);
            var flag = notified.TryGetValue(key, out var f) && f;
            var fire = RestockPlanner.ShouldNotify(listed, e.Threshold, ref flag);
            notified[key] = flag;
            if (!fire) continue;

            var name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(e.ItemId, out var row)
                ? row.Name.ExtractText() : $"#{e.ItemId}";
            var avail = Sources(e.ItemId, e.IsHq).Sum(s => s.Available);
            Plugin.ChatPrint($"low stock: {name}{(e.IsHq ? " (HQ)" : "")}: {listed} listed (alert at ≤{e.Threshold}), {avail} available in retainers / bag / saddlebag. Restocky › Low stock to restock.");
        }
    }
}
