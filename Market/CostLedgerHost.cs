using System;
using System.IO;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;

namespace Restocker.Market;

/// <summary>Fork: captures confirmed purchases and persists the cost ledger.</summary>
internal sealed class CostLedgerHost : IDisposable
{
    private readonly IMarketBoard marketBoard;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly string path;
    private readonly object purchaseGate = new();
    private readonly PurchasePairing pairing;
    private DateTime nextSaveUtc;
    private bool disposed;

    public CostLedger Ledger { get; }

    public CostLedgerHost(IMarketBoard marketBoard, IFramework framework, IPluginLog log, string configDirectory)
    {
        this.marketBoard = marketBoard;
        this.framework = framework;
        this.log = log;
        path = Path.Combine(configDirectory, CostLedger.FileName);
        Ledger = CostLedger.Load(path, message => log.Warning(message));
        pairing = new PurchasePairing(Ledger);

        marketBoard.PurchaseRequested += OnPurchaseRequested;
        marketBoard.ItemPurchased += OnItemPurchased;
        framework.Update += OnUpdate;
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler request)
    {
        lock (purchaseGate)
        {
            if (disposed) return;
            pairing.Request(request.CatalogId, request.IsHq, request.ItemQuantity, request.PricePerUnit, request.TotalTax, DateTime.UtcNow);
        }
    }

    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        lock (purchaseGate)
        {
            if (disposed) return;
            pairing.Confirm(purchase.CatalogId, purchase.ItemQuantity, DateTime.UtcNow);
        }
    }

    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextSaveUtc) return;
        nextSaveUtc = now.AddSeconds(5);
        Save();
    }

    private void Save()
    {
        try { Ledger.SaveIfDirty(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning(ex, "[Restocker] Could not write cost-ledger.json.");
        }
    }

    public void Dispose()
    {
        marketBoard.PurchaseRequested -= OnPurchaseRequested;
        marketBoard.ItemPurchased -= OnItemPurchased;
        framework.Update -= OnUpdate;
        lock (purchaseGate) disposed = true;
        Save();
    }
}
