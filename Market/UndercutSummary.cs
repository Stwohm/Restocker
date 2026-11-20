using System.Collections.Generic;
using System.Linq;

namespace Restocker.Market;

// Fork: summarize individual market listings for the Reprice tooltip.
public readonly record struct UndercutSummary(IReadOnlyList<MarketOffer> Offers, int ListingCount, long UnitCount)
{
    public int RemainingCount => ListingCount - Offers.Count;

    public static UndercutSummary Create(IEnumerable<MarketOffer> offers, long ourPrice)
    {
        var priceUnknown = ourPrice == 0;
        var matching = offers.Where(o => priceUnknown || o.Price < ourPrice).ToList();
        var visible = matching.OrderBy(o => o.Price).Take(priceUnknown ? 10 : 15).ToArray();
        return new UndercutSummary(visible, matching.Count, matching.Sum(o => (long)o.Quantity));
    }
}
