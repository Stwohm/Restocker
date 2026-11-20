namespace Restocker.Data;

/// <summary>One occupied RetainerMarket slot read straight from the game, not from a snapshot.</summary>
public readonly record struct LiveListing(int Slot, uint ItemId, bool IsHQ, int Quantity, long UnitPrice);
