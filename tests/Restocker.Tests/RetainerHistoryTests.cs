using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Restocker.Common;
using Restocker.Market;
using Restocker.Data;

namespace Restocker.Tests;

// Fork: history reads can repeat across visits and plugin restarts.
public class RetainerHistoryTests
{
    private const uint Time = 1_700_000_000;
    private static readonly DateTime SoldAt = DateTimeOffset.FromUnixTimeSeconds(Time).UtcDateTime;
    private static readonly RetainerHistorySale Sale = new(100, 190, Time, 2, true, false, "BuyerA");

    [Fact]
    public void Packet_uses_header_stride_offsets_terminator_and_record_limit()
    {
        var packet = new byte[8 + 22 * 52];
        for (var i = 0; i < 22; i++)
        {
            var row = packet.AsSpan(8 + i * 52, 52);
            BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)(100 + i));
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], 190);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], Time);
            BinaryPrimitives.WriteUInt32LittleEndian(row[12..], 2);
            row[16] = 1;
            row[17] = 255;
            row[18] = (byte)(i % 2);
            Encoding.UTF8.GetBytes("BuyerA", row[19..]);
            row[26] = 255;
        }
        var sales = RetainerHistorySale.Parse(packet);
        Assert.Equal(20, sales.Count);
        Assert.Equal(Sale, sales[0]);
        Assert.Equal(Sale with { ItemId = 101, IsMannequin = true }, sales[1]);
        packet.AsSpan(8 + 52, 4).Clear();
        Assert.Equal(Sale, Assert.Single(RetainerHistorySale.Parse(packet)));
        Assert.Empty(RetainerHistorySale.Parse(packet.AsSpan(0, 59)));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("gone")]
    [InlineData("allaganmarket")]
    [InlineData("delayed")]
    public void History_closes_or_enriches_the_existing_listing(string prior)
    {
        var tracker = new ListingTracker();
        LiveListing[] stack = [new(0, 100, true, 2, 100)];
        tracker.ObserveRetainer("B", "RetainerB", stack, _ => "Item", SoldAt.AddDays(-1));
        tracker.ObserveRetainer("A", "RetainerA", stack, _ => "Item", SoldAt.AddDays(-1));
        var listing = tracker.Records.Last();
        if (prior == "gone")
            tracker.ObserveRetainer("A", "RetainerA", [], _ => "Item", SoldAt.AddDays(4), true);
        if (prior is "allaganmarket" or "delayed")
        {
            tracker.ObserveRetainer("B", "RetainerB", [], _ => "Item", SoldAt, true);
            var receipt = prior == "delayed" ? SoldAt.AddDays(2) : SoldAt.AddMinutes(1);
            Assert.Same(listing, tracker.OnAllaganSale([100], 2, 190, receipt));
        }
        tracker.OnHistorySale("A", "RetainerA", Sale, _ => "Item");
        tracker.OnHistorySale("A", "RetainerA", Sale, _ => "Item");
        Assert.Equal(2, tracker.Records.Count);
        Assert.Same(listing, Assert.Single(tracker.GetSales(100, true)));
        Assert.Equal(SoldAt, listing.EndedUtc);
        Assert.Equal(SoldAt, listing.SaleTimeUtc);
        Assert.Equal(190, listing.SoldTotalGil);
        Assert.Equal(200, listing.SoldGrossGil);
        Assert.Equal("BuyerA", listing.BuyerName);
        Assert.False(listing.HistoryOnly);
    }

    [Fact]
    public void Enrichment_preserves_same_source_matching_for_the_next_sale()
    {
        var tracker = new ListingTracker();
        tracker.ObserveRetainer("A", "RetainerA", [new(0, 100, true, 2, 100), new(1, 100, true, 2, 100)],
            _ => "Item", SoldAt.AddMinutes(-5));
        tracker.OnAllaganSale([100], 2, 190, SoldAt.AddSeconds(10));
        tracker.OnHistorySale("A", "RetainerA", Sale, _ => "Item");
        Assert.Null(tracker.OnSale(100, true, 190, SoldAt.AddSeconds(20)));
        Assert.NotNull(tracker.OnAllaganSale([100], 2, 190, SoldAt.AddMinutes(1)));
        Assert.Equal(2, tracker.GetSales(100, true).Count());
    }

    [Fact]
    public void Old_unmatched_sales_survive_reload_and_do_not_close_a_new_listing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var tracker = new ListingTracker();
            tracker.ObserveRetainer("A", "RetainerA", [new(0, 100, true, 2, 100)],
                _ => "Item", SoldAt.AddDays(1));
            var sale = Sale with { IsMannequin = true };
            tracker.OnHistorySale("A", "RetainerA", sale, _ => "Item");
            tracker.OnHistorySale("A", "RetainerA", sale with { UnixSeconds = Time + 1 }, _ => "Item");
            tracker.OnHistorySale("B", "RetainerB", sale, _ => "Item");
            Assert.True(tracker.Save(dir.FullName));
            var restored = ListingTracker.Load(Path.Combine(dir.FullName, "listings.json"));
            restored.OnHistorySale("A", "RetainerA", sale, _ => "Item");
            Assert.False(restored.Dirty);
            Assert.Single(restored.ActiveFor(100, true));
            Assert.Empty(restored.GetSales(100, false));
            var sales = restored.GetSales(100, true).ToList();
            Assert.Equal(3, sales.Count);
            Assert.Equal(SoldAt.AddSeconds(1), sales[0].SaleTimeUtc);
            Assert.All(sales, s =>
            {
                Assert.True(s.HistoryOnly);
                Assert.True(s.IsMannequin);
                Assert.Null(s.DaysListed);
                Assert.Null(s.SoldGrossGil);
            });
            var csv = File.ReadAllLines(Path.Combine(dir.FullName, "listings.csv"));
            Assert.EndsWith("days_listed,buyer,sale_time_utc,sold_gross_gil,is_mannequin", csv[0]);
            Assert.Contains(csv, line => line.EndsWith(",BuyerA,2023-11-14T22:13:20Z,,true"));
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData("View sale history.")]
    [InlineData("販売履歴を見る")]
    [InlineData("Transaktionsverlauf ansehen")]
    [InlineData("Voir l’historique des transactions")]
    [InlineData("查看出售记录")]
    [InlineData("판매 내역 보기")]
    public void Matches_regional_history_menu(string label) => Assert.True(AddonText.IsSaleHistoryEntry(label));
}
