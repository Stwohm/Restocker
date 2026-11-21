using System;

namespace Restocker.Market;

public static class BreakEvenFloor
{
    public static (long Price, bool FromCost) Resolve(long manualFloor, decimal? unitCost,
        decimal saleTaxPercent, decimal minProfitPercent)
    {
        if (unitCost is not { } cost) return (manualFloor, false);

        var retainedShare = 1m - saleTaxPercent / 100m;
        var profitFactor = 1m + minProfitPercent / 100m;
        var floor = cost >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Clamp(decimal.Ceiling(cost * profitFactor / retainedShare), 1m, long.MaxValue);
        return floor > manualFloor ? (floor, true) : (manualFloor, false);
    }
}
