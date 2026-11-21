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
    [InlineData(0)]
    [InlineData(125)]
    public void Uses_the_manual_floor_without_a_cost(long manualFloor)
    {
        Assert.Equal((manualFloor, false), BreakEvenFloor.Resolve(manualFloor, null, 5, 0));
    }

    [Theory]
    [InlineData(105, 106, true)]
    [InlineData(106, 106, false)]
    [InlineData(125, 125, false)]
    public void Uses_the_higher_floor_and_reports_its_source(long manualFloor, long expected, bool fromCost)
    {
        Assert.Equal((expected, fromCost), BreakEvenFloor.Resolve(manualFloor, 100m, 5, 0));
    }

    [Fact]
    public void Saturates_a_floor_above_the_long_range()
    {
        Assert.Equal((long.MaxValue, true), BreakEvenFloor.Resolve(0, decimal.MaxValue, 20, 500));
    }
}
