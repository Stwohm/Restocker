using System;
using System.Collections.Generic;

namespace Restocker.Market;

public readonly record struct SaleWindow(long Units, int Sales, double? AverageHoursBetweenSales);

// Fork: velocity is per item and quality. Supply is passed in from the inventory snapshots.
public readonly record struct SalesVelocity(SaleWindow Last24h, SaleWindow Last3d, SaleWindow Last7d, SaleWindow Last30d)
{
    public long SuggestedThreshold => (long)Math.Ceiling(Last7d.Units / 7m);

    public double? DaysOfStock(long listedUnits, long availableUnits)
        => Last30d.Units > 0 ? (listedUnits + (double)availableUnits) / (Last30d.Units / 30d) : null;

    public static SalesVelocity Calculate(IEnumerable<ListingRecord> sales, DateTime nowUtc)
    {
        var day = new Window();
        var threeDays = new Window();
        var week = new Window();
        var month = new Window();
        foreach (var sale in sales)
        {
            if (sale.Outcome != ListingTracker.Sold || sale.RecordedSaleUtc is not { } utc) continue;
            var age = nowUtc - utc;
            if (age < TimeSpan.Zero || age.TotalDays > 30) continue;
            month.Add(utc, sale.Quantity);
            if (age.TotalDays <= 7) week.Add(utc, sale.Quantity);
            if (age.TotalDays <= 3) threeDays.Add(utc, sale.Quantity);
            if (age.TotalDays <= 1) day.Add(utc, sale.Quantity);
        }
        return new(day.Result, threeDays.Result, week.Result, month.Result);
    }

    private struct Window
    {
        private long units;
        private int sales;
        private DateTime earliest;
        private DateTime latest;

        public void Add(DateTime utc, int quantity)
        {
            if (sales == 0 || utc < earliest) earliest = utc;
            if (sales == 0 || utc > latest) latest = utc;
            units += quantity;
            sales++;
        }

        // The gaps between consecutive sales sum to the time from the first to the last.
        public readonly SaleWindow Result => new(units, sales,
            sales > 1 ? (latest - earliest).TotalHours / (sales - 1) : null);
    }
}
