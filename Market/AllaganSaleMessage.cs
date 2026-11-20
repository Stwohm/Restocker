using System.Text;
using System.Text.RegularExpressions;

namespace Restocker.Market;

/// <summary>Pure parser for AllaganMarket's English plain-text sale notification.</summary>
public static class AllaganSaleMessage
{
    private static readonly Regex Pattern = new(
        @"^You sold (?<qty>[1-9]\d*) (?<item>.+) for \uE049?(?<gil>\d[\d.,\u00A0\u202F ]*)$",
        RegexOptions.CultureInvariant);

    public static bool TryParse(string text, out int qty, out string itemName, out long grossGil)
    {
        qty = 0;
        itemName = string.Empty;
        grossGil = 0;
        if (string.IsNullOrEmpty(text)) return false;

        var match = Pattern.Match(text);
        if (!match.Success || !int.TryParse(match.Groups["qty"].Value, out qty)) return false;
        itemName = match.Groups["item"].Value;
        if (string.IsNullOrWhiteSpace(itemName)) return false;

        var raw = match.Groups["gil"].Value;
        var digits = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c is >= '0' and <= '9') digits.Append(c);
            else if (c is not ('.' or ',' or ' ' or '\u00A0' or '\u202F')) return false;
        }
        return digits.Length > 0 && long.TryParse(digits.ToString(), out grossGil) && grossGil > 0;
    }
}
