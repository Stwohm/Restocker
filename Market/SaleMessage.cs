using System.Text.RegularExpressions;

namespace Restocker.Market;

/// <summary>
/// Pure part of the RetainerSale chat message ("The X you put up for sale in the Limsa Lominsa
/// markets has sold for 27,485 gil (after fees)."). The item comes from the message's item
/// payload; this only extracts the gil amount, for EN / JA / DE / FR clients.
/// </summary>
public static class SaleMessage
{
    private static readonly Regex Gil = new(@"(\d[\d,.   ]*)\s*(?:gil|ギル)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <returns>Gil received, or 0 when no amount was found.</returns>
    public static long ParseGil(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var matches = Gil.Matches(text);
        if (matches.Count == 0) return 0;
        var digits = Regex.Replace(matches[^1].Groups[1].Value, @"\D", "");
        return long.TryParse(digits, out var v) ? v : 0;
    }
}
