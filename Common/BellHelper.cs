using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Restocker.Common;

/// <summary>
/// Fork: reopen the summoning bell the player is standing at (AutoRetainer closes it when it finishes).
/// Same detection as AutoRetainer (Lang.BellName / PlayerWorldHandlers, checked 2026-11-20):
/// EventObj / HousingEventObject named like EObjName row 2000401, within reach.
/// </summary>
public static unsafe class BellHelper
{
    private const float Reach = 4.0f;

    private static string[]? names;

    private static string[] BellNames()
    {
        if (names != null) return names;
        var n = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.EObjName>().GetRowOrDefault(2000401)?.Singular.ExtractText();
        names = string.IsNullOrEmpty(n) ? new[] { "Summoning Bell", "リテイナーベル" } : new[] { n, "リテイナーベル" };
        return names;
    }

    /// <summary>True when an interaction was sent.</summary>
    public static bool TryOpenNearestBell()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return false;
        var bellNames = BellNames();
        var bell = Plugin.ObjectTable
            .Where(o => o != null && o.IsTargetable
                        && (o.ObjectKind == ObjectKind.EventObj || o.ObjectKind == ObjectKind.HousingEventObject)
                        && bellNames.Any(b => string.Equals(b, o.Name.TextValue, System.StringComparison.OrdinalIgnoreCase)))
            .OrderBy(o => Vector3.Distance(o.Position, me.Position))
            .FirstOrDefault();
        if (bell == null || Vector3.Distance(bell.Position, me.Position) > Reach) return false;
        var ts = TargetSystem.Instance();
        if (ts == null) return false;
        ts->InteractWithObject((GameObjectStruct*)bell.Address, false);
        Plugin.Log.Information("[Restocker] reopened summoning bell");
        return true;
    }
}
