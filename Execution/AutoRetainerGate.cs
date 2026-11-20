using System;
using System.Collections.Generic;
using System.Linq;

namespace Restocker.Execution;

/// <summary>What AutoRetainer reports right now (null = not loaded / IPC failed).</summary>
public sealed record AutoRetainerState(
    bool Busy,
    long? SecondsToNextVenture,
    bool RetainerSense,
    bool MultiMode);

public readonly record struct GateDecision(bool Proceed, string Reason);

/// <summary>
/// Fork: decides whether a full cycle may start now or must wait for AutoRetainer.
/// Wait when AutoRetainer is busy, or when it will act at the bell (RetainerSense / multi mode)
/// on a venture that is ready or comes back within the expected cycle length + margin.
/// Never waits longer than <paramref name="maxWait"/> while AutoRetainer is idle.
/// </summary>
public static class AutoRetainerGate
{
    public static readonly TimeSpan DefaultCycle = TimeSpan.FromMinutes(5);

    public static GateDecision Decide(
        AutoRetainerState? ar,
        TimeSpan expectedCycle,
        TimeSpan margin,
        TimeSpan waitedSoFar,
        TimeSpan maxWait)
    {
        if (ar == null) return new GateDecision(true, "AutoRetainer not loaded");
        if (ar.Busy) return new GateDecision(false, "AutoRetainer is working");

        var willAct = ar.RetainerSense || ar.MultiMode;
        if (!willAct || ar.SecondsToNextVenture is not { } secs)
            return new GateDecision(true, "AutoRetainer idle");

        var window = (expectedCycle + margin).TotalSeconds;
        if (secs <= window)
        {
            if (waitedSoFar >= maxWait)
                return new GateDecision(true, $"waited {waitedSoFar.TotalMinutes:0} min, AutoRetainer did not start; going ahead");
            return secs <= 0
                ? new GateDecision(false, "venture ready, letting AutoRetainer collect it")
                : new GateDecision(false, $"venture back in {secs / 60}:{secs % 60:00}, waiting for AutoRetainer");
        }
        return new GateDecision(true, "next venture is far enough");
    }

    /// <summary>Average of the recent cycle durations, or <see cref="DefaultCycle"/> when none.</summary>
    public static TimeSpan ExpectedCycle(IReadOnlyCollection<double> recentSeconds)
        => recentSeconds.Count == 0 ? DefaultCycle : TimeSpan.FromSeconds(recentSeconds.Average());
}
