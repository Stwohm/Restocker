using System.Collections.Generic;
using System.IO;
using Restocker.Market;

namespace Restocker.Tests;

public sealed class CostLedgerTests : IDisposable
{
    private const uint Item = 42;
    private static readonly DateTime Utc = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    private readonly string path = Path.Combine(Path.GetTempPath(), $"restocky-cost-ledger-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
    }

    [Fact]
    public void ConsumptionUsesAcquisitionOrder()
    {
        var ledger = new CostLedger();
        ledger.RecordLot(Item, false, 2, 200m, Utc.AddMinutes(1));
        ledger.RecordLot(Item, false, 2, 100m, Utc);

        Assert.Equal(100m, ledger.CurrentUnitCost(Item, false));
        Assert.Equal((400m, 0), ledger.Consume(Item, false, 3, Utc.AddMinutes(2)));
        Assert.Equal(200m, ledger.CurrentUnitCost(Item, false));
        Assert.Equal(1, ledger.RemainingUnits(Item, false));
    }

    [Fact]
    public void PartialConsumptionKeepsTheLot()
    {
        var ledger = new CostLedger();
        var lot = ledger.RecordLot(Item, false, 8, 150.25m, Utc);

        Assert.Equal((450.75m, 0), ledger.Consume(Item, false, 3, Utc));
        Assert.Equal(lot with { RemainingQuantity = 5 }, Assert.Single(ledger.GetLots(Item, false)));
    }

    [Fact]
    public void ConsumptionCountsUnitsWithoutCost()
    {
        var ledger = new CostLedger();
        ledger.RecordLot(Item, false, 2, 25m, Utc);

        Assert.Equal((50m, 3), ledger.Consume(Item, false, 5, Utc));
        Assert.Null(ledger.CurrentUnitCost(Item, false));
        Assert.Null(ledger.WeightedAverageCost(Item, false));
        Assert.Equal(0, ledger.RemainingUnits(Item, false));
        Assert.Empty(ledger.GetLots());
    }

    [Fact]
    public void ConsumptionUsesLotsAvailableAtTheGivenTime()
    {
        var ledger = new CostLedger();
        ledger.RecordLot(Item, false, 2, 40m, Utc.AddMinutes(1));

        Assert.Equal((0m, 1), ledger.Consume(Item, false, 1, Utc));
        Assert.Equal(2, ledger.RemainingUnits(Item, false));
    }

    [Fact]
    public void ItemAndQualityHaveIndependentLots()
    {
        var ledger = new CostLedger();
        ledger.RecordLot(Item, false, 2, 30m, Utc);
        ledger.RecordLot(Item, true, 3, 50m, Utc);
        ledger.RecordLot(Item + 1, false, 1, 70m, Utc);

        Assert.Equal((60m, 2), ledger.Consume(Item, false, 4, Utc));
        Assert.Equal(50m, ledger.CurrentUnitCost(Item, true));
        Assert.Equal(3, ledger.RemainingUnits(Item, true));
        Assert.Equal(70m, ledger.CurrentUnitCost(Item + 1, false));
    }

    [Fact]
    public void ManualLotsShareAcquisitionOrderWithMarketLots()
    {
        var ledger = new CostLedger();
        var manual = ledger.AddManualLot(Item, true, 3, 0m, Utc);
        var market = ledger.RecordLot(Item, true, 2, 80m, Utc);

        Assert.Equal("manual", manual.Source);
        Assert.Equal("market", market.Source);
        Assert.Equal((80m, 0), ledger.Consume(Item, true, 4, Utc));
        Assert.Equal(80m, ledger.CurrentUnitCost(Item, true));
    }

    [Fact]
    public void RemovingALotExposesTheNextLot()
    {
        var ledger = new CostLedger();
        var first = ledger.RecordLot(Item, false, 1, 70m, Utc);
        var second = ledger.AddManualLot(Item, false, 2, 40m, Utc.AddSeconds(1));

        Assert.True(ledger.RemoveLot(first.Id));
        Assert.False(ledger.RemoveLot(first.Id));
        Assert.Equal(second, Assert.Single(ledger.GetLots(Item, false)));
        Assert.Equal(second.UnitCost, ledger.CurrentUnitCost(Item, false));
    }

    [Fact]
    public void WeightedAverageUsesRemainingQuantities()
    {
        var ledger = new CostLedger();
        ledger.RecordLot(Item, false, 2, 10m, Utc);
        ledger.RecordLot(Item, false, 3, 30m, Utc.AddSeconds(1));

        Assert.Equal(22m, ledger.WeightedAverageCost(Item, false));
        ledger.Consume(Item, false, 1, Utc);
        Assert.Equal(25m, ledger.WeightedAverageCost(Item, false));
    }

