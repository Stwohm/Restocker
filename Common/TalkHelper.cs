using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Restocker.Common;

/// <summary>
/// Fork: advance the "Talk" subtitle box (retainer lines such as "you shouldn't summon me while I'm
/// on a venture"). AutoRetainer normally clicks these, but only while it is NOT suppressed, and
/// Restocky suppresses it during a cycle. Same event sequence as ECommons AddonMaster.Talk.Click
/// (checked 2026-11-20): MouseDown / MouseClick / MouseUp with state flags 132.
/// </summary>
public static unsafe class TalkHelper
{
    public static bool ClickIfVisible()
    {
        var addon = AddonHelper.GetVisible("Talk");
        if (addon == null) return false;

        var evt = stackalloc AtkEvent[1];
        evt[0] = new AtkEvent
        {
            Listener = (AtkEventListener*)addon,
            Target = &AtkStage.Instance()->AtkEventTarget,
        };
        evt[0].State.StateFlags = (AtkEventStateFlags)132;

        var data = stackalloc AtkEventData[1];
        for (var i = 0; i < sizeof(AtkEventData); i++) ((byte*)data)[i] = 0;

        addon->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
        addon->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
        return true;
    }
}
