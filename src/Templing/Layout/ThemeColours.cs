using System.Globalization;
using System.Text;
using MudBlazor;
using MudBlazor.Utilities;
using Templing.Models;

namespace Templing.Layout;

/// <summary>
/// The colours a user can change (Settings > Colours), with their built-in values. Each slot feeds the MudBlazor palette
/// and the CSS tokens in templing.css, so a custom colour reaches both MudBlazor components and the app's own styles.
/// </summary>
public static class ThemeColours
{
    public record Slot(string Key, string Label, string Light, string Dark);

    public static readonly Slot[] Slots =
    [
        new("accent", "Accent", "#0361c3", "#0361c3"),
        new("data", "Charts and rests", "#6a449f", "#6a449f"),
        new("good", "Goals reached", "#0a8a0a", "#0ca30c"),
        new("warning", "Warnings", "#c98500", "#fab219"),
        new("error", "Errors and deletes", "#d03b3b", "#e66767"),
        new("background", "Background", "#f6f5f2", "#0f0f0e"),
        new("card", "Cards", "#fcfcfb", "#1a1a19"),
        new("text", "Text", "#0b0b0b", "#f0efec"),
    ];

    /// <summary>
    /// Quick picks for a slot's picker (MudColorPicker shows the first five when its preview dot is clicked):
    /// neutrals for the surfaces and text, colours for the rest.
    /// </summary>
    public static MudColor[] Palette(string key, bool dark) => (key switch
    {
        "background" => dark ? ["#0f0f0e", "#121417", "#14110f", "#101512", "#000000"] : ["#f6f5f2", "#f4f6f8", "#faf6f0", "#f2f5f1", "#ffffff"],
        "card" => dark ? ["#1a1a19", "#1b1e23", "#1f1b18", "#1a1f1b", "#202020"] : ["#fcfcfb", "#ffffff", "#fffdf9", "#fbfdfb", "#f6f7f9"],
        "text" => dark ? ["#f0efec", "#ffffff", "#e6e8eb", "#f3ece2", "#d9d9d4"] : ["#0b0b0b", "#1f2328", "#2b2420", "#000000", "#33322f"],
        _ => new[] { "#0361c3", "#6a449f", "#14a3a3", "#f0763f", "#d6589b" },
    }).Select(c => new MudColor(c)).ToArray();

    /// <summary>The colour in use for a slot: the user's, else the built-in one.</summary>
    public static string Get(CustomColours? custom, string key, bool dark)
    {
        if (custom != null && custom.For(dark).TryGetValue(key, out var c) && !string.IsNullOrWhiteSpace(c)) return c;
        var slot = Slots.First(s => s.Key == key);
        return dark ? slot.Dark : slot.Light;
    }

    /// <summary>The user picked a colour for this slot that differs from the built-in one (a saved value can match it once the defaults change).</summary>
    public static bool IsCustom(CustomColours? custom, string key, bool dark)
    {
        if (custom == null || !custom.For(dark).TryGetValue(key, out var c)) return false;
        var slot = Slots.First(s => s.Key == key);
        return !string.Equals(c, dark ? slot.Dark : slot.Light, StringComparison.OrdinalIgnoreCase);
    }

    public static bool AnyCustom(CustomColours? custom) => Slots.Any(s => IsCustom(custom, s.Key, false) || IsCustom(custom, s.Key, true));

    /// <summary>The app theme with the user's colours laid over the built-in palettes.</summary>
    public static MudTheme Theme(CustomColours? custom)
    {
        var t = AppTheme.Create();
        if (custom == null) return t;
        Apply(t.PaletteLight, custom, false);
        Apply(t.PaletteDark, custom, true);
        return t;
    }

    static void Apply(Palette p, CustomColours custom, bool dark)
    {
        string? C(string key) => custom.For(dark).TryGetValue(key, out var c) ? c : null;
        if (C("accent") is { } a) p.Primary = a;
        if (C("data") is { } d) p.Secondary = d;
        if (C("good") is { } g) p.Success = g;
        if (C("warning") is { } w) p.Warning = w;
        if (C("error") is { } e) p.Error = e;
        if (C("background") is { } bg) { p.Background = bg; p.DrawerBackground = bg; p.AppbarBackground = bg + "ee"; if (dark) p.BackgroundGray = Mix(bg, "#ffffff", .03); }
        if (C("card") is { } card) p.Surface = card;
        if (C("text") is { } text) { p.TextPrimary = text; p.AppbarText = text; }
    }

    /// <summary>
    /// CSS that sets the app's tokens from the user's colours, for the same selectors as templing.css (rendered after it, so it wins).
    /// Derived tokens (accent-soft, heatmap shades, second card shade) follow their slot through color-mix.
    /// </summary>
    public static string Css(CustomColours? custom)
    {
        if (custom == null) return "";
        var sb = new StringBuilder();
        foreach (var dark in new[] { false, true })
        {
            var map = custom.For(dark);
            if (map.Count == 0) continue;
            var mode = dark ? "dark" : "light";
            sb.Append($".theme-{mode}, :root:has(.theme-{mode}) {{");
            string? C(string key) => map.TryGetValue(key, out var c) ? c : null;
            if (C("accent") is { } a) sb.Append($"--accent:{a};--accent-soft:color-mix(in srgb,{a} {(dark ? 14 : 12)}%,transparent);");
            if (C("data") is { } d)
            {
                var bg = dark ? "#000" : "#fff";
                sb.Append($"--series:{d};--rest:{d};--hm-3:{d};");
                sb.Append($"--hm-1:color-mix(in srgb,{d} 35%,{bg});--hm-2:color-mix(in srgb,{d} 70%,{bg});");
                sb.Append($"--hm-4:color-mix(in srgb,{d} 60%,{(dark ? "#fff" : "#000")});");
            }
            if (C("good") is { } g) sb.Append($"--good:{g};");
            if (C("card") is { } card) sb.Append($"--card:{card};--card-2:color-mix(in srgb,{card} 94%,{(dark ? "#fff" : "#000")});");
            if (C("text") is { } text) sb.Append($"--ink:{text};--tip-bg:{text};");
            sb.Append('}');
        }
        return sb.ToString();
    }

    /// <summary>Mixes two #rrggbb colours: <paramref name="amount"/> of <paramref name="b"/> into <paramref name="a"/>.</summary>
    static string Mix(string a, string b, double amount)
    {
        var ca = new MudColor(a); var cb = new MudColor(b);
        int M(byte x, byte y) => (int)Math.Round(x + (y - x) * amount);
        return string.Create(CultureInfo.InvariantCulture, $"#{M(ca.R, cb.R):x2}{M(ca.G, cb.G):x2}{M(ca.B, cb.B):x2}");
    }
}