    [Fact]
    public void SavedLotsRetainOrderAndQuantities()
    {
        var ledger = new CostLedger();
        var first = ledger.RecordLot(Item, true, 3, 101.25m, Utc);
        var second = ledger.AddManualLot(Item, true, 1, 120m, Utc);
        var removed = ledger.RecordLot(Item, false, 1, 40m, Utc);
        ledger.Consume(Item, true, 1, Utc);
        ledger.RemoveLot(removed.Id);
        ledger.SaveIfDirty(path);

        var warnings = new List<string>();
        var loaded = CostLedger.Load(path, warnings.Add);
        Assert.Empty(warnings);
        Assert.Equal(new[] { first with { RemainingQuantity = 2 }, second }, loaded.GetLots(Item, true));
        Assert.Empty(loaded.GetLots(Item, false));

        loaded.Consume(Item, true, 3, Utc);
        loaded.SaveIfDirty(path);
        Assert.Empty(CostLedger.Load(path, warnings.Add).GetLots());
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid json")]
    [InlineData("[{}]")]
    public void UnreadableLedgerStartsEmptyAndWarns(string? contents)
    {
        if (contents != null) File.WriteAllText(path, contents);
        var warnings = new List<string>();

        Assert.Empty(CostLedger.Load(path, warnings.Add).GetLots());
        Assert.Single(warnings);
    }

    [Fact]
    public void ConfirmationRecordsTheMatchingPurchase()
    {
        var ledger = new CostLedger();
        var pairing = new PurchasePairing(ledger);
        pairing.Request(Item, true, 4, 100, 21, Utc);
        Assert.Empty(ledger.GetLots());

        var confirmed = Utc.AddSeconds(1);
        var lot = Assert.IsType<CostLot>(pairing.Confirm(Item, 4, confirmed));
        Assert.Equal(confirmed, lot.Utc);
        Assert.Equal(Item, lot.ItemId);
        Assert.True(lot.IsHq);
        Assert.Equal(4, lot.RemainingQuantity);
        Assert.Equal(105.25m, lot.UnitCost);
        Assert.Equal("market", lot.Source);

        Assert.Null(pairing.Confirm(Item, 4, confirmed));
        Assert.Equal(lot, Assert.Single(ledger.GetLots()));
    }

    [Fact]
    public void ConfirmationRequiresMatchingItemAndQuantity()
    {
        var ledger = new CostLedger();
        var pairing = new PurchasePairing(ledger);
        pairing.Request(Item, false, 2, 100, 10, Utc);

        Assert.Null(pairing.Confirm(Item + 1, 2, Utc.AddSeconds(1)));
        Assert.Null(pairing.Confirm(Item, 3, Utc.AddSeconds(2)));
        Assert.Empty(ledger.GetLots());
        Assert.NotNull(pairing.Confirm(Item, 2, Utc.AddSeconds(3)));
    }

    [Fact]
    public void ConfirmationMatchesAnEncodedHighQualityItem()
    {
        var ledger = new CostLedger();
        var pairing = new PurchasePairing(ledger);
        pairing.Request(Item, false, 2, 100, 10, Utc);
        pairing.Request(Item, true, 2, 200, 20, Utc.AddSeconds(1));

        var lot = Assert.IsType<CostLot>(pairing.Confirm(Item + 1_000_000, 2, Utc.AddSeconds(2)));
        Assert.True(lot.IsHq);
        Assert.Equal(Item, lot.ItemId);
        Assert.Equal(210m, lot.UnitCost);
        Assert.Null(ledger.CurrentUnitCost(Item, false));
        Assert.Null(pairing.Confirm(Item + 1_000_000, 2, Utc.AddSeconds(3)));
    }

    [Fact]
    public void ExpiredRequestsDoNotProduceLots()
    {
        var ledger = new CostLedger();
        var pairing = new PurchasePairing(ledger);
        pairing.Request(Item, false, 2, 100, 10, Utc);

        Assert.Null(pairing.Confirm(Item, 2, Utc.AddSeconds(30)));
        Assert.Empty(ledger.GetLots());

        pairing.Request(Item, true, 2, 200, 20, Utc.AddSeconds(31));
        var lot = Assert.IsType<CostLot>(pairing.Confirm(Item, 2, Utc.AddSeconds(32)));
        Assert.True(lot.IsHq);
        Assert.Equal(210m, lot.UnitCost);
    }
}
