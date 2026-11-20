using System.Collections.Generic;
using System.Linq;
using Restocker.Common;

namespace Restocker.Tests;

public class DialogItemNameTests
{
    private static readonly Dictionary<string, uint> Items = new()
    {
        ["Craftsman's Competence Materia V"] = 5703,
        ["Craftsman's Competence Materia VI"] = 18025,
        ["Craftsman's Competence Materia XII"] = 41778,
        ["Heavens' Eye Materia VII"] = 25187,
        ["Heavens' Eye Materia VIII"] = 26728,
        ["Timeworn Zonureskin Map"] = 26745,
        ["Ice"] = 1,
    };

    private static readonly List<KeyValuePair<string, uint>> ByLen =
        Items.OrderByDescending(p => p.Key.Length).ToList();

    private static uint Resolve(string raw) => DialogItemName.Match(DialogItemName.Clean(raw), Items, ByLen);

    [Theory]
    // Raw strings as they appeared in dalamud.log on 2026-11-18 (macro remnants included).
    [InlineData("H\u0002%I\u0003&Craftsman's Competence Materia VIH", 5703u)]   // was resolved as VI (18025)
    [InlineData("H\u0002%I\u0003&Heavens' Eye Materia VIIIH", 25187u)]        // was resolved as VIII (26728)
    public void Raw_macro_remnants_never_resolve_to_the_next_tier(string raw, uint realItem)
    {
        var id = Resolve(raw);
        // The boundary rule refuses "VI"/"VIII" (followed by a letter). Either it finds the real
        // item or it gives up (0 = skip row); it must never pick a different item.
        Assert.True(id == 0 || id == realItem, $"resolved {id}");
    }

    [Theory]
    [InlineData("Craftsman's Competence Materia V", 5703u)]
    [InlineData("Craftsman's Competence Materia VI", 18025u)]
    [InlineData("Craftsman's Competence Materia XII", 41778u)]
    [InlineData("Heavens' Eye Materia VII", 25187u)]
    [InlineData("Timeworn Zonureskin Map", 26745u)]
    public void Clean_parsed_names_resolve_exactly(string text, uint expected)
        => Assert.Equal(expected, Resolve(text));

    [Fact]
    public void Substring_inside_a_word_is_refused()
        => Assert.Equal(0u, Resolve("Iced Tea"));

    [Fact]
    public void Substring_with_word_boundaries_is_accepted()
        => Assert.Equal(26745u, Resolve("2 Timeworn Zonureskin Map"));

    [Fact]
    public void Hq_glyph_is_detected_and_stripped()
    {
        Assert.True(DialogItemName.IsHq("Heavens' Eye Materia VII"));
        Assert.Equal("Heavens' Eye Materia VII", DialogItemName.Clean("Heavens' Eye Materia VII"));
    }
}
