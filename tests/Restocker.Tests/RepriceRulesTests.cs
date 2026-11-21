using System.Collections.Generic;
using System.IO;
using Restocker.Data;
using Restocker.Market;
using Restocker.Plan;

namespace Restocker.Tests;

public class RepriceRulesTests
{
    private static readonly RepriceSettings Default = new();

    [Fact]
    public void Undercuts_lowest_competitor_by_one()
    {
        var d = RepriceRules.Decide(80_000, new long[] { 75_000, 76_000 }, 0, Default);
        Assert.True(d.Apply);
        Assert.Equal(74_999, d.NewPrice);
        Assert.Equal(RepriceReason.Undercut, d.Reason);
        Assert.Equal(75_000, d.LowestCompetitor);
    }

    [Fact]
    public void Raises_when_we_are_far_below_the_market()
    {
        var d = RepriceRules.Decide(50_000, new long[] { 75_000 }, 0, Default);
        Assert.True(d.Apply);
        Assert.Equal(74_999, d.NewPrice);
    }

    [Fact]
    public void Already_at_target_is_no_change()
    {
        var d = RepriceRules.Decide(74_999, new long[] { 75_000 }, 0, Default);
        Assert.False(d.Apply);
        Assert.Equal(RepriceReason.NoChange, d.Reason);
    }

    [Fact]
    public void No_competitor_keeps_price()
    {
        var d = RepriceRules.Decide(10_000, new long[0], 0, Default);
        Assert.False(d.Apply);
        Assert.Equal(RepriceReason.NoCompetitor, d.Reason);
    }

    [Fact]
    public void Neotunic_single_bait_listing_is_refused_by_max_drop()
    {
        // 2026-11-18: one 11,111 listing, real price ~75K. Only one competitor, so the
        // outlier filter cannot help; the max-drop guard must stop it.
        var d = RepriceRules.Decide(75_000, new long[] { 11_111 }, 0, Default);
        Assert.False(d.Apply);
        Assert.Equal(RepriceReason.MaxDrop, d.Reason);
        Assert.Equal(11_110, d.NewPrice);
    }

    [Fact]
    public void Bait_listing_with_real_cluster_behind_it_is_ignored()
    {
        var d = RepriceRules.Decide(78_000, new long[] { 11_111, 75_000, 75_500, 76_000 }, 0, Default);
        Assert.True(d.Apply);
        Assert.Equal(74_999, d.NewPrice);
        Assert.Equal(RepriceReason.Outlier, d.Reason);
    }

    [Fact]
    public void Floor_clamps_the_target()
    {
        var d = RepriceRules.Decide(60_000, new long[] { 50_000, 51_000 }, floor: 55_000, Default);
        Assert.True(d.Apply);
        Assert.Equal(55_000, d.NewPrice);
        Assert.Equal(RepriceReason.Floor, d.Reason);
    }

    [Fact]
    public void Floor_equal_to_current_is_no_change()
    {
        var d = RepriceRules.Decide(55_000, new long[] { 50_000 }, floor: 55_000, Default);
        Assert.False(d.Apply);
        Assert.Equal(RepriceReason.NoChange, d.Reason);
    }

    [Fact]
    public void Max_drop_zero_disables_the_guard()
    {
        var s = new RepriceSettings { MaxDropPercent = 0 };
        var d = RepriceRules.Decide(75_000, new long[] { 11_111 }, 0, s);
        Assert.True(d.Apply);
    }

    [Fact]
    public void Drop_exactly_at_limit_is_allowed()
    {
        // 100 -> 70 is exactly 30%.
        var d = RepriceRules.Decide(100, new long[] { 71 }, 0, Default);
        Assert.True(d.Apply);
        Assert.Equal(70, d.NewPrice);
    }

    [Fact]
    public void Price_never_goes_below_one()
    {
        var s = new RepriceSettings { MaxDropPercent = 0, UndercutGil = 5 };
        var d = RepriceRules.Decide(10, new long[] { 2 }, 0, s);
        Assert.Equal(1, d.NewPrice);
    }

    [Fact]
    public void Planner_splits_applied_and_not_applied_and_compares_hq_separately()
    {
        var listings = new List<LiveListing>
        {
            new(0, 100, false, 99, 80_000),   // NQ, undercut to 74,999
            new(1, 100, true, 10, 120_000),   // HQ, no HQ competitor
            new(2, 200, false, 5, 1_000),     // no fresh data
        };
        IReadOnlyList<MarketOffer>? Comp(uint item, bool hq) => (item, hq) switch
        {
            (100, false) => new[] { new MarketOffer(75_000, "Rival", 1), new MarketOffer(90_000, "Bob", 1) },
            (100, true) => new MarketOffer[0],
            _ => null,
        };
        var r = AutoRepricePlanner.Plan("k", "RetainerA", listings, Comp, _ => 0, Default, new System.DateTime(2026, 11, 20));

        var a = Assert.Single(r.Actions);
        Assert.Equal(0, a.ListingIndex);
        Assert.Equal(74_999, a.UnitPrice);
        Assert.Equal(80_000, a.OldPrice);
        Assert.Equal("Rival 1x", a.Undercutters);
        Assert.Equal(2, r.NotApplied.Count);
        Assert.Equal(RepriceReason.NoCompetitor, r.NotApplied[0].Reason);
        Assert.Equal(RepriceReason.Error, r.NotApplied[1].Reason);
        Assert.False(r.NotApplied[1].Applied);
        Assert.Equal(r.NotApplied[1].OldPrice, r.NotApplied[1].NewPrice);
    }

