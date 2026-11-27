using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.UI;

namespace Iris_MediaPlayer.Services;

/// <summary>
/// One Iris colour scheme, named after a kind of obsidian. Every colour the app uses comes from
/// these: backgrounds and cards, text, and the accent with its lighter and darker steps.
/// </summary>
internal sealed record IrisTheme(
    string Id, string Name, string Description,
    Color Background, Color Window, Color Surface, Color SurfaceHover, Color Raised, Color Field,
    Color Glass, Color Sheet,
    Color Text, Color TextSecondary, Color TextTertiary, Color TextMuted, Color CaptionInactive, Color PlayerIcon,
    Color Accent, Color AccentHover, Color AccentText, Color AccentLight, Color OnAccent,
    Color SystemAccent, Color SystemDark1, Color SystemDark2, Color SystemDark3);

/// <summary>
/// The colour schemes and switching between them. Pages use the Iris brushes from Styles/Iris.xaml
/// as StaticResources, which share one brush object each, so changing a brush's colour here
/// repaints every page at once, with no restart. The same goes for the WinUI accent brushes that
/// sliders, toggles and progress rings draw with. Fades over artwork are drawn with gradients
/// written into each page; <see cref="TrackFades"/> recolours those.
/// </summary>
internal static class Themes
{
    private const string ThemeKey = "Theme";

    public static readonly IrisTheme Obsidian = new(
        "Obsidian", "Obsidian", "Black glass with an indigo sheen",
        Background: C("09090B"), Window: C("0D0E11"), Surface: C("111216"), SurfaceHover: C("15161B"), Raised: C("17181D"),
        Field: C("141519"), Glass: C("121318"), Sheet: C("0E0F13"),
        Text: C("F4F4F6"), TextSecondary: C("A1A1AA"), TextTertiary: C("8A8A93"), TextMuted: C("C7C7CE"),
        CaptionInactive: C("5A5A62"), PlayerIcon: C("E4E4E9"),
        Accent: C("8B93FF"), AccentHover: C("A2A8FF"), AccentText: C("AEB4FF"), AccentLight: C("C9CDFF"), OnAccent: C("0B0B12"),
        SystemAccent: C("7C84F2"), SystemDark1: C("6A72E0"), SystemDark2: C("565DC4"), SystemDark3: C("4248A3"));

    public static readonly IrisTheme GoldenSheen = new(
        "GoldenSheen", "Golden Sheen", "Warm charcoal with a metallic gold glint",
        Background: C("12110E"), Window: C("171612"), Surface: C("1E1B16"), SurfaceHover: C("221F1A"), Raised: C("24211C"),
        Field: C("211E19"), Glass: C("1F1C17"), Sheet: C("191713"),
        Text: C("F2F1EC"), TextSecondary: C("A6A29E"), TextTertiary: C("918E8A"), TextMuted: C("C8C5C3"),
        CaptionInactive: C("66625E"), PlayerIcon: C("E3E1E0"),
        Accent: C("D4AF37"), AccentHover: C("DABB55"), AccentText: C("DFC369"), AccentLight: C("EAD79B"), OnAccent: C("0E0E0B"),
        SystemAccent: C("BF9E32"), SystemDark1: C("AA8C2C"), SystemDark2: C("907725"), SystemDark3: C("75601E"));

    public static readonly IrisTheme Mahogany = new(
        "Mahogany", "Mahogany", "Espresso black swirled with rust",
        Background: C("140E0D"), Window: C("1A1211"), Surface: C("221715"), SurfaceHover: C("261B19"), Raised: C("281D1B"),
        Field: C("251A18"), Glass: C("231816"), Sheet: C("1C1312"),
        Text: C("F2EDED"), TextSecondary: C("A59BA0"), TextTertiary: C("90868C"), TextMuted: C("C7C0C5"),
        CaptionInactive: C("655B60"), PlayerIcon: C("E3DEE1"),
        Accent: C("C9644A"), AccentHover: C("D17B65"), AccentText: C("D68B77"), AccentLight: C("E4B2A4"), OnAccent: C("100B0A"),
        SystemAccent: C("B55A43"), SystemDark1: C("A1503B"), SystemDark2: C("894432"), SystemDark3: C("6F3729"));

