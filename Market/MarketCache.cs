using System;
using System.Collections.Generic;
using System.Linq;

namespace Restocker.Market;

/// <summary>
/// 「最安値 -1 ギル」のための短命なマーケット価格キャッシュ。
/// ItemSearchResult addon が更新されるたびに <see cref="MarketWatcher"/> が
/// 該当アイテム種ぶんの listings 価格を上書き保存する。
///
/// インスタンスは Plugin 寿命と同じ。プロセス終了で消える（永続化しない）。
///
/// Fork: stores every competitor price (not only the first) so the outlier filter has
/// something to work with, plus the time of the last update so a fetch can tell fresh
/// data from a previous cycle's leftovers.
/// </summary>
public sealed class MarketCache
{
    private readonly Dictionary<(uint ItemId, bool IsHQ), (List<long> Prices, DateTime UpdatedUtc)> store = new();

    /// <summary>Injectable clock for tests.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// 1 検索ぶんの listings をまるごと差し替える。空リストは「競合なし」を意味する。
    /// </summary>
    public void Replace(uint itemId, bool isHQ, IEnumerable<long> prices)
    {
        store[(itemId, isHQ)] = (prices.Where(p => p > 0).OrderBy(p => p).ToList(), Clock());
    }

    /// <summary>外れ値除外つきの最安値。データ無し / 競合なしなら 0。</summary>
    public long GetLowest(uint itemId, bool isHQ)
    {
        if (!store.TryGetValue((itemId, isHQ), out var e) || e.Prices.Count == 0) return 0;
        return LowestPriceResolver.Pick(e.Prices);
    }

    public bool HasData(uint itemId, bool isHQ) => store.ContainsKey((itemId, isHQ));

    /// <summary>True when the entry was written at or after <paramref name="sinceUtc"/>.</summary>
    public bool HasDataSince(uint itemId, bool isHQ, DateTime sinceUtc)
        => store.TryGetValue((itemId, isHQ), out var e) && e.UpdatedUtc >= sinceUtc;

    public IReadOnlyList<long> GetRaw(uint itemId, bool isHQ)
        => store.TryGetValue((itemId, isHQ), out var e) ? e.Prices : new List<long>();
}