    [Fact]
    public void Auto_planner_held_listing_has_no_action_and_writes_held_history()
    {
        var listings = new List<LiveListing> { new(3, 100, true, 1, 99_999) };
        IReadOnlyList<MarketOffer>? Comp(uint item, bool hq) => new[] { new MarketOffer(120_000, "Rival", 1) };

        var r = AutoRepricePlanner.Plan("k", "RetainerA", listings, Comp, _ => 0, Default,
            new System.DateTime(2026, 11, 20), (item, hq) => item == 100 && hq);

        Assert.Empty(r.Actions);
        var row = Assert.Single(r.NotApplied);
        Assert.False(row.Applied);
        Assert.Equal(RepriceReason.Held, row.Reason);
        Assert.Equal(99_999, row.NewPrice);
        Assert.Equal(1, r.HeldCount);
        Assert.EndsWith(",false,held,", PriceHistoryLog.Format(row));
    }

    [Fact]
    public void Auto_planner_hold_is_independent_for_hq_and_nq()
    {
        var listings = new List<LiveListing>
        {
            new(0, 100, false, 1, 100_000),
            new(1, 100, true, 1, 100_000),
        };
        IReadOnlyList<MarketOffer>? Comp(uint item, bool hq) => new[] { new MarketOffer(90_000, "Rival", 1) };

        var r = AutoRepricePlanner.Plan("k", "RetainerA", listings, Comp, _ => 0, Default,
            new System.DateTime(2026, 11, 20), (item, hq) => item == 100 && hq);

        Assert.Single(r.Actions);
        Assert.False(r.Actions[0].IsHQ);
        Assert.Equal(RepriceReason.Held, Assert.Single(r.NotApplied).Reason);
    }

    [Fact]
    public void History_row_format_matches_header()
    {
        var row = new PriceHistoryRow
        {
            UtcTime = new System.DateTime(2026, 11, 20, 18, 5, 0, System.DateTimeKind.Utc),
            Retainer = "RetainerA", Slot = 3, ItemId = 44_000, Hq = true, Qty = 5,
            OldPrice = 80_000, LowestCompetitor = 75_000, NewPrice = 74_999, Applied = true,
            Reason = RepriceReason.Undercut,
        };
        Assert.Equal("2026-11-20T18:05:00Z,RetainerA,3,44000,true,5,80000,75000,74999,true,undercut,",
            PriceHistoryLog.Format(row));
        Assert.Equal(12, PriceHistoryLog.Header.Split(',').Length);
    }

    [Fact]
    public void Undercutters_sum_quantity_per_seller_cheapest_first()
    {
        var offers = new[]
        {
            new MarketOffer(84_999, "Rival", 3),
            new MarketOffer(84_990, "Bob", 1),
            new MarketOffer(84_998, "Rival", 1),
            new MarketOffer(90_000, "Expensive", 9),
        };
        Assert.Equal("Bob 1x;Rival 4x", MarketOfferFormat.Undercutters(offers, 85_000));
        Assert.Equal(string.Empty, MarketOfferFormat.Undercutters(offers, 1_000));
    }

    [Fact]
    public void Lemonette_bait_is_reported_with_seller_quantity_and_price()
    {
        var listings = new List<LiveListing> { new(9, 27835, false, 99, 943) };
        IReadOnlyList<MarketOffer>? Comp(uint item, bool hq) => new[]
        {
            new MarketOffer(520, "Meowsugar", 190),
            new MarketOffer(944, "Arthurrr", 128),
            new MarketOffer(945, "Izzywizzy", 10),
            new MarketOffer(997, "Mineralprincess", 198),
        };
        var r = AutoRepricePlanner.Plan("k", "RetainerB", listings, Comp, _ => 0, Default, new System.DateTime(2026, 11, 20));
        var bait = Assert.Single(r.Baits);
        Assert.Equal(944, bait.RealLowest);
        Assert.Equal("Meowsugar", Assert.Single(bait.Baits).Seller);
        Assert.Equal("Lemonette [RetainerB]: bait listing skipped: Meowsugar 190x @ 520 (priced against 944)",
            BaitFinder.Format("Lemonette", bait));
    }

    [Fact]
    public void No_bait_notice_when_lowest_is_used()
    {
        var listings = new List<LiveListing> { new(0, 1, false, 1, 1_000) };
        IReadOnlyList<MarketOffer>? Comp(uint item, bool hq) => new[] { new MarketOffer(900, "A", 1), new MarketOffer(950, "B", 1) };
        var r = AutoRepricePlanner.Plan("k", "R", listings, Comp, _ => 0, Default, new System.DateTime(2026, 11, 20));
        Assert.Empty(r.Baits);
    }

    [Fact]
    public void History_append_writes_header_once()
    {
        var path = Path.Combine(Path.GetTempPath(), "restocky-test-" + System.Guid.NewGuid() + ".csv");
        try
        {
            var log = new PriceHistoryLog(path);
            var row = new PriceHistoryRow { Retainer = "RetainerB", Reason = RepriceReason.MaxDrop };
            Assert.True(log.Append(row));
            Assert.True(log.Append(row));
            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Equal(PriceHistoryLog.Header, lines[0]);
            Assert.EndsWith(",false,max_drop,", lines[2]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Cache_fresh_check_uses_update_time()
    {
        var now = new System.DateTime(2026, 11, 20, 12, 0, 0);
        var c = new MarketCache { Clock = () => now };
        c.Replace(1, false, new long[] { 300, 100, 200 });
        Assert.True(c.HasDataSince(1, false, now));
        Assert.False(c.HasDataSince(1, false, now.AddSeconds(1)));
        Assert.Equal(new long[] { 100, 200, 300 }, c.GetRaw(1, false));
    }
}
