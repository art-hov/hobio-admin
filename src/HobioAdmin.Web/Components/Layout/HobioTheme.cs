using MudBlazor;

namespace HobioAdmin.Web.Components.Layout;

/// <summary>Colors and type taken from hobio.ca (css/site.css).</summary>
public static class HobioTheme
{
    public const string Green = "#27A74A";
    public const string GreenDark = "#1F863B";
    public const string Text = "#333333";
    public const string TextMuted = "#6C757D";
    public const string Slate = "#495057";
    public const string Background = "#F8F9FA";
    public const string Border = "#DEE2E6";
    public const string Amber = "#FFC107";
    public const string Red = "#C53030";

    /// <summary>Chart series colors: brand green first, then neutral and accent tones.</summary>
    public static readonly string[] ChartPalette = [Green, Slate, "#7BC68F", Amber, GreenDark, "#ADB5BD"];

    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = Green,
            PrimaryDarken = GreenDark,
            Secondary = Slate,
            Tertiary = Amber,
            Success = Green,
            Warning = Amber,
            Error = Red,
            Background = Background,
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = Text,
            DrawerBackground = "#FFFFFF",
            DrawerText = Slate,
            DrawerIcon = Slate,
            TextPrimary = Text,
            TextSecondary = TextMuted,
            LinesDefault = Border,
            TableLines = Border,
            Divider = Border,
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Segoe UI", "Tahoma", "Geneva", "Verdana", "sans-serif"],
            },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
            DrawerWidthLeft = "240px",
        },
    };
}
