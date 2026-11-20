using System.Linq;
using Restocker.Market;

namespace Restocker.Tests;

// Fork: verify the listing-level summary shown in the Reprice tooltip.
public class UndercutSummaryTests
{
    [Fact]
    public void Only_strictly_cheaper_offers_are_included()
    {
        var cheaper = new MarketOffer(99, "Cheaper", 2);
        var offers = new[] { new MarketOffer(100, "Equal", 3), new MarketOffer(101, "Higher", 4), cheaper };

        var summary = UndercutSummary.Create(offers, 100);

        Assert.Equal(cheaper, Assert.Single(summary.Offers));
        Assert.Equal(1, summary.ListingCount);
        Assert.Equal(2, summary.UnitCount);
    }

    [Fact]
    public void Listings_stay_separate_and_are_ordered_cheapest_first()
    {
        var first = new MarketOffer(80, "Rival", 2);
        var second = new MarketOffer(85, "Bob", 4);
        var third = new MarketOffer(90, "Rival", 1);

        var summary = UndercutSummary.Create(new[] { third, first, second }, 100);

        Assert.Equal(new[] { first, second, third }, summary.Offers);
        Assert.Equal(3, summary.ListingCount);
        Assert.Equal(7, summary.UnitCount);
        Assert.Equal(0, summary.RemainingCount);
    }

    [Fact]
    public void Known_price_shows_fifteen_rows_and_totals_include_hidden_listings()
    {
        var offers = Enumerable.Range(1, 20).Reverse().Select(i => new MarketOffer(i, "Rival", i));

        var summary = UndercutSummary.Create(offers, 19);

        Assert.Equal(Enumerable.Range(1, 15).Select(i => (long)i), summary.Offers.Select(o => o.Price));
        Assert.Equal(18, summary.ListingCount);
        Assert.Equal(3, summary.RemainingCount);
        Assert.Equal(171, summary.UnitCount);
    }

    [Fact]
    public void Unknown_price_includes_all_offers_and_shows_the_cheapest_ten()
    {
        var offers = Enumerable.Range(1, 12).Reverse().Select(i => new MarketOffer(i, "Rival", 2));

        var summary = UndercutSummary.Create(offers, 0);

        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), summary.Offers.Select(o => o.Price));
        Assert.Equal(12, summary.ListingCount);
        Assert.Equal(2, summary.RemainingCount);
        Assert.Equal(24, summary.UnitCount);
    }
}
