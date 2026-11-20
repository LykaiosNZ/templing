using MudBlazor;

namespace Songler.Layout;

public static class AppTheme
{
    private static readonly string[] Font = ["Inter", "system-ui", "-apple-system", "Segoe UI", "Roboto", "sans-serif"];

    /// <summary>A fresh copy of the built-in theme (ThemeColours.Theme lays the user's colours over it). Keep in step with ThemeColours.Slots.</summary>
    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#0361c3",
            Secondary = "#6a449f",
            Tertiary = "#1baf7a",
            Background = "#f6f5f2",
            Surface = "#fcfcfb",
            AppbarBackground = "#fcfcfbee",
            AppbarText = "#0b0b0b",
            DrawerBackground = "#f6f5f2",
            DrawerText = "#33322f",
            DrawerIcon = "#52514e",
            TextPrimary = "#0b0b0b",
            TextSecondary = "#52514e",
            LinesDefault = "#e1e0d9",
            TableLines = "#e9e8e2",
            Divider = "#e1e0d9",
            ActionDefault = "#52514e",
            Success = "#0a8a0a",
            Warning = "#c98500",
            Error = "#d03b3b",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#0361c3",
            Secondary = "#6a449f",
            Tertiary = "#199e70",
            Background = "#0f0f0e",
            BackgroundGray = "#161615",
            Surface = "#1a1a19",
            AppbarBackground = "#0f0f0eee",
            AppbarText = "#f0efec",
            DrawerBackground = "#0f0f0e",
            DrawerText = "#c3c2b7",
            DrawerIcon = "#a3a29a",
            TextPrimary = "#f0efec",
            TextSecondary = "#c3c2b7",
            TextDisabled = "#6f6e69",
            ActionDefault = "#c3c2b7",
            LinesDefault = "#2c2c2a",
            LinesInputs = "#4a4a46",
            TableLines = "#262624",
            Divider = "#2c2c2a",
            Success = "#0ca30c",
            Warning = "#fab219",
            Error = "#e66767",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Font },
            H1 = new H1Typography { FontFamily = Font, FontWeight = "800" },
            H2 = new H2Typography { FontFamily = Font, FontWeight = "800" },
            H3 = new H3Typography { FontFamily = Font, FontWeight = "700" },
            H4 = new H4Typography { FontFamily = Font, FontWeight = "700", LetterSpacing = "-.02em" },
            H5 = new H5Typography { FontFamily = Font, FontWeight = "700", LetterSpacing = "-.01em" },
            H6 = new H6Typography { FontFamily = Font, FontWeight = "600" },
            Button = new ButtonTypography { FontFamily = Font, FontWeight = "600", TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "10px", DrawerWidthLeft = "232px" },
    };
}
