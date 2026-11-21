using System.IO;
using System.Text.Json;
using Restocker.Market;

namespace Restocker.Tests;

// Fork: exercise undercut counts and persistence without running Dalamud.
public sealed class UndercutHistoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    private readonly string directory = Directory.CreateTempSubdirectory("restocky-undercuts-").FullName;
    private string PathToHistory => Path.Combine(directory, "undercut-history.json");

    [Theory]
    [InlineData(1, 0, 1, 1, 1)]
    [InlineData(1, 1, 0, 1, 1)]
    [InlineData(3, 0, 0, 1, 1)]
    [InlineData(3, 1, 0, 0, 1)]
    [InlineData(7, 0, 0, 0, 1)]
    [InlineData(7, 1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0, 0)]
    public void Counts_include_exact_window_boundaries(int days, int extraTicks, int last24h, int last3d, int last7d)
    {
        var history = new UndercutHistory();
        Record(history, Now.AddDays(-days).AddTicks(-extraTicks));

        Assert.Equal(new UndercutCounts(last24h, last3d, last7d), history.Counts(1, false, "Rival", Now));
    }

    [Fact]
    public void A_scan_counts_each_seller_once_and_keeps_the_lowest_price_across_repeated_responses()
    {
        var history = new UndercutHistory();
        history.RecordScan(1, false, 100, new MarketOffer[]
        {
            new(90, "Rival", 1), new(80, "Rival", 5), new(95, "Bob", 1), new(100, "RetainerA", 1),
        }, Now);
        history.RecordScan(1, false, 85, new MarketOffer[] { new(70, "Rival", 2), new(75, "Bob", 2) }, Now.AddMinutes(1));
        history.RecordScan(1, false, 90, new MarketOffer[] { new(72, "Rival", 1) }, Now.AddMinutes(2));

        Assert.Equal(1, history.Counts(1, false, "Rival", Now.AddMinutes(2)).Last24h);
        Assert.Equal(1, history.Counts(1, false, "Bob", Now.AddMinutes(2)).Last24h);
        Assert.Equal(0, history.Counts(1, false, "RetainerA", Now.AddMinutes(2)).Last24h);
        Assert.True(history.Save(PathToHistory, Now.AddMinutes(2)));
        Assert.Equal(new UndercutObservation(1, false, "Rival", Now, 85, 70),
            Assert.Single(ReadSaved(), e => e.Seller == "Rival"));
    }

    [Fact]
    public void Responses_do_not_extend_the_scan_window()
    {
        var history = new UndercutHistory();
        Record(history, Now);
        Record(history, Now.AddMinutes(1), price: 80);
        Record(history, Now.AddMinutes(2).AddTicks(1));

        Assert.Equal(new UndercutCounts(2, 2, 2), history.Counts(1, false, "Rival", Now.AddMinutes(3)));
    }

    [Fact]
    public void Item_and_quality_have_independent_histories()
    {
        var history = new UndercutHistory();
        Record(history, Now);
        Record(history, Now, isHq: true);
        Record(history, Now.AddMinutes(3), isHq: true);
        Record(history, Now, itemId: 2);

        Assert.Equal(1, history.Counts(1, false, "Rival", Now.AddMinutes(3)).Last7d);
        Assert.Equal(2, history.Counts(1, true, "Rival", Now.AddMinutes(3)).Last7d);
        Assert.Equal(1, history.Counts(2, false, "Rival", Now.AddMinutes(3)).Last7d);
    }

    [Fact]
    public void Top_rivals_rank_by_weekly_count_even_when_a_rival_is_absent_from_the_latest_scan()
    {
        var history = new UndercutHistory();
        Record(history, Now.AddDays(-6));
        Record(history, Now.AddDays(-5));
        Record(history, Now, seller: "Bob");
        Record(history, Now.AddDays(-7).AddTicks(-1), seller: "RetainerA");

        Assert.Equal(new[]
        {
            new UndercutRival("Rival", new UndercutCounts(0, 0, 2)),
            new UndercutRival("Bob", new UndercutCounts(1, 1, 1)),
        }, history.TopRivals(1, false, Now, 5));
        Assert.Equal("Rival", Assert.Single(history.TopRivals(1, false, Now, 1)).Seller);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_and_save_remove_observations_older_than_eight_days(bool pruneOnLoad)
    {
        var history = new UndercutHistory();
        var eightDaysAgo = Now.AddDays(-8);
        Record(history, eightDaysAgo);
        Record(history, eightDaysAgo.AddTicks(-1), seller: "Bob");

        if (pruneOnLoad)
        {
            Assert.True(history.Save(PathToHistory, eightDaysAgo));
            history = UndercutHistory.Load(PathToHistory, Now);
        }

        // Writing with the earlier clock in the load case isolates pruning performed by Load.
        Assert.True(history.Save(PathToHistory, pruneOnLoad ? eightDaysAgo : Now));
        Assert.Equal("Rival", Assert.Single(ReadSaved()).Seller);
    }

    [Fact]
    public void History_round_trips_and_still_deduplicates_after_reload()
    {
        var history = new UndercutHistory();
        Record(history, Now);
        Record(history, Now.AddDays(-5), isHq: true, seller: "Bob", price: 75);
        Assert.True(history.Save(PathToHistory, Now));

        var loaded = UndercutHistory.Load(PathToHistory, Now);
        Record(loaded, Now.AddMinutes(1), price: 80);

        Assert.Equal(new UndercutCounts(1, 1, 1), loaded.Counts(1, false, "Rival", Now.AddMinutes(1)));
        Assert.Equal(new UndercutCounts(0, 0, 1), loaded.Counts(1, true, "Bob", Now));
        Assert.True(loaded.Save(PathToHistory, Now.AddMinutes(1)));
        Assert.Contains(new UndercutObservation(1, true, "Bob", Now.AddDays(-5), 100, 75), ReadSaved());
    }

    [Fact]
    public void Missing_and_corrupt_files_load_as_empty_history()
    {
        Assert.Empty(UndercutHistory.Load(PathToHistory, Now).TopRivals(1, false, Now, 5));
        File.WriteAllText(PathToHistory, "[");
        Assert.Empty(UndercutHistory.Load(PathToHistory, Now).TopRivals(1, false, Now, 5));
    }

    private static void Record(UndercutHistory history, DateTime time, uint itemId = 1, bool isHq = false,
        string seller = "Rival", long price = 90)
        => history.RecordScan(itemId, isHq, 100, new[] { new MarketOffer(price, seller, 1) }, time);

    private UndercutObservation[] ReadSaved()
        => JsonSerializer.Deserialize<UndercutObservation[]>(File.ReadAllText(PathToHistory))!;

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
