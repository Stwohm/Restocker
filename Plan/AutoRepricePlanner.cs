using System;
using System.Collections.Generic;
using Restocker.Data;
using Restocker.Market;

namespace Restocker.Plan;

/// <summary>
/// Turns one retainer's live listings + freshly fetched competitor prices into
/// Reprice actions (to apply) and history rows for everything not applied.
/// Pure logic; the executor supplies live data and writes the rows.
/// </summary>
public static class AutoRepricePlanner
{
    public sealed class Result
    {
        public List<PlannedAction> Actions { get; } = new();

        /// <summary>History rows for listings that will NOT be changed (applied=false).</summary>
        public List<PriceHistoryRow> NotApplied { get; } = new();
    }

    /// <param name="competitors">
    /// Competitor prices (ascending, own retainers excluded) fetched in THIS cycle for (item, hq),
    /// or null when no fresh data exists for it.
    /// </param>
    public static Result Plan(
        string retainerKey,
        string retainerName,
        IReadOnlyList<LiveListing> listings,
        Func<uint, bool, IReadOnlyList<MarketOffer>?> competitors,
        Func<uint, long> floorFor,
        RepriceSettings settings,
        DateTime nowUtc)
    {
        var result = new Result();
        foreach (var l in listings)
        {
            var offers = competitors(l.ItemId, l.IsHQ);
            RepriceDecision d;
            var undercutters = string.Empty;
            if (offers == null)
            {
                d = new RepriceDecision(l.UnitPrice, false, RepriceReason.Error, 0);
            }
            else
            {
                var prices = new List<long>(offers.Count);
                foreach (var o in offers) prices.Add(o.Price);
                prices.Sort();
                d = RepriceRules.Decide(l.UnitPrice, prices, floorFor(l.ItemId), settings);
                undercutters = MarketOfferFormat.Undercutters(offers, l.UnitPrice);
            }

            if (d.Apply)
            {
                result.Actions.Add(new PlannedAction
                {
                    Kind = PlannedActionKind.Reprice,
                    RetainerKey = retainerKey,
                    ItemId = l.ItemId,
                    IsHQ = l.IsHQ,
                    Quantity = l.Quantity,
                    UnitPrice = d.NewPrice,
                    ListingIndex = l.Slot,
                    OldPrice = l.UnitPrice,
                    LowestCompetitor = d.LowestCompetitor,
                    Reason = d.Reason,
                    Undercutters = undercutters,
                });
            }
            else
            {
                result.NotApplied.Add(new PriceHistoryRow
                {
                    UtcTime = nowUtc,
                    Retainer = retainerName,
                    Slot = l.Slot,
                    ItemId = l.ItemId,
                    Hq = l.IsHQ,
                    Qty = l.Quantity,
                    OldPrice = l.UnitPrice,
                    LowestCompetitor = d.LowestCompetitor,
                    NewPrice = d.NewPrice,
                    Applied = false,
                    Reason = d.Reason,
                    Undercutters = undercutters,
                });
            }
        }
        return result;
    }
}
