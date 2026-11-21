using System.Linq;
using Restocker.Data;
using Restocker.Plan;

namespace Restocker.Tests;

// Fork: exercise the display order without loading Dalamud or ImGui.
public sealed class RepriceSortTests
{
    [Fact]
    public void DefaultSort_GroupsStacksOfTheSameItem()
    {
        var rows = new[]
        {
            Row(0, "Twine"),
            Row(1, "Cloth"),
            Row(2, "Twine"),
            Row(3, "Yarn"),
            Row(4, "Cloth"),
        };

        Array.Sort(rows, (left, right) => RepriceSort.Compare(left, right));

        Assert.Equal(new[] { 1, 4, 0, 2, 3 }, rows.Select(row => row.Entry.ListingIndex));
    }

    [Fact]
    public void DefaultSort_PutsHqBeforeNqRegardlessOfPrice()
    {
        var nq = Row(0, "Twine", price: 20);
        var hq = Row(1, "Twine", price: 50, hq: true);

        Assert.True(RepriceSort.Compare(hq, nq) < 0);
    }

    [Fact]
    public void DefaultSort_BreaksNameAndQualityTiesByPriceThenSlot()
    {
        var rows = new[]
        {
            Row(2, "Twine", price: 10),
            Row(0, "Twine", price: 25),
            Row(1, "Twine", price: 10),
        };

        Array.Sort(rows, (left, right) => RepriceSort.Compare(left, right));

        Assert.Equal(new[] { 1, 2, 0 }, rows.Select(row => row.Entry.ListingIndex));
    }

    [Theory]
    [InlineData(RepriceSortColumn.Slot, false, 1)]
    [InlineData(RepriceSortColumn.Slot, true, 2)]
    [InlineData(RepriceSortColumn.Item, false, 2)]
    [InlineData(RepriceSortColumn.Item, true, 1)]
    [InlineData(RepriceSortColumn.Quantity, false, 1)]
    [InlineData(RepriceSortColumn.Quantity, true, 2)]
    [InlineData(RepriceSortColumn.CurrentPrice, false, 2)]
    [InlineData(RepriceSortColumn.CurrentPrice, true, 1)]
    public void SortColumn_OrdersItsOwnValuesInEitherDirection(RepriceSortColumn column, bool descending, int firstSlot)
    {
        var rows = new[] { Row(1, "Twine", 2, 500), Row(2, "Cloth", 5, 200) };

        Array.Sort(rows, (left, right) => RepriceSort.Compare(left, right, column, descending));

        Assert.Equal(firstSlot, rows[0].Entry.ListingIndex);
    }

    [Theory]
    [InlineData(RepriceSortColumn.Item)]
    [InlineData(RepriceSortColumn.CurrentPrice)]
    public void AscendingPrice_PutsUnknownAfterKnown(RepriceSortColumn column)
    {
        var unknown = Row(0, "Twine", price: 0);
        var known = Row(1, "Twine", price: 100);

        Assert.True(RepriceSort.Compare(known, unknown, column) < 0);
    }

    private static (ListingEntry Entry, string ItemName) Row(
        int slot, string name, int quantity = 1, long price = 1, bool hq = false)
        => (new ListingEntry { ListingIndex = slot, Quantity = quantity, UnitPrice = price, IsHQ = hq }, name);
}
