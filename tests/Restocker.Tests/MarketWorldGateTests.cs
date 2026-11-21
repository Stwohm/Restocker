using Restocker.Market;

namespace Restocker.Tests;

public class MarketWorldGateTests
{
    [Theory]
    [InlineData(21u, 21u, true)]
    [InlineData(21u, 22u, false)]
    [InlineData(0u, 21u, false)]
    [InlineData(21u, 0u, false)]
    [InlineData(0u, 0u, false)]
    public void Accepts_only_a_known_home_world(uint currentWorld, uint homeWorld, bool expected)
        => Assert.Equal(expected, MarketWorldGate.Accept(currentWorld, homeWorld));
}
