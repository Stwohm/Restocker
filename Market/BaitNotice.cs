using System.Collections.Generic;
using System.Linq;

namespace Restocker.Market;

/// <summary>Competitor listings the outlier filter ignored for one of our listings.</summary>
public sealed record BaitNotice(
    uint ItemId,
    bool IsHq,
    string Retainer,
    IReadOnlyList<MarketOffer> Baits,
    long RealLowest);

public static class BaitFinder
{
    /// <summary>
    /// Offers cheaper than the price the rules actually used (<paramref name="pickedLowest"/>),
    /// i.e. the ones dropped as bait. Empty when nothing was dropped.
    /// </summary>
    public static IReadOnlyList<MarketOffer> Dropped(IEnumerable<MarketOffer> offers, long pickedLowest)
        => pickedLowest <= 0
            ? new List<MarketOffer>()
            : offers.Where(o => o.Price > 0 && o.Price < pickedLowest).OrderBy(o => o.Price).ToList();

    /// <summary>"Lemonette: bait listing skipped — Meowsugar 190x @ 520 (used 944)".</summary>
    public static string Format(string itemName, BaitNotice n)
    {
        var parts = n.Baits.Select(b => $"{(string.IsNullOrEmpty(b.Seller) ? "?" : b.Seller)} {b.Quantity}x @ {b.Price:N0}");
        var what = n.Baits.Count == 1 ? "bait listing skipped" : $"{n.Baits.Count} bait listings skipped";
        return $"{itemName}{(n.IsHq ? " (HQ)" : "")} [{n.Retainer}]: {what}: {string.Join(", ", parts)} (priced against {n.RealLowest:N0})";
    }

    /// <summary>Session de-duplication key: same seller, item and price is reported once.</summary>
    public static string Key(BaitNotice n) =>
        $"{n.ItemId}:{n.IsHq}:" + string.Join("|", n.Baits.Select(b => $"{b.Seller}@{b.Price}x{b.Quantity}"));
}
