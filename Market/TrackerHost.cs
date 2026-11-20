using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace Restocker.Market;

/// <summary>
/// Fork: owns the <see cref="ListingTracker"/>, feeds it the RetainerSale chat message and
/// saves it (debounced) to pluginConfigs\Restocky\ (listings.json / listings.csv / competitors.csv).
/// </summary>
public sealed class TrackerHost : IDisposable
{
    private readonly IFramework framework;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly string dir;
    private DateTime nextSaveUtc = DateTime.MinValue;

    public ListingTracker Tracker { get; }

    public TrackerHost(IFramework framework, IChatGui chat, IDataManager data, IPluginLog log, string dir)
    {
        this.framework = framework;
        this.chat = chat;
        this.log = log;
        this.dir = dir;
        Tracker = ListingTracker.Load(System.IO.Path.Combine(dir, "listings.json"));
        framework.Update += OnUpdate;
        ChatHook.Subscribe(chat, data, OnGameSaleMessage, OnAllaganSaleMessage);
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        ChatHook.Unsubscribe(chat);
        if (Tracker.Dirty) Tracker.Save(dir);
    }

    private void OnUpdate(IFramework _)
    {
        if (!Tracker.Dirty) return;
        var now = DateTime.UtcNow;
        if (now < nextSaveUtc) return;
        nextSaveUtc = now + TimeSpan.FromSeconds(5);
        if (!Tracker.Save(dir)) log.Warning("[Restocker] could not write listings.json / csv");
    }

    /// <summary>Game RetainerSale message: item/quality from the payload, net gil from the text.</summary>
    private void OnGameSaleMessage(uint itemId, bool isHq, string text)
    {
        var gil = SaleMessage.ParseGil(text);
        var rec = Tracker.OnSale(itemId, isHq, gil, DateTime.UtcNow);
        if (rec == null)
            log.Information($"[Restocker] sale message source=game item={itemId} hq={isHq} gil={gil}: no tracked listing");
        else
            LogSold(rec, gil, "game");
    }

    private void OnAllaganSaleMessage(IReadOnlyList<uint> itemIds, int qty, string itemName, long grossGil)
    {
        var netGil = ListingTracker.NetFromGross(grossGil);
        var rec = Tracker.OnAllaganSale(itemIds, qty, netGil, DateTime.UtcNow);
        if (rec == null)
            log.Information($"[Restocker] sale message source=allaganmarket item='{itemName}' ids={string.Join("/", itemIds)} qty={qty} gross={grossGil} net={netGil}: no tracked listing");
        else
        {
            rec.SoldGrossGil = grossGil;
            LogSold(rec, netGil, "allaganmarket");
        }
    }

    private void LogSold(ListingRecord rec, long gil, string source)
    {
        log.Information($"[Restocker] sold: {rec.ItemName} x{rec.Quantity} on {rec.RetainerName} for {gil:N0} gil after {rec.DaysListed:0.0} days, {rec.RepriceCount} reprices, undercut {rec.UndercutCount}x by {string.Join(", ", rec.Competitors.Values.Where(c => c.TimesBelowUs > 0).Select(c => c.Seller))} (source={source})");
    }
}
