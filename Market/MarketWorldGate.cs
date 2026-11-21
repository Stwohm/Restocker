namespace Restocker.Market;

public static class MarketWorldGate
{
    public static bool Accept(uint currentWorld, uint homeWorld)
        => currentWorld != 0 && currentWorld == homeWorld;
}