    public static readonly IrisTheme SilverSheen = new(
        "SilverSheen", "Silver Sheen", "Smoky slate with a brushed silver lustre",
        Background: C("0F1114"), Window: C("14161A"), Surface: C("191D22"), SurfaceHover: C("1D2126"), Raised: C("1F2328"),
        Field: C("1C2025"), Glass: C("1A1E23"), Sheet: C("15181C"),
        Text: C("F2F2F4"), TextSecondary: C("A2A2AC"), TextTertiary: C("8C8D96"), TextMuted: C("C6C6CE"),
        CaptionInactive: C("5F6068"), PlayerIcon: C("E2E3E8"),
        Accent: C("AEB8C4"), AccentHover: C("BAC3CD"), AccentText: C("C2CAD3"), AccentLight: C("D6DCE2"), OnAccent: C("0C0E10"),
        SystemAccent: C("9DA6B0"), SystemDark1: C("8B939D"), SystemDark2: C("767D85"), SystemDark3: C("60656C"));

    public static IReadOnlyList<IrisTheme> All { get; } = [Obsidian, GoldenSheen, Mahogany, SilverSheen];

    public static IrisTheme Current { get; private set; } = Obsidian;

    /// <summary>Raised on the UI thread after a new theme has been applied.</summary>
    public static event Action? Changed;

    /// <summary>The theme saved in Settings, applied once at startup before any window opens.</summary>
    public static void ApplySaved()
    {
        var id = AppStorage.GetString(ThemeKey);
        Apply(All.FirstOrDefault(theme => theme.Id == id) ?? Obsidian, save: false);
    }

    public static void Apply(IrisTheme theme, bool save = true)
    {
        Current = theme;
        if (save)
            AppStorage.SetString(ThemeKey, theme.Id);

        var resources = Application.Current.Resources;

        // Iris's own brushes (Styles/Iris.xaml).
        SetBrush(resources, "IrisWindowBrush", theme.Window);
        SetBrush(resources, "IrisBackgroundBrush", theme.Background);
        SetBrush(resources, "IrisSurfaceBrush", theme.Surface);
        SetBrush(resources, "IrisSurfaceHoverBrush", theme.SurfaceHover);
        SetBrush(resources, "IrisRaisedBrush", theme.Raised);
        SetBrush(resources, "IrisFieldBrush", theme.Field);
        SetBrush(resources, "IrisThumbnailBrush", theme.Raised);
        SetBrush(resources, "IrisTextBrush", theme.Text);
        SetBrush(resources, "IrisTextSecondaryBrush", theme.TextSecondary);
        SetBrush(resources, "IrisTextTertiaryBrush", theme.TextTertiary);
        SetBrush(resources, "IrisTextMutedBrush", theme.TextMuted);
        SetBrush(resources, "IrisAccentBrush", theme.Accent);
        SetBrush(resources, "IrisAccentHoverBrush", theme.AccentHover);
        SetBrush(resources, "IrisAccentTextBrush", theme.AccentText);
        SetBrush(resources, "IrisAccentLightBrush", theme.AccentLight);
        SetBrush(resources, "IrisAccentSoftBrush", WithAlpha(theme.Accent, 0x24));
        SetBrush(resources, "IrisAccentStrokeBrush", WithAlpha(theme.Accent, 0x8C));
        SetBrush(resources, "IrisAccentHoverStrokeBrush", WithAlpha(theme.Accent, 0x59));
        SetBrush(resources, "IrisAccentFocusStrokeBrush", WithAlpha(theme.Accent, 0x99));
        SetBrush(resources, "IrisOnAccentBrush", theme.OnAccent);
        SetBrush(resources, "IrisOnLightBrush", theme.OnAccent);
        SetBrush(resources, "IrisLightBrush", theme.Text);
        SetBrush(resources, "IrisPlayerIconBrush", theme.PlayerIcon);
        SetBrush(resources, "IrisControlsGlassBrush", WithAlpha(theme.Sheet, 0x8C));
        SetBrush(resources, "IrisSearchRestBrush", WithAlpha(theme.Raised, 0x8C));
        SetBrush(resources, "IrisSearchHoverBrush", WithAlpha(theme.Raised, 0xB3));
        SetBrush(resources, "IrisSearchFocusBrush", WithAlpha(theme.Raised, 0xE6));
        SetBrush(resources, "IrisPillBrush", WithAlpha(theme.Surface, 0xB3));
        SetBrush(resources, "IrisToastBrush", WithAlpha(theme.Surface, 0xE6));
        SetAcrylic(resources, "IrisGlassBrush", theme.Glass, 0xF0);
        SetAcrylic(resources, "IrisSheetBrush", theme.Sheet, 0xF7);

        // The accent WinUI's own controls use: Windows' accent colours, replaced in Iris.xaml's theme
        // dictionaries for controls made from now on, and the brushes already made from them.
        var light2 = theme.Accent;
        var light3 = theme.AccentText;
        foreach (var dictionary in resources.MergedDictionaries)
        {
            foreach (var themeDictionary in dictionary.ThemeDictionaries.Values.OfType<ResourceDictionary>())
            {
                if (!themeDictionary.ContainsKey("SystemAccentColor"))
                    continue;
                themeDictionary["SystemAccentColor"] = theme.SystemAccent;
                themeDictionary["SystemAccentColorLight1"] = theme.Accent;
                themeDictionary["SystemAccentColorLight2"] = light2;
                themeDictionary["SystemAccentColorLight3"] = light3;
                themeDictionary["SystemAccentColorDark1"] = theme.SystemDark1;
                themeDictionary["SystemAccentColorDark2"] = theme.SystemDark2;
                themeDictionary["SystemAccentColorDark3"] = theme.SystemDark3;
            }
        }
        SetBrush(resources, "AccentFillColorDefaultBrush", light2);
        SetBrush(resources, "AccentFillColorSecondaryBrush", light2);
        SetBrush(resources, "AccentFillColorTertiaryBrush", light2);
        SetBrush(resources, "AccentTextFillColorPrimaryBrush", light3);
        SetBrush(resources, "AccentTextFillColorSecondaryBrush", light3);
        SetBrush(resources, "AccentTextFillColorTertiaryBrush", light2);
        SetBrush(resources, "SystemControlHighlightAccentBrush", theme.SystemAccent);
        SetBrush(resources, "SystemControlForegroundAccentBrush", theme.SystemAccent);
        SetBrush(resources, "SystemControlBackgroundAccentBrush", theme.SystemAccent);
        SetBrush(resources, "SystemControlHighlightAltAccentBrush", theme.SystemAccent);
        SetBrush(resources, "SystemAccentColorBrush", theme.SystemAccent);

        Changed?.Invoke();
    }

