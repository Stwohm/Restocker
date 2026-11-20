using System.Collections.Generic;
using System.Text;

namespace Restocker.Common;

/// <summary>
/// Pure item-name matching for the RetainerSell dialog title. Extracted from Executor so it can be tested.
///
/// Fork fix: upstream read the node text as raw bytes, so SeString macro remnants survived the
/// control-char strip (e.g. "H\u0002%I\u0003&Craftsman's Competence Materia VIH"). The longest-substring
/// fallback then matched the NEXT tier ("Materia V" + "IH" -> "Materia VI"), the market answer for the real
/// item was ignored as "unexpected" and the row timed out. The executor now parses the SeString properly;
/// this matcher additionally refuses substring hits that run into a letter or digit.
/// </summary>
public static class DialogItemName
{
    public const char HqGlyph = '';

    /// <summary>Removes control and private-use characters (HQ glyph, icons) and trims.</summary>
    public static string Clean(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c < 0x20) continue;
            if (c >= 0xE000 && c <= 0xF8FF) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    public static bool IsHq(string raw) => raw.IndexOf(HqGlyph) >= 0;

    /// <param name="exact">name -> item id.</param>
    /// <param name="byLengthDesc">same entries, longest name first.</param>
    /// <returns>Item id, or 0 when nothing matches safely.</returns>
    public static uint Match(
        string cleaned,
        IReadOnlyDictionary<string, uint> exact,
        IReadOnlyList<KeyValuePair<string, uint>> byLengthDesc)
    {
        if (string.IsNullOrEmpty(cleaned)) return 0;
        if (exact.TryGetValue(cleaned, out var id)) return id;

        foreach (var kv in byLengthDesc)
        {
            if (kv.Key.Length < 3) break;
            var idx = cleaned.IndexOf(kv.Key, System.StringComparison.Ordinal);
            while (idx >= 0)
            {
                var end = idx + kv.Key.Length;
                var beforeOk = idx == 0 || !char.IsLetterOrDigit(cleaned[idx - 1]);
                var afterOk = end >= cleaned.Length || !char.IsLetterOrDigit(cleaned[end]);
                if (beforeOk && afterOk) return kv.Value;
                idx = cleaned.IndexOf(kv.Key, idx + 1, System.StringComparison.Ordinal);
            }
        }
        return 0;
    }
}
