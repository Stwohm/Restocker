using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Restocker.Common;

namespace Restocker.Execution;

/// <summary>
/// Fork: timed full cycle while the player stands at the summoning bell.
///
/// Off on every plugin load (never persisted). While on:
///   - runs <see cref="Executor.StartFullCycle"/> immediately, then every interval +/- jitter;
///   - turns itself off when the player leaves the bell, moves, enters combat / duty /
///     cutscene / zone change, a duty pops, the user cancels a run, or N cycles in a row fail.
/// Every stop is printed to chat with its reason.
/// </summary>
public sealed class AutoCycleController : IDisposable
{
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Executor executor;
    private readonly Configuration configuration;
    private readonly Func<bool> isAutoRetainerLoaded;
    private readonly Random rng = new();

    private bool cycleRunning;
    private bool cycleIsAuto;
    private Vector3 anchor;
    private DateTime nextPollUtc = DateTime.MinValue;

    public bool Enabled { get; private set; }
    public DateTime? NextRunUtc { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public string? LastSummary { get; private set; }
    public bool CycleRunning => cycleRunning;

    /// <summary>Max distance (yalms) the player may drift from where the loop was turned on.</summary>
    private const float MoveTolerance = 1.0f;

    public AutoCycleController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        IChatGui chat,
        IPluginLog log,
        Executor executor,
        Configuration configuration,
        Func<bool> isAutoRetainerLoaded)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.chat = chat;
        this.log = log;
        this.executor = executor;
        this.configuration = configuration;
        this.isAutoRetainerLoaded = isAutoRetainerLoaded;
        framework.Update += Tick;
    }

    public void Dispose() => framework.Update -= Tick;

    public static bool IsBellOpen() => AddonHelper.IsOpen("RetainerList");

    /// <summary>One full cycle now (the manual "one click" button). Does not turn the loop on.</summary>
    public bool RunOnce()
    {
        if (executor.IsRunning) return false;
        if (!IsBellOpen()) { Print("open the summoning bell first."); return false; }
        if (isAutoRetainerLoaded())
            Print("warning: AutoRetainer is loaded. Make sure it is not driving the bell right now.");
        Start(auto: false);
        return executor.IsRunning;
    }

    public void Enable()
    {
        if (Enabled) return;
        if (!IsBellOpen() || !condition[ConditionFlag.OccupiedSummoningBell])
        {
            Print("auto cycle needs the summoning bell open.");
            return;
        }
        var pos = PlayerPosition();
        if (pos == null) return;
        anchor = pos.Value;
        Enabled = true;
        ConsecutiveFailures = 0;
        NextRunUtc = DateTime.UtcNow;
        Print($"auto cycle ON, every {Interval().TotalMinutes:0} min (+/- {Math.Max(0, configuration.AutoCycleJitterSeconds)} s).");
        if (isAutoRetainerLoaded())
            Print("warning: AutoRetainer is loaded. If it drives the bell at the same time the two will collide.");
        log.Information("[Restocker] auto cycle enabled");
    }

    public void Disable(string reason)
    {
        if (!Enabled) return;
        Enabled = false;
        NextRunUtc = null;
        if (cycleRunning && cycleIsAuto && executor.IsRunning) executor.Cancel();
        Print($"auto cycle OFF: {reason}");
        log.Information($"[Restocker] auto cycle disabled: {reason}");
    }

    private void Start(bool auto)
    {
        executor.StartFullCycle();
        if (!executor.IsRunning)
        {
            LastSummary = "no retainers in the bell list";
            return;
        }
        cycleRunning = true;
        cycleIsAuto = auto;
    }

    private TimeSpan Interval() => TimeSpan.FromMinutes(Math.Max(1, configuration.AutoCycleIntervalMinutes));

    private void ScheduleNext()
    {
        var jitter = Math.Max(0, configuration.AutoCycleJitterSeconds);
        var offset = jitter == 0 ? 0 : rng.Next(-jitter, jitter + 1);
        NextRunUtc = DateTime.UtcNow + Interval() + TimeSpan.FromSeconds(offset);
    }

    private void Tick(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextPollUtc) return;
        nextPollUtc = now + TimeSpan.FromMilliseconds(250);

        try
        {
            if (cycleRunning && !executor.IsRunning) FinishCycle();
            if (!Enabled) return;

            var stop = StopReason();
            if (stop != null) { Disable(stop); return; }

            // Only from the plain bell list: never while the user is inside a retainer's menus.
            if (!executor.IsRunning && NextRunUtc is { } next && now >= next && IsBellOpen()
                && !AddonHelper.IsOpen("SelectString") && !AddonHelper.IsOpen("RetainerSellList")
                && !AddonHelper.IsOpen("RetainerSell"))
            {
                log.Information("[Restocker] auto cycle: starting full cycle");
                Start(auto: true);
                if (!cycleRunning) ScheduleNext();
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Restocker] auto cycle tick exception");
            Disable($"internal error ({ex.GetType().Name})");
        }
    }

    private void FinishCycle()
    {
        cycleRunning = false;
        var ok = executor.State == ExecutionState.Done;
        var stamp = DateTime.Now.ToString("HH:mm");
        LastSummary = ok
            ? $"{stamp} done: {executor.CompletedJobs} retainer(s), {executor.RepricesApplied} repriced, {executor.RepricesSkipped} unchanged/skipped"
            : $"{stamp} stopped: {executor.StatusMessage}";
        log.Information($"[Restocker] cycle finished: {LastSummary}");

        if (!cycleIsAuto)
        {
            Print(LastSummary);
            return;
        }
        if (!Enabled) return;

        if (ok)
        {
            ConsecutiveFailures = 0;
        }
        else if (executor.StatusMessage == "cancelled")
        {
            Disable("run cancelled by user");
            return;
        }
        else
        {
            ConsecutiveFailures++;
            Print($"cycle failed ({ConsecutiveFailures}/{configuration.AutoCycleMaxFailures}): {executor.StatusMessage}");
            if (ConsecutiveFailures >= Math.Max(1, configuration.AutoCycleMaxFailures))
            {
                Disable($"{ConsecutiveFailures} failed cycles in a row");
                return;
            }
        }
        ScheduleNext();
    }

    /// <summary>Null when it is safe to keep the loop on, otherwise why it must stop.</summary>
    private string? StopReason()
    {
        if (condition[ConditionFlag.InCombat]) return "combat";
        if (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95])
            return "in a duty";
        if (condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78] || condition[ConditionFlag.OccupiedInCutSceneEvent])
            return "cutscene";
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return "zone change";
        if (AddonHelper.IsOpen("ContentsFinderConfirm")) return "duty pop";

        var pos = PlayerPosition();
        if (pos == null) return "no local player";
        if (Vector3.Distance(pos.Value, anchor) > MoveTolerance) return "player moved";

        // During a run the executor walks retainer menus; it stops by itself if the bell
        // UI disappears. Between runs the player must still be at the bell.
        if (!executor.IsRunning && !condition[ConditionFlag.OccupiedSummoningBell]) return "left the summoning bell";
        return null;
    }

    private Vector3? PlayerPosition() => objectTable.LocalPlayer?.Position;

    private void Print(string msg) => chat.Print($"[Restocky] {msg}");
}
