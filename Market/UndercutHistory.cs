using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Restocker.Market;

public readonly record struct UndercutObservation(
    uint ItemId, bool IsHq, string Seller, DateTime ObservedUtc, long OurPrice, long RivalPrice);

public readonly record struct UndercutCounts(int Last24h, int Last3d, int Last7d);

public readonly record struct UndercutRival(string Seller, UndercutCounts Counts);

// Fork: rolling history of sellers seen below our lowest known price for an item and quality.
public sealed class UndercutHistory
{
    private static readonly TimeSpan ScanWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(8);
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    private static readonly TimeSpan ThreeDays = TimeSpan.FromDays(3);
    private static readonly TimeSpan OneWeek = TimeSpan.FromDays(7);
    private readonly object gate = new();
    private readonly Dictionary<(uint ItemId, bool IsHq), List<UndercutObservation>> history = new();
    private bool dirty;

    internal bool Dirty
    {
        get { lock (gate) return dirty; }
    }

    public void RecordScan(uint itemId, bool isHq, long ourPrice, IEnumerable<MarketOffer> offers, DateTime nowUtc)
    {
        var undercutters = offers.Where(o => o.Price > 0 && o.Price < ourPrice && !string.IsNullOrWhiteSpace(o.Seller))
            .GroupBy(o => o.Seller, StringComparer.Ordinal);

        lock (gate)
        {
            foreach (var seller in undercutters)
            {
                var rivalPrice = seller.Min(o => o.Price);
                var observations = ForItem(itemId, isHq);
                var index = observations.FindLastIndex(e => e.Seller == seller.Key
                    && (nowUtc - e.ObservedUtc).Duration() <= ScanWindow);
                if (index < 0)
                    observations.Add(new UndercutObservation(itemId, isHq, seller.Key, nowUtc, ourPrice, rivalPrice));
                else if (rivalPrice < observations[index].RivalPrice)
                    // Keep the original timestamp so repeated responses cannot extend the scan window.
                    observations[index] = observations[index] with { OurPrice = ourPrice, RivalPrice = rivalPrice };
                else
                    continue;

                dirty = true;
            }
        }
    }

    public UndercutCounts Counts(uint itemId, bool isHq, string seller, DateTime nowUtc)
    {
        lock (gate)
        {
            return history.TryGetValue((itemId, isHq), out var observations)
                ? Count(observations.Where(e => e.Seller == seller), nowUtc)
                : default;
        }
    }

    public IReadOnlyList<UndercutRival> TopRivals(uint itemId, bool isHq, DateTime nowUtc, int n)
    {
        lock (gate)
        {
            if (n <= 0 || !history.TryGetValue((itemId, isHq), out var observations))
                return Array.Empty<UndercutRival>();

            return observations.GroupBy(e => e.Seller, StringComparer.Ordinal)
                .Select(seller => new UndercutRival(seller.Key, Count(seller, nowUtc)))
                .Where(rival => rival.Counts.Last7d > 0)
                .OrderByDescending(rival => rival.Counts.Last7d)
                .ThenBy(rival => rival.Seller, StringComparer.Ordinal)
                .Take(n)
                .ToArray();
        }
    }

    public static UndercutHistory Load(string path, DateTime nowUtc)
    {
        var result = new UndercutHistory();
        try
        {
            using var file = File.OpenRead(path);
            var observations = JsonSerializer.Deserialize<UndercutObservation[]>(file);
            if (observations == null) return result;

            foreach (var e in observations)
            {
                if (e.ItemId == 0 || string.IsNullOrWhiteSpace(e.Seller) || e.ObservedUtc.Kind != DateTimeKind.Utc
                    || e.RivalPrice <= 0 || e.OurPrice <= e.RivalPrice || nowUtc - e.ObservedUtc > Retention)
                {
                    result.dirty = true;
                    continue;
                }

                result.ForItem(e.ItemId, e.IsHq).Add(e);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or unreadable history starts empty.
        }
        return result;
    }

    public bool Save(string path, DateTime nowUtc)
    {
        lock (gate)
        {
            var tempPath = path + ".tmp";
            try
            {
                foreach (var (key, observations) in history.ToArray())
                {
                    dirty |= observations.RemoveAll(e => nowUtc - e.ObservedUtc > Retention) > 0;
                    if (observations.Count == 0) history.Remove(key);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using (var file = File.Create(tempPath))
                    JsonSerializer.Serialize(file, history.Values.SelectMany(e => e));
                File.Move(tempPath, path, overwrite: true);
                dirty = false;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private List<UndercutObservation> ForItem(uint itemId, bool isHq)
    {
        if (!history.TryGetValue((itemId, isHq), out var observations))
            history.Add((itemId, isHq), observations = new List<UndercutObservation>());
        return observations;
    }

    private static UndercutCounts Count(IEnumerable<UndercutObservation> observations, DateTime nowUtc)
    {
        var last24h = 0;
        var last3d = 0;
        var last7d = 0;
        foreach (var observation in observations)
        {
            var age = nowUtc - observation.ObservedUtc;
            if (age < TimeSpan.Zero || age > OneWeek) continue;
            last7d++;
            if (age <= ThreeDays) last3d++;
            if (age <= OneDay) last24h++;
        }
        return new UndercutCounts(last24h, last3d, last7d);
    }
}
