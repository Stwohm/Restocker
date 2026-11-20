using System;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Restocker.Market;

/// <summary>
/// Dalamud の <see cref="IMarketBoard.OfferingsReceived"/> イベントで
/// マーケットボード検索結果を受信して <see cref="MarketCache"/> を更新する。
/// PennyPincher と同じ流儀で、
///   - 自リテイナー listing はスキップ（自分で自分を undercut しない）
///   - HQ / NQ それぞれで「サーバが返した順序の先頭にある有効 listing」を採用
///     (server returns listings sorted by price ascending)
/// その採用 price を 1 件だけ <see cref="MarketCache"/> に入れる。
/// </summary>
public sealed unsafe class MarketWatcher : IDisposable
{
    private readonly IMarketBoard marketBoard;
    private readonly IPluginLog log;
    public MarketCache Cache { get; } = new();

    /// <summary>
    /// Fork: fired for answers that belong to our own fetch (expected item set), i.e. the home
    /// world board seen from our retainer. Browsing another world's board never fires it.
    /// Args: itemId, isHq, competitor offers ascending.
    /// </summary>
    public event Action<uint, bool, System.Collections.Generic.IReadOnlyList<MarketOffer>>? CompetitorsObserved;

    /// <summary>同じ検索結果が複数回飛んでくる server エラー時の保護。</summary>
    private int lastRequestId = -1;

    /// <summary>Executor が自動 fetch している間にセットされる「期待アイテム」。
    /// これと違う itemId の OfferingsReceived は server からの遅延着信扱いで cache 更新しない。
    /// null のとき (= 自動 fetch 中でない) は通常通り全結果を cache に入れる。</summary>
    private uint? expectedItemId;
    private bool expectedIsHq;

    public void SetExpectedItem(uint itemId, bool isHq)
    {
        expectedItemId = itemId;
        expectedIsHq = isHq;
    }

    public void ClearExpectedItem()
    {
        expectedItemId = null;
    }

    public MarketWatcher(IMarketBoard marketBoard, IPluginLog log)
    {
        this.marketBoard = marketBoard;
        this.log = log;
        marketBoard.OfferingsReceived += OnOfferingsReceived;
    }

    public void Dispose()
    {
        marketBoard.OfferingsReceived -= OnOfferingsReceived;
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        try
        {
            if (offerings.RequestId == lastRequestId) return;
            lastRequestId = offerings.RequestId;

            // Fork: an empty board for the item we are fetching means "no competitor".
            // Record that, otherwise the executor waits for its 6s timeout every time.
            if (offerings.ItemListings.Count == 0)
            {
                if (expectedItemId is { } exp && exp != 0)
                {
                    Cache.Replace(exp, false, Array.Empty<MarketOffer>());
                    Cache.Replace(exp, true, Array.Empty<MarketOffer>());
                    log.Information($"[Restocker] OfferingsReceived item={exp} empty board -> no competitor");
                }
                return;
            }

            var itemId = offerings.ItemListings[0].ItemId;
            if (itemId == 0) return;

            // 自動 fetch 中は「期待アイテム」と一致するものだけ採用する。
            // server が古いリクエストの応答を遅延配送してくると、現在 fetch 中の
            // listing と違うアイテムの結果が「最新の cache」になってしまうため。
            if (expectedItemId.HasValue && (expectedItemId.Value == 0 || itemId != expectedItemId.Value))
            {
                log.Information($"[Restocker] OfferingsReceived for item={itemId} ignored (expected {expectedItemId.Value})");
                return;
            }

            // デバッグ用に受信 listing をすべてログに出す。OWN マークは自リテイナーで
            // スキップされた listing。各行: HQ/NQ, price, retainerId。
            var dump = new System.Text.StringBuilder();
            dump.Append($"[Restocker] OfferingsReceived item={itemId} count={offerings.ItemListings.Count} reqId={offerings.RequestId}");

            // Fork: keep every competitor price (ascending as delivered) so
            // LowestPriceResolver can actually drop bait listings. Upstream kept only the first.
            var nqPrices = new System.Collections.Generic.List<MarketOffer>();
            var hqPrices = new System.Collections.Generic.List<MarketOffer>();
            for (var idx = 0; idx < offerings.ItemListings.Count; idx++)
            {
                var listing = offerings.ItemListings[idx];
                if (listing.ItemId != itemId) continue;
                var isOwn = IsOwnRetainer(listing.RetainerId);
                var price = (long)listing.PricePerUnit;
                var hqMark = listing.IsHq ? "HQ" : "NQ";
                var ownMark = isOwn ? " OWN" : "";
                dump.Append($"\n  [{idx,2}] {hqMark} price={price,9:N0} retainer={listing.RetainerId}{ownMark}");

                if (isOwn) continue;
                if (price <= 0) continue;
                var offer = new MarketOffer(price, listing.RetainerName ?? string.Empty, (int)listing.ItemQuantity);
                if (listing.IsHq) hqPrices.Add(offer);
                else nqPrices.Add(offer);
            }

            // Both flags are always written (possibly empty) so "no HQ competitor" is a known
            // answer, not a timeout.
            Cache.Replace(itemId, false, nqPrices);
            Cache.Replace(itemId, true, hqPrices);
            if (expectedItemId.HasValue)
            {
                try
                {
                    CompetitorsObserved?.Invoke(itemId, false, Cache.GetOffers(itemId, false));
                    CompetitorsObserved?.Invoke(itemId, true, Cache.GetOffers(itemId, true));
                }
                catch (Exception ex) { log.Error(ex, "[Restocker] CompetitorsObserved handler threw"); }
            }

            dump.Append($"\n  → nq={nqPrices.Count} (lowest {Cache.GetLowest(itemId, false):N0})  hq={hqPrices.Count} (lowest {Cache.GetLowest(itemId, true):N0})");
            log.Information(dump.ToString());
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] OfferingsReceived handler exception");
        }
    }

    private static bool IsOwnRetainer(ulong retainerId)
    {
        var rm = RetainerManager.Instance();
        if (rm == null) return false;
        for (uint i = 0; i < rm->GetRetainerCount(); ++i)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId == retainerId) return true;
        }
        return false;
    }
}
