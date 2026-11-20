using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Restocker.Data;
using Restocker.Market;

namespace Restocker.Tests;

public class ListingTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string RetainerA = "RetainerA";
    private const string K = "k1";
    private static string Name(uint id) => id == 100 ? "Twine" : $"#{id}";

    private static List<LiveListing> L(params LiveListing[] l) => l.ToList();

    [Fact]
    public void Full_lifecycle_first_price_reprices_undercutters_and_sale()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 85_000)), Name, T0);
        var rec = Assert.Single(t.Records);
        Assert.Equal(85_000, rec.FirstPrice);
        Assert.Equal("Twine", rec.ItemName);

        // Rival undercuts, Restocky fixes it twice.
        t.ObserveCompetitors(100, false, new[] { new MarketOffer(84_999, "Rival", 1), new MarketOffer(90_000, "Bob", 1) }, T0.AddMinutes(10));
        t.OnRepriceApplied(K, 0, 100, false, 84_998);
        t.ObserveCompetitors(100, false, new[] { new MarketOffer(84_997, "Rival", 1) }, T0.AddMinutes(20));
        t.OnRepriceApplied(K, 0, 100, false, 84_996);
        // A later sell-list read sees the same price: no extra reprice counted.
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 84_996)), Name, T0.AddMinutes(21));

        var sold = t.OnSale(100, false, 80_746, T0.AddDays(2));
        Assert.Same(rec, sold);
        Assert.Equal(2, rec.RepriceCount);
        Assert.Equal(2, rec.UndercutCount);
        Assert.Equal(2, rec.Competitors["Rival"].TimesBelowUs);
        Assert.Equal(0, rec.Competitors["Bob"].TimesBelowUs);
        Assert.Equal(84_997, rec.Competitors["Rival"].LowestPrice);
        Assert.Equal(ListingTracker.Sold, rec.Outcome);
        Assert.Equal(2.0, rec.DaysListed!.Value, 3);
        Assert.Equal(84_996, rec.LastPrice);
    }

    [Fact]
    public void Seller_with_several_listings_counts_once_with_summed_units()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 85_000)), Name, T0);
        t.ObserveCompetitors(100, false, new[] { new MarketOffer(84_999, "Rival", 3), new MarketOffer(84_998, "Rival", 1) }, T0);
        var st = t.Records[0].Competitors["Rival"];
        Assert.Equal(1, st.TimesSeen);
        Assert.Equal(4, st.MaxQuantity);
        Assert.Equal(84_998, st.LowestPrice);
    }

    [Fact]
    public void Manual_price_change_in_game_counts_as_reprice()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(3, 100, false, 1, 50_000)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(3, 100, false, 1, 49_000)), Name, T0.AddHours(1));
        Assert.Equal(1, t.Records[0].RepriceCount);
        Assert.Equal(50_000, t.Records[0].FirstPrice);
    }

    [Fact]
    public void Missing_listing_closes_as_gone_only_after_grace_period()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 1_000)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(), Name, T0.AddSeconds(5));
        Assert.Equal(ListingTracker.Active, t.Records[0].Outcome);
        t.ObserveRetainer(K, RetainerA, L(), Name, T0.AddMinutes(10));
        Assert.Equal(ListingTracker.Gone, t.Records[0].Outcome);
        Assert.Equal(T0.AddSeconds(5), t.Records[0].EndedUtc);
    }

    [Fact]
    public void Listing_missing_from_a_non_empty_read_closes_immediately()
    {
        // Placeholder twine on slot 1, other listings remain: one read later it is gone.
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 7, false, 1, 10), new LiveListing(1, 100, false, 1, 99_999_999)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 7, false, 1, 10)), Name, T0.AddSeconds(15));
        Assert.Equal(ListingTracker.Gone, t.Records.Single(r => r.ItemId == 100).Outcome);
    }

    [Fact]
    public void Authoritative_empty_read_closes_immediately()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(1, 100, false, 1, 10)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(), Name, T0.AddSeconds(15), authoritative: true);
        Assert.Equal(ListingTracker.Gone, t.Records[0].Outcome);
    }

    [Fact]
    public void Transient_empty_read_does_not_break_the_lifecycle()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 1_000)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(), Name, T0.AddSeconds(1));
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 1_000)), Name, T0.AddMinutes(5));
        var rec = Assert.Single(t.Records);
        Assert.Equal(ListingTracker.Active, rec.Outcome);
        Assert.Null(rec.MissingSinceUtc);
    }

    [Fact]
    public void Moved_slot_keeps_the_same_record()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(4, 100, false, 1, 1_000)), Name, T0);
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(1, 100, false, 1, 1_000)), Name, T0.AddMinutes(1));
        Assert.Single(t.Records);
        Assert.Equal(1, t.Records[0].Slot);
    }

    [Fact]
    public void Sale_picks_the_stack_whose_value_matches_the_gil()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 7, false, 1, 5_500), new LiveListing(1, 7, false, 5, 5_500)), Name, T0);
        var rec = t.OnSale(7, false, 26_125, T0.AddHours(3)); // 5 x 5,500 x 0.95
        Assert.Equal(5, rec!.Quantity);
    }

    [Fact]
    public void Competitors_on_another_retainers_listing_and_hq_are_separate()
    {
        var t = new ListingTracker();
        t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, true, 1, 1_000)), Name, T0);
        t.ObserveCompetitors(100, false, new[] { new MarketOffer(10, "NqGuy", 1) }, T0);
        Assert.Empty(t.Records[0].Competitors);
    }

    [Fact]
    public void Save_and_load_roundtrip_and_csv_headers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "restocky-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t = new ListingTracker();
            t.ObserveRetainer(K, RetainerA, L(new LiveListing(0, 100, false, 1, 85_000)), Name, T0);
            t.ObserveCompetitors(100, false, new[] { new MarketOffer(84_999, "Rival", 1) }, T0);
            Assert.True(t.Save(dir));
            Assert.False(t.Dirty);

            var back = ListingTracker.Load(Path.Combine(dir, "listings.json"));
            Assert.Equal("Rival", back.Records[0].Competitors.Keys.Single());

            var listings = File.ReadAllLines(Path.Combine(dir, "listings.csv"));
            Assert.Equal(ListingTracker.ListingsHeader, listings[0].TrimStart('﻿'));
            Assert.Contains(",Rival 1x,Rival 1x,", listings[1]);
            var comps = File.ReadAllLines(Path.Combine(dir, "competitors.csv"));
            Assert.StartsWith("100,Twine,false,Rival,1,1,84999,1,1,", comps[1]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("The Twine you put up for sale in the Limsa Lominsa markets has sold for 80,746 gil (after fees).", 80_746)]
    [InlineData("リテイナーに預けたTwineが売れました！　80,746ギルを受け取りました。", 80_746)]
    [InlineData("Dein Twine wurde für 80.746 Gil verkauft.", 80_746)]
    [InlineData("no amount here", 0)]
    public void Sale_message_gil_is_parsed(string text, long expected)
        => Assert.Equal(expected, SaleMessage.ParseGil(text));
}
