using System;
using Restocker.Execution;

namespace Restocker.Tests;

public class AutoRetainerGateTests
{
    private static readonly TimeSpan Cycle = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    private static GateDecision D(AutoRetainerState? s, double waitedMin = 0)
        => AutoRetainerGate.Decide(s, Cycle, Margin, TimeSpan.FromMinutes(waitedMin), MaxWait);

    [Fact]
    public void Not_loaded_proceeds() => Assert.True(D(null).Proceed);

    [Fact]
    public void Busy_waits() => Assert.False(D(new(true, 9999, true, false)).Proceed);

    [Fact]
    public void Venture_inside_cycle_plus_margin_waits()
        => Assert.False(D(new(false, 4 * 60, true, false)).Proceed);   // 4 min < 3 + 2

    [Fact]
    public void Venture_outside_window_proceeds()
        => Assert.True(D(new(false, 6 * 60, true, false)).Proceed);    // 6 min > 5

    [Fact]
    public void Ready_venture_waits_when_autoretainer_acts_at_the_bell()
        => Assert.False(D(new(false, -30, false, true)).Proceed);

    [Fact]
    public void Ready_venture_does_not_block_when_autoretainer_would_not_act()
        => Assert.True(D(new(false, -30, false, false)).Proceed);

    [Fact]
    public void Gives_up_waiting_after_max_wait()
        => Assert.True(D(new(false, -30, true, false), waitedMin: 15).Proceed);

    [Fact]
    public void Busy_still_waits_after_max_wait()
        => Assert.False(D(new(true, -30, true, false), waitedMin: 60).Proceed);

    [Fact]
    public void Expected_cycle_is_average_or_default()
    {
        Assert.Equal(AutoRetainerGate.DefaultCycle, AutoRetainerGate.ExpectedCycle(Array.Empty<double>()));
        Assert.Equal(TimeSpan.FromSeconds(150), AutoRetainerGate.ExpectedCycle(new double[] { 100, 200 }));
    }
}
