using System;
using System.Collections.Generic;

namespace Restocker.Market;

/// <summary>Fork: pairs market purchase events without depending on Dalamud.</summary>
public sealed class PurchasePairing(CostLedger ledger)
{
    private const uint HqCatalogOffset = 1_000_000;
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromSeconds(30);
    private readonly List<PendingPurchase> pending = new();

    public void Request(uint itemId, bool hq, uint quantity, uint pricePerUnit, uint totalTax, DateTime utc)
    {
        Expire(utc);
        if (itemId == 0 || quantity == 0 || quantity > int.MaxValue) return;

        // Fork: TotalTax is the buyer's tax for the entire stack. Prorate it per unit without rounding to gil.
        var unitCost = pricePerUnit + (decimal)totalTax / quantity;
        pending.Add(new PendingPurchase(itemId, hq, quantity, unitCost, utc));
    }

    public CostLot? Confirm(uint catalogId, uint quantity, DateTime utc)
    {
        Expire(utc);
        // Fork: Dalamud's own pairing accepts the base catalog ID or that ID plus 1,000,000 for HQ.
        // Take the oldest request matching the response's item and quantity.
        var index = pending.FindIndex(request => request.Quantity == quantity && request.Utc <= utc
            && (request.ItemId == catalogId
                || (request.IsHq && catalogId >= HqCatalogOffset && request.ItemId == catalogId - HqCatalogOffset)));
        if (index < 0) return null;

        var match = pending[index];
        pending.RemoveAt(index);
        return ledger.RecordLot(match.ItemId, match.IsHq, (int)match.Quantity, match.UnitCost, utc);
    }

    private void Expire(DateTime utc) => pending.RemoveAll(request => utc - request.Utc >= RequestLifetime);

    private sealed record PendingPurchase(uint ItemId, bool IsHq, uint Quantity, decimal UnitCost, DateTime Utc);
}
