using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Restocker.Data;

namespace Restocker.Market;

// Copy the native buffer while it is valid. Resolve game state on the framework thread.
internal sealed unsafe class RetainerHistoryCapture : IDisposable
{
    private delegate nint ProcessHistory(nint self, nint data);
    private readonly Hook<ProcessHistory>? hook;
    private readonly ListingTracker tracker;
    private readonly ConcurrentQueue<byte[]> pending = new();
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
        var active = manager == null ? null : manager->GetActiveRetainer();
        var cid = Plugin.PlayerState.ContentId;
        if (active == null || active->RetainerId == 0 || cid == 0) return null;
        name = active->NameString;
        return RetainerSnapshot.MakeKey(cid, active->RetainerId);
    }

    private nint Detour(nint self, nint data)
    {
        var result = hook!.Original(self, data);
        try
        {
            if (data != 0)
                pending.Enqueue(new ReadOnlySpan<byte>((void*)data, RetainerHistorySale.PacketSize).ToArray());
            else
                Plugin.Log.Warning("[Restocky] history: detour hit with null packet");
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[Restocky] history: packet copy failed"); }
        return result;
    }

    private void Drain(IFramework _)
    {
        while (pending.TryDequeue(out var packet))
        {
            try
            {
                var sales = RetainerHistorySale.Parse(packet, message => Plugin.Log.Information(message));
                var first = sales.FirstOrDefault();
                Plugin.Log.Information($"[Restocky] history: detour hit parsed={sales.Count} " +
                    (first == null ? "first=none" : $"first item={first.ItemId} qty={first.Quantity} unix={first.UnixSeconds}"));
                var key = ActiveKey(out var name);
                var source = "active";
                if (key == null)
                {
                    var seen = Plugin.Instance.RetainerWatcher?.LastSeen;
                    if (seen == null || seen.CharacterContentId != Plugin.PlayerState.ContentId)
                    {
                        Plugin.Log.Warning($"[Restocky] history: drain retainer unresolved, skipped packet parsed={sales.Count}");
                        continue;
                    }
                    key = seen.Key;
                    name = seen.RetainerName;
                    source = "watcher";
                }

                var matched = 0;
                var historyOnly = 0;
                var duplicates = 0;
                var items = Plugin.DataManager.GetExcelSheet<Item>();
                // Fork: apply FIFO costs in transaction order within each history response.
                foreach (var sale in sales.OrderBy(s => s.UnixSeconds))
                {
                    var outcome = tracker.OnHistorySale(key, name, sale,
                        id => items.TryGetRow(id, out var row) ? row.Name.ExtractText() : $"#{id}",
                        message => Plugin.Log.Information(message));
                    switch (outcome)
                    {
                        case HistorySaleResult.Duplicate: duplicates++; break;
                        case HistorySaleResult.MatchedListing: matched++; break;
                        case HistorySaleResult.HistoryOnly: historyOnly++; break;
                    }
                }
                tracker.ReconcileHistory(message => Plugin.Log.Information(message));
                receipts[key] = Receipt(key) + 1;
                Plugin.Log.Information($"[Restocky] history: drain retainer='{name}' source={source} new={matched + historyOnly} duplicate={duplicates} matched-listing={matched} history-only={historyOnly}");
            }
            catch (Exception ex) { Plugin.Log.Warning(ex, "[Restocky] history: recording packet failed"); }
        }
    }

    public void Dispose()
    {
        hook?.Dispose();
        Plugin.Framework.Update -= Drain;
        if (!pending.IsEmpty)
            Plugin.Framework.RunOnFrameworkThread(() => Drain(Plugin.Framework)).GetAwaiter().GetResult();
    }
}
