using System;

namespace Restocker.Market;

public static class BreakEvenFloor
{
    public static (long Price, bool FromCost) Resolve(long manualFloor, decimal? unitCost,
        decimal saleTaxPercent, decimal minProfitPercent)
    {
        if (manualFloor > 0) return (manualFloor, false);
        if (unitCost is not { } cost) return (0, false);

        var retainedShare = 1m - saleTaxPercent / 100m;
        var profitFactor = 1m + minProfitPercent / 100m;
        var floor = cost >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Clamp(decimal.Ceiling(cost * profitFactor / retainedShare), 1m, long.MaxValue);
        return (floor, true);
    }

    public static bool ShouldRemoveManualFloor(long manualFloor, decimal? nqUnitCost, decimal? hqUnitCost,
        decimal saleTaxPercent, decimal minProfitPercent)
    {
        var nqFloor = Resolve(0, nqUnitCost, saleTaxPercent, minProfitPercent).Price;
        var hqFloor = Resolve(0, hqUnitCost, saleTaxPercent, minProfitPercent).Price;
        var costFloor = Math.Max(nqFloor, hqFloor);
        return costFloor > 0 && manualFloor < costFloor;
    }
}
