using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Restocker.Data;
using Restocker.Localization;

namespace Restocker;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// リテイナーベル（RetainerList addon）が開いている間に MainWindow を自動表示するか。
    /// </summary>
    public bool AutoOpenOnBell { get; set; } = true;

    /// <summary>
    /// パッシブ収集したリテイナーごとのスナップショット。
    /// キーは <see cref="RetainerSnapshot.Key"/>（CharacterContentId + RetainerId の文字列）。
    /// </summary>
    public Dictionary<string, RetainerSnapshot> Snapshots { get; set; } = new();

    /// <summary>
    /// キャラクター側の所持品スナップショット。キーは <see cref="CharacterSnapshot.MakeKey"/>。
    /// 新規出品でキャラ側のアイテムも対象にするため。
    /// </summary>
    public Dictionary<string, CharacterSnapshot> Characters { get; set; } = new();

    /// <summary>
    /// UI 言語。
    /// -1 = FFXIV クライアント言語に従う（ZH/KO はグローバルクライアントから返らないため EN/JA/DE/FR にフォールバック）。
    /// 0..5 = <see cref="Language"/> 列挙の値で明示指定。
    /// </summary>
    public int Language { get; set; } = -1;

    // ---------------- Fork (Restocky) ----------------

    /// <summary>Gil below the lowest competitor for the full / auto cycle.</summary>
    public int UndercutGil { get; set; } = 1;

    /// <summary>Do not apply a new price that is more than this % below the current one. 0 = off.</summary>
    public double MaxDropPercent { get; set; } = 30;

    /// <summary>Per-item minimum price (key = item id, HQ and NQ share it). Missing / 0 = no floor.</summary>
    public Dictionary<uint, long> PriceFloors { get; set; } = new();

    /// <summary>Automatic-reprice holds, keyed as "itemId:hq" (HQ and NQ are independent).</summary>
    public HashSet<string> HeldItems { get; set; } = new();

    /// <summary>Auto cycle interval in minutes.</summary>
    public int AutoCycleIntervalMinutes { get; set; } = 10;

    // Fork: request the retainer's sale history once per full/auto visit.
    public bool FetchSaleHistory { get; set; } = true;

    /// <summary>Random +/- jitter added to each auto cycle interval, in seconds.</summary>
    public int AutoCycleJitterSeconds { get; set; } = 60;

    /// <summary>Consecutive failed cycles before the auto cycle turns itself off.</summary>
    public int AutoCycleMaxFailures { get; set; } = 3;

    /// <summary>Chat channel (XivChatType value) for Restocky messages. Default 71 = RetainerSale,
    /// so a Chat 2 "Market" tab that shows Retainer Sale Notifications picks them up.</summary>
    public int ChatChannel { get; set; } = (int)Dalamud.Game.Text.XivChatType.RetainerSale;

    /// <summary>Items watched for low stock (alert + restock).</summary>
    public List<Restocker.Plan.LowStockEntry> LowStock { get; set; } = new();

    // Fork: age in whole days before an active listing is flagged as stuck.
    public int StuckListingDays { get; set; } = 3;

    /// <summary>Durations (seconds) of the last successful full cycles; used to time AutoRetainer waits.</summary>
    public List<double> RecentCycleSeconds { get; set; } = new();

    public long FloorFor(uint itemId) => PriceFloors.TryGetValue(itemId, out var f) ? f : 0;

    public bool IsHeld(uint itemId, bool hq) => HeldItems.Contains(HeldKey(itemId, hq));

    public void SetHeld(uint itemId, bool hq, bool held)
    {
        if (held) HeldItems.Add(HeldKey(itemId, hq));
        else HeldItems.Remove(HeldKey(itemId, hq));
    }

    private static string HeldKey(uint itemId, bool hq) => $"{itemId}:{(hq ? 1 : 0)}";

    public Restocker.Market.RepriceSettings ToRepriceSettings() => new()
    {
        UndercutGil = UndercutGil,
        MaxDropPercent = MaxDropPercent,
    };

    public Language ResolveLanguage(Dalamud.Game.ClientLanguage clientLanguage)
    {
        if (Language >= 0 && Language <= 5)
            return (Language)Language;

        return clientLanguage switch
        {
            Dalamud.Game.ClientLanguage.Japanese => Localization.Language.Japanese,
            Dalamud.Game.ClientLanguage.German => Localization.Language.German,
            Dalamud.Game.ClientLanguage.French => Localization.Language.French,
            _ => Localization.Language.English,
        };
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
