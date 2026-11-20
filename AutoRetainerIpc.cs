using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Restocker.Execution;

namespace Restocker;

/// <summary>
/// Fork: thin AutoRetainer IPC client. Endpoint names checked against PunishXIV/AutoRetainer
/// master on 2026-11-20 (Modules/IPC.cs, Modules/EzIPCManagers/IPC_PluginState.cs).
/// Every call swallows failures: an AutoRetainer update must never break Restocky.
/// </summary>
public sealed class AutoRetainerIpc
{
    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;
    private readonly Func<bool> isLoaded;
    private bool suppressedByUs;

    public AutoRetainerIpc(IDalamudPluginInterface pi, IPluginLog log, Func<bool> isLoaded)
    {
        this.pi = pi;
        this.log = log;
        this.isLoaded = isLoaded;
    }

    public bool IsLoaded => isLoaded();

    /// <summary>Null when AutoRetainer is not loaded or the IPC answer could not be read.</summary>
    public AutoRetainerState? Read(ulong contentId)
    {
        if (!isLoaded()) return null;
        try
        {
            var busy = pi.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc();
            long? secs = null;
            try
            {
                secs = pi.GetIpcSubscriber<ulong, long?>("AutoRetainer.PluginState.GetClosestRetainerVentureSecondsRemaining")
                    .InvokeFunc(contentId);
            }
            catch (Exception ex) { log.Debug(ex, "[Restocker] AR venture seconds unavailable"); }
            var sense = Try(() => pi.GetIpcSubscriber<bool>("AutoRetainer.PluginState.GetOptionRetainerSense").InvokeFunc());
            var multi = Try(() => pi.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled").InvokeFunc());
            return new AutoRetainerState(busy, secs, sense, multi);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Restocker] AutoRetainer IPC read failed");
            return null;
        }
    }

    /// <summary>Pause AutoRetainer's scheduler / multi mode for the length of our cycle.</summary>
    public void Suppress()
    {
        if (!isLoaded() || suppressedByUs) return;
        try
        {
            var already = pi.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed").InvokeFunc();
            if (already) return; // someone else suppressed it; leave their state alone
            pi.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(true);
            suppressedByUs = true;
            log.Information("[Restocker] AutoRetainer suppressed for the cycle");
        }
        catch (Exception ex) { log.Warning(ex, "[Restocker] AutoRetainer suppress failed"); }
    }

    /// <summary>Undo <see cref="Suppress"/>. Safe to call repeatedly.</summary>
    public void Release()
    {
        if (!suppressedByUs) return;
        suppressedByUs = false;
        try
        {
            pi.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(false);
            log.Information("[Restocker] AutoRetainer released");
        }
        catch (Exception ex) { log.Warning(ex, "[Restocker] AutoRetainer release failed"); }
    }

    private static bool Try(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }
}
