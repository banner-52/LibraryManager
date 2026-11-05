using System.Windows;
using System.Windows.Media;
using LibraryManager.Models;
using Microsoft.Win32;
using AppThemeMode = LibraryManager.Models.ThemeMode;

namespace LibraryManager.Services;

public sealed class ThemeService
{
    public void Apply(AppSettings settings)
    {
        var mode = settings.Theme == AppThemeMode.System ? GetSystemTheme() : settings.Theme;
        var palette = mode switch
        {
            AppThemeMode.Dark => CreatePalette("#FF171A21", "#FF20242D", "#FF292E39", "#FF3B82F6", "#FF1F2A44"),
            AppThemeMode.Light => CreatePalette("#FFF3F5F8", "#FFFFFFFF", "#FFFFFFFF", "#FF2563EB", "#FFE7ECF4"),
            AppThemeMode.Custom => CreatePalette(settings.CustomBackgroundColor, settings.CustomSurfaceColor, settings.CustomSurfaceColor, settings.CustomAccentColor, settings.CustomSurfaceColor),
            _ => CreatePalette("#FFF3F5F8", "#FFFFFFFF", "#FFFFFFFF", "#FF2563EB", "#FFE7ECF4")
        };

        SetColor("WindowBackgroundColor", palette.Window);
        SetColor("PanelBackgroundColor", WithOpacity(palette.Panel, settings.PanelOpacity));
        SetColor("CardBackgroundColor", palette.Card);
        SetColor("AlternateCardBackgroundColor", Shift(palette.Card, IsDark(palette.Card) ? 10 : -7));
        SetColor("WindowTextColor", palette.WindowText);
        SetColor("PrimaryTextColor", palette.SurfaceText);
        SetColor("SecondaryTextColor", palette.SecondaryText);
        SetColor("AccentColor", palette.Accent);
        SetColor("SelectionColor", palette.Accent);
        SetColor("HeaderBackgroundColor", palette.Header);
        SetColor("HeaderControlBackgroundColor", Shift(palette.Header, IsDark(palette.Header) ? 18 : -18));
        SetColor("HeaderTextColor", BestText(palette.Header));
        SetColor("HeaderSecondaryTextColor", MutedText(palette.Header));
        SetColor("BorderColor", palette.Border);
    }

    private static AppThemeMode GetSystemTheme()
    {
        try
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return Convert.ToInt32(value) == 0 ? AppThemeMode.Dark : AppThemeMode.Light;
        }
        catch { return AppThemeMode.Light; }
    }

    private static void SetColor(string key, string value)
    {
        if (ColorConverter.ConvertFromString(value) is not Color color) return;

        // Put the runtime override at application scope. DynamicResource lookup
        // resolves this before the values declared in Theme.xaml.
        Application.Current.Resources[key] = color;

        // Brushes created from DynamicResource colors are not reliably refreshed
        // when only the Color resource changes. Update the matching brush in place
        // so every existing control immediately receives the new palette.
        var brushKey = key.EndsWith("Color", StringComparison.Ordinal)
            ? key[..^"Color".Length] + "Brush"
            : null;

        if (brushKey is not null)
        {
            // Brushes loaded from a ResourceDictionary may be frozen by WPF and
            // cannot be modified in place. Replace the resource with a new brush.
            Application.Current.Resources[brushKey] = new SolidColorBrush(color);
        }
    }

    private static string WithOpacity(string color, double opacity)
    {
        if (ColorConverter.ConvertFromString(color) is not Color parsed) return color;
        parsed.A = (byte)(Math.Clamp(opacity, 0.2, 1.0) * 255);
        return parsed.ToString();
    }

    private static Palette CreatePalette(string window, string panel, string card, string accent, string header)
    {
        return new Palette(
            window,
            panel,
            card,
            BestText(window),
            BestText(card),
            MutedText(card),
            accent,
            header,
            Shift(card, IsDark(card) ? 34 : -28));
    }

    private static string Shift(string color, int amount)
    {
        if (ColorConverter.ConvertFromString(color) is not Color parsed) return color;
        return Color.FromArgb(parsed.A, Clamp(parsed.R + amount), Clamp(parsed.G + amount), Clamp(parsed.B + amount)).ToString();
    }

    private static string BestText(string background)
    {
        if (ColorConverter.ConvertFromString(background) is not Color color) return "#FF1F2937";
        return color.R * .299 + color.G * .587 + color.B * .114 > 150 ? "#FF1F2937" : "#FFF3F4F6";
    }

    private static string MutedText(string background) => IsDark(background) ? "#FFCBD5E1" : "#FF64748B";
    private static bool IsDark(string color)
    {
        if (ColorConverter.ConvertFromString(color) is not Color parsed) return false;
        return parsed.R * .299 + parsed.G * .587 + parsed.B * .114 <= 150;
    }
    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private sealed record Palette(string Window, string Panel, string Card, string WindowText, string SurfaceText, string SecondaryText, string Accent, string Header, string Border);
}
