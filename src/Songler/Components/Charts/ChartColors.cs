using Songler.Layout;

namespace Songler.Components.Charts;

/// <summary>Chart colours for MudChart palettes (which need real colour values, not CSS variables), taken from the app theme.</summary>
public static class ChartColors
{
    public static string Series(bool dark) => (dark ? AppTheme.Theme.PaletteDark.Secondary : AppTheme.Theme.PaletteLight.Secondary).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
    public static string Good(bool dark) => (dark ? AppTheme.Theme.PaletteDark.Success : AppTheme.Theme.PaletteLight.Success).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);

    /// <summary>Practice calendar ramp, none → most (same steps as the --hm-* CSS tokens).</summary>
    public static string[] Heat(bool dark) => dark
        ? ["#252523", "#184f95", "#256abf", "#3987e5", "#86b6ef"]
        : ["#ebeae5", "#b7d3f6", "#6da7ec", "#2a78d6", "#184f95"];
}