    /// <summary>
    /// Recolours the fades a page draws over artwork (gradients from the background colour to
    /// clear) now, whenever it's shown again, and when the theme changes while it's showing.
    /// </summary>
    public static void TrackFades(FrameworkElement page)
    {
        void Recolor() => RecolorFades(page);
        page.Loaded += (_, _) =>
        {
            Recolor();
            Changed -= Recolor;
            Changed += Recolor;
        };
        page.Unloaded += (_, _) => Changed -= Recolor;
    }

    /// <summary>Tints the window's caption buttons (minimise, maximise, close) to match.</summary>
    public static void ApplyCaptionColors(Window window)
    {
        var bar = window.AppWindow.TitleBar;
        bar.ButtonForegroundColor = Current.TextSecondary;
        bar.ButtonInactiveForegroundColor = Current.CaptionInactive;
        bar.ButtonHoverForegroundColor = Current.Text;
        bar.ButtonPressedForegroundColor = Current.Text;
    }

    private static void RecolorFades(DependencyObject element)
    {
        var brush = element switch
        {
            Panel panel => panel.Background,
            Border border => border.Background,
            Shape shape => shape.Fill,
            Control control => control.Background,
            _ => null
        };
        if (brush is GradientBrush gradient)
        {
            foreach (var stop in gradient.GradientStops)
            {
                if (IsThemeBackground(stop.Color))
                    stop.Color = WithAlpha(Current.Background, stop.Color.A);
            }
        }

        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var index = 0; index < count; index++)
            RecolorFades(VisualTreeHelper.GetChild(element, index));
    }

    private static bool IsThemeBackground(Color color)
        => All.Any(theme => theme.Background.R == color.R && theme.Background.G == color.G && theme.Background.B == color.B);

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        if (resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            brush.Color = color;
    }

    private static void SetAcrylic(ResourceDictionary resources, string key, Color tint, byte fallbackAlpha)
    {
        if (resources.TryGetValue(key, out var value) && value is AcrylicBrush brush)
        {
            brush.TintColor = tint;
            brush.FallbackColor = WithAlpha(tint, fallbackAlpha);
        }
    }

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color C(string hex)
        => Color.FromArgb(0xFF,
            byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber),
            byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber),
            byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber));
}
