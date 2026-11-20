using System;
using System.Collections.Generic;
using System.Linq;

namespace Restocker.Market;

/// <summary>One competitor listing from a market board answer.</summary>
public readonly record struct MarketOffer(long Price, string Seller, int Quantity);

public static class MarketOfferFormat
{
    /// <summary>
    /// Sellers priced below <paramref name="ourPrice"/> with the quantity they have up,
    /// e.g. "Rival 4x;Bob 1x" (several listings of one seller are summed), cheapest first.
    /// </summary>
    public static string Undercutters(IEnumerable<MarketOffer> offers, long ourPrice)
    {
        var order = new List<string>();
        var qty = new Dictionary<string, int>();
        foreach (var o in offers.Where(o => o.Price < ourPrice).OrderBy(o => o.Price))
        {
            var name = string.IsNullOrEmpty(o.Seller) ? "?" : o.Seller;
            if (!qty.ContainsKey(name)) { qty[name] = 0; order.Add(name); }
            qty[name] += System.Math.Max(1, o.Quantity);
        }
        return string.Join(";", order.Select(n => $"{n} {qty[n]}x"));
    }
}

/// <summary>
/// 「最安値 -1 ギル」のための短命なマーケット価格キャッシュ。
/// ItemSearchResult addon が更新されるたびに <see cref="MarketWatcher"/> が
/// 該当アイテム種ぶんの listings 価格を上書き保存する。
///
/// インスタンスは Plugin 寿命と同じ。プロセス終了で消える（永続化しない）。
///
/// Fork: stores every competitor offer (price + retainer name), not only the first, so the
/// outlier filter has something to work with and the history knows who is competing; plus
/// the time of the last update so a fetch can tell fresh data from a previous cycle's leftovers.
/// </summary>
public sealed class MarketCache
{
    private readonly Dictionary<(uint ItemId, bool IsHQ), (List<MarketOffer> Offers, DateTime UpdatedUtc)> store = new();

    /// <summary>Injectable clock for tests.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>1 検索ぶんの listings をまるごと差し替える。空リストは「競合なし」を意味する。</summary>
    public void Replace(uint itemId, bool isHQ, IEnumerable<MarketOffer> offers)
    {
        store[(itemId, isHQ)] = (offers.Where(o => o.Price > 0).OrderBy(o => o.Price).ToList(), Clock());
    }

    public void Replace(uint itemId, bool isHQ, IEnumerable<long> prices)
        => Replace(itemId, isHQ, prices.Select(p => new MarketOffer(p, string.Empty, 0)));

    /// <summary>外れ値除外つきの最安値。データ無し / 競合なしなら 0。</summary>
    public long GetLowest(uint itemId, bool isHQ)
    {
        var raw = GetRaw(itemId, isHQ);
        return raw.Count == 0 ? 0 : LowestPriceResolver.Pick(raw);
    }

    public bool HasData(uint itemId, bool isHQ) => store.ContainsKey((itemId, isHQ));

    /// <summary>When the entry was last written, or null when never fetched.</summary>
    public DateTime? UpdatedUtc(uint itemId, bool isHQ)
        => store.TryGetValue((itemId, isHQ), out var e) ? e.UpdatedUtc : null;

    /// <summary>True when the entry was written at or after <paramref name="sinceUtc"/>.</summary>
    public bool HasDataSince(uint itemId, bool isHQ, DateTime sinceUtc)
        => store.TryGetValue((itemId, isHQ), out var e) && e.UpdatedUtc >= sinceUtc;

    /// <summary>Prices ascending.</summary>
    public IReadOnlyList<long> GetRaw(uint itemId, bool isHQ)
        => store.TryGetValue((itemId, isHQ), out var e) ? e.Offers.Select(o => o.Price).ToList() : new List<long>();

    /// <summary>Offers ascending by price.</summary>
    public IReadOnlyList<MarketOffer> GetOffers(uint itemId, bool isHQ)
        => store.TryGetValue((itemId, isHQ), out var e) ? e.Offers : new List<MarketOffer>();
}
