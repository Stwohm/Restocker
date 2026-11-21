using Restocker.Market;

namespace Restocker.Tests;

public class BreakEvenFloorTests
{
    [Theory]
    [InlineData(100, 5, 0, 106)]
    [InlineData(95, 5, 0, 100)]
    [InlineData(100, 0, 0, 100)]
    [InlineData(100, 5, 20, 127)]
    [InlineData(0, 5, 0, 1)]
    public void Computes_the_minimum_whole_gil_price(int cost, int tax, int profit, long expected)
    {
        Assert.Equal((expected, true), BreakEvenFloor.Resolve(0, cost, tax, profit));
    }

    [Fact]
    public void Rounds_up_fractional_purchase_cost()
    {
        Assert.Equal((101L, true), BreakEvenFloor.Resolve(0, 100.01m, 0, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Returns_no_floor_without_a_manual_override_or_cost(long manualFloor)
    {
        Assert.Equal((0L, false), BreakEvenFloor.Resolve(manualFloor, null, 5, 0));
    }

    [Fact]
    public void Uses_the_manual_floor_without_a_cost()
    {
        Assert.Equal((125L, false), BreakEvenFloor.Resolve(125, null, 5, 0));
    }

    [Theory]
    [InlineData(105)]
    [InlineData(106)]
    [InlineData(125)]
    public void A_positive_manual_floor_overrides_cost(long manualFloor)
    {
        Assert.Equal((manualFloor, false), BreakEvenFloor.Resolve(manualFloor, 100m, 5, 0));
    }

    [Fact]
    public void Falls_back_to_cost_for_a_negative_legacy_entry()
    {
        Assert.Equal((106L, true), BreakEvenFloor.Resolve(-1, 100m, 5, 0));
    }

    [Theory]
    [InlineData(125, null, null, false)]
    [InlineData(0, null, null, false)]
    [InlineData(0, 0, null, true)]
    [InlineData(126, 100, null, true)]
    [InlineData(126, null, 100, true)]
    [InlineData(126, 100, 95, true)]
    [InlineData(126, 95, 100, true)]
    [InlineData(127, 95, 100, false)]
    [InlineData(128, 100, 95, false)]
    public void Migration_removes_only_floors_below_the_highest_known_break_even(
        long manualFloor, int? nqUnitCost, int? hqUnitCost, bool expected)
    {
        Assert.Equal(expected, BreakEvenFloor.ShouldRemoveManualFloor(manualFloor, nqUnitCost, hqUnitCost, 5, 20));
    }

    [Fact]
    public void Saturates_a_floor_above_the_long_range()
    {
        Assert.Equal((long.MaxValue, true), BreakEvenFloor.Resolve(0, decimal.MaxValue, 20, 500));
    }
}
