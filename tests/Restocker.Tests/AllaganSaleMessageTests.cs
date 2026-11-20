using Restocker.Market;

namespace Restocker.Tests;

public class AllaganSaleMessageTests
{
    [Theory]
    [InlineData("You sold 1 Everkeep Chest for \uE04975.739", 1, "Everkeep Chest", 75_739)]
    [InlineData("You sold 7 Grade 3 Thanalan Topsoil for \uE04922.393", 7, "Grade 3 Thanalan Topsoil", 22_393)]
    [InlineData("You sold 1 Interstellar Dhalmel Whistle for \uE049899.935", 1, "Interstellar Dhalmel Whistle", 899_935)]
    [InlineData("You sold 12 Test Item for \uE0491,234,567", 12, "Test Item", 1_234_567)]
    [InlineData("You sold 2 Test Item for \uE0491 234 567", 2, "Test Item", 1_234_567)]
    [InlineData("You sold 3 Test Item for \uE0491234567", 3, "Test Item", 1_234_567)]
    [InlineData("You sold 4 Test Item for 98,765", 4, "Test Item", 98_765)]
    public void Parses_allaganmarket_sale(string text, int qty, string itemName, long grossGil)
    {
        Assert.True(AllaganSaleMessage.TryParse(text, out var actualQty, out var actualName, out var actualGil));
        Assert.Equal(qty, actualQty);
        Assert.Equal(itemName, actualName);
        Assert.Equal(grossGil, actualGil);
    }

    [Theory]
    [InlineData("You have been undercut by Rival.")]
    [InlineData("[Restocky] done: 1 price changed")]
    [InlineData("The Test Item you put up for sale has sold for 1,000 gil (after fees).")]
    [InlineData("You sold 0 Test Item for \uE0491,000")]
    [InlineData("You sold 1 Test Item for \uE049")]
    public void Rejects_non_allaganmarket_sale(string text)
        => Assert.False(AllaganSaleMessage.TryParse(text, out _, out _, out _));
}
