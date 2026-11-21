using System;
using Restocker.Data;

namespace Restocker.Plan;

// Fork: stable column IDs for the shared Update prices sort preference.
public enum RepriceSortColumn : uint
{
    Slot = 1,
    Item = 2,
    Quantity = 3,
    CurrentPrice = 4,
}

public static class RepriceSort
{
    public static int Compare(
        (ListingEntry Entry, string ItemName) left,
        (ListingEntry Entry, string ItemName) right,
        RepriceSortColumn column = RepriceSortColumn.Item,
        bool descending = false)
    {
        var order = column switch
        {
            RepriceSortColumn.Slot => left.Entry.ListingIndex.CompareTo(right.Entry.ListingIndex),
            RepriceSortColumn.Quantity => left.Entry.Quantity.CompareTo(right.Entry.Quantity),
            RepriceSortColumn.CurrentPrice => ComparePrice(left.Entry.UnitPrice, right.Entry.UnitPrice),
            _ => StringComparer.OrdinalIgnoreCase.Compare(left.ItemName, right.ItemName),
        };
        if (order != 0)
            return descending ? -Math.Sign(order) : order;

        if (column == RepriceSortColumn.Item)
        {
            order = right.Entry.IsHQ.CompareTo(left.Entry.IsHQ);
            if (order != 0) return order;

            order = ComparePrice(left.Entry.UnitPrice, right.Entry.UnitPrice);
            if (order != 0) return order;
        }

        return left.Entry.ListingIndex.CompareTo(right.Entry.ListingIndex);
    }

    private static int ComparePrice(long left, long right)
    {
        var order = (left == 0).CompareTo(right == 0);
        return order != 0 ? order : left.CompareTo(right);
    }
}
