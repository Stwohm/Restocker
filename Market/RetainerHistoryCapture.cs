using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Restocker.Data;

namespace Restocker.Market;

// Fork: capture the packet and its owner together. Only framework updates mutate the tracker.
internal sealed unsafe class RetainerHistoryCapture : IDisposable
{
    private delegate nint ProcessHistory(nint self, nint data);
    private readonly Hook<ProcessHistory>? hook;
    private readonly ListingTracker tracker;
    private readonly ConcurrentQueue<(string Key, string Name, List<RetainerHistorySale> Sales)> pending = new();
    private readonly Dictionary<string, long> receipts = new();
    public bool Available => hook != null;
    public long Receipt(string key) => receipts.GetValueOrDefault(key);

    public RetainerHistoryCapture(ListingTracker tracker)
    {
        this.tracker = tracker;
        try
        {
            hook = Plugin.Interop.HookFromSignature<ProcessHistory>("40 53 56 57 41 57 48 83 EC 38 48 8B F1", Detour);
            hook.Enable();
        }
        catch (Exception ex)
        {
            hook?.Dispose();
            hook = null;
            Plugin.Log.Warning(ex, "[Restocky] Retainer sale history unavailable");
        }
        Plugin.Framework.Update += Drain;
    }

    internal static string? ActiveKey(out string name)
    {
        name = string.Empty;
        var manager = RetainerManager.Instance();
        var active = manager == null || !manager->IsReady ? null : manager->GetActiveRetainer();
        var cid = Plugin.PlayerState.ContentId;
        if (active == null || active->RetainerId == 0 || cid == 0) return null;
        name = active->NameString;
        return RetainerSnapshot.MakeKey(cid, active->RetainerId);
    }

    private nint Detour(nint self, nint data)
    {
        try
        {
            if (data != 0 && ActiveKey(out var name) is { } key)
            {
                var packet = new ReadOnlySpan<byte>((void*)data,
                    8 + RetainerHistorySale.MaxRecords * RetainerHistorySale.RecordSize);
                pending.Enqueue((key, name, RetainerHistorySale.Parse(packet)));
            }
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[Restocky] Reading sale history failed"); }
        return hook!.Original(self, data);
    }

    private void Drain(IFramework _)
    {
        while (pending.TryDequeue(out var packet))
        {
            try
            {
                var items = Plugin.DataManager.GetExcelSheet<Item>();
                foreach (var sale in packet.Sales)
                    tracker.OnHistorySale(packet.Key, packet.Name, sale,
                        id => items.TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}");
                receipts[packet.Key] = Receipt(packet.Key) + 1;
            }
            catch (Exception ex) { Plugin.Log.Warning(ex, "[Restocky] Recording sale history failed"); }
        }
    }

    public void Dispose()
    {
        hook?.Dispose();
        Plugin.Framework.Update -= Drain;
        Drain(Plugin.Framework);
    }
}
