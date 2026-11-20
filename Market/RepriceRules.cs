using System;
using System.Collections.Generic;

namespace Restocker.Market;

/// <summary>Why a listing got (or did not get) a new price. Written to price-history.csv.</summary>
public enum RepriceReason
{
    /// <summary>Lowest competitor - undercut.</summary>
    Undercut,

    /// <summary>Like Undercut, but one or two bait listings at the bottom of the board were ignored.</summary>
    Outlier,

    /// <summary>Target was below the per-item floor; clamped to the floor.</summary>
    Floor,

    /// <summary>Target dropped more than the allowed percentage; not applied.</summary>
    MaxDrop,

    /// <summary>Already at the target price.</summary>
    NoChange,

    /// <summary>No competitor listing on the board (only own retainers, or none).</summary>
    NoCompetitor,

    /// <summary>Market data missing / slot mismatch / game call failed.</summary>
    Error,

    /// <summary>Applied from the manual plan-then-apply table.</summary>
    Manual,
}

public sealed class RepriceSettings
{
    public int UndercutGil { get; init; } = 1;

    /// <summary>Skip (do not apply) when the new price is more than this % below the current one. 0 disables.</summary>
    public double MaxDropPercent { get; init; } = 30;

    public int OutlierMaxGroupSize { get; init; } = 2;
    public double OutlierGapRatio { get; init; } = 1.5;
}

public readonly record struct RepriceDecision(
    long NewPrice,
    bool Apply,
    RepriceReason Reason,
    long LowestCompetitor);

/// <summary>
/// Pure price rule for the auto / full-cycle path:
/// lowest competitor (outliers ignored) - undercut, clamped to the item floor,
/// refused when it would drop the price by more than <see cref="RepriceSettings.MaxDropPercent"/>.
/// HQ and NQ are compared separately by the caller (it passes the matching list).
/// </summary>
public static class RepriceRules
{
    public static string ReasonCode(RepriceReason r) => r switch
    {
        RepriceReason.Undercut => "undercut",
        RepriceReason.Outlier => "outlier",
        RepriceReason.Floor => "floor",
        RepriceReason.MaxDrop => "max_drop",
        RepriceReason.NoChange => "no_change",
        RepriceReason.NoCompetitor => "no_competitor",
        RepriceReason.Error => "error",
        RepriceReason.Manual => "manual",
        _ => r.ToString().ToLowerInvariant(),
    };

    /// <param name="currentPrice">Our current asking price for this listing.</param>
    /// <param name="competitorPricesAscending">Other sellers' unit prices, same HQ flag, ascending. Own retainers excluded.</param>
    /// <param name="floor">Per-item minimum price. 0 = none.</param>
    public static RepriceDecision Decide(
        long currentPrice,
        IReadOnlyList<long> competitorPricesAscending,
        long floor,
        RepriceSettings settings)
    {
        if (competitorPricesAscending.Count == 0)
            return new RepriceDecision(currentPrice, false, RepriceReason.NoCompetitor, 0);

        var raw = competitorPricesAscending[0];
        var picked = LowestPriceResolver.Pick(
            competitorPricesAscending, settings.OutlierMaxGroupSize, settings.OutlierGapRatio);
        if (picked <= 0)
            return new RepriceDecision(currentPrice, false, RepriceReason.Error, 0);

        var reason = picked != raw ? RepriceReason.Outlier : RepriceReason.Undercut;
        var target = Math.Max(1, picked - Math.Max(0, settings.UndercutGil));

        if (floor > 0 && target < floor)
        {
            target = floor;
            reason = RepriceReason.Floor;
        }

        if (target == currentPrice)
            return new RepriceDecision(target, false, RepriceReason.NoChange, picked);

        if (settings.MaxDropPercent > 0 && currentPrice > 0 && target < currentPrice)
        {
            var dropPercent = (currentPrice - target) * 100.0 / currentPrice;
            if (dropPercent > settings.MaxDropPercent)
                return new RepriceDecision(target, false, RepriceReason.MaxDrop, picked);
        }

        return new RepriceDecision(target, true, reason, picked);
    }
}
