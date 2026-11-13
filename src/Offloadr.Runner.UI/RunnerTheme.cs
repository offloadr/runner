using MudBlazor;

namespace Offloadr.Runner.UI;

/// <summary>
/// The look of offloadr.studio: Blue Ribbon for actions, Purple Heart only as an accent, calm
/// surfaces, and a deep navy dark mode rather than an inverted light one. What MudTheme cannot
/// express (card and panel radii, glass surfaces, soft shadows, pill buttons) is in tokens.css.
/// </summary>
public static class RunnerTheme
{
    /// <summary>The page colors, for the host to paint behind the web view before it renders.</summary>
    public const string LightPage = "#f4f7fb";

    public const string DarkPage = "#07111f";

    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1753d7",
            PrimaryContrastText = "#ffffff",
            Secondary = "#7a00e2",
            SecondaryContrastText = "#ffffff",
            Info = "#3f77ed",
            Success = "#16a34a",
            Warning = "#d97706",
            Error = "#ef4444",
            Dark = "#0d1430",
            Background = LightPage,
            BackgroundGray = "#eef2ff",
            Surface = "#ffffff",
            AppbarBackground = "rgba(255, 255, 255, 0.76)",
            AppbarText = "#0d1430",
            DrawerBackground = "#ffffff",
            TextPrimary = "#102033",
            TextSecondary = "#5e708a",
            ActionDefault = "#52618f",
            LinesDefault = "rgba(109, 128, 156, 0.18)",
            LinesInputs = "rgba(109, 128, 156, 0.42)",
            TableLines = "rgba(109, 128, 156, 0.18)",
            Divider = "rgba(109, 128, 156, 0.18)",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#3f77ed",
            PrimaryContrastText = "#ffffff",
            Secondary = "#9c3fff",
            SecondaryContrastText = "#ffffff",
            Info = "#5084ef",
            Success = "#22c55e",
            Warning = "#f59e0b",
            Error = "#f87171",
            Dark = "#070b1d",
            Background = DarkPage,
            BackgroundGray = "#0b182a",
            Surface = "#0d1d34",
            AppbarBackground = "rgba(11, 24, 42, 0.8)",
            AppbarText = "#edf4ff",
            DrawerBackground = "#0d1d34",
            TextPrimary = "#edf4ff",
            TextSecondary = "#9fb3cc",
            ActionDefault = "#aeb8dd",
            LinesDefault = "rgba(143, 174, 219, 0.18)",
            LinesInputs = "rgba(143, 174, 219, 0.36)",
            TableLines = "rgba(143, 174, 219, 0.18)",
            Divider = "rgba(143, 174, 219, 0.18)",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Avenir Next", "Segoe UI Variable Text", "Segoe UI", "system-ui", "sans-serif"],
            },
            H4 = new H4Typography { FontFamily = DisplayFont, FontWeight = "650", LetterSpacing = "-0.02em" },
            H5 = new H5Typography { FontFamily = DisplayFont, FontWeight = "650", LetterSpacing = "-0.01em" },
            H6 = new H6Typography { FontFamily = DisplayFont, FontWeight = "600" },
            Button = new ButtonTypography { FontWeight = "600", TextTransform = "none", LetterSpacing = "0" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "12px" },
    };

    private static readonly string[] DisplayFont = ["Avenir Next", "Segoe UI Variable Display", "Segoe UI", "system-ui", "sans-serif"];
}
