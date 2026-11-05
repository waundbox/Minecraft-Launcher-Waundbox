using System.Windows;
using System.Windows.Media;
using Launcher.Core.Configuration;

namespace Launcher.UI.Services;

/// <summary>Итоговые цвета оформления: фон, основной цвет и признак светлой темы.</summary>
internal sealed record ThemePalette(bool IsLight, Color Background, Color Accent, bool Liquid = false);

/// <summary>
/// Тема интерфейса: светлая/тёмная, основной цвет, цвет фона. Кисти подменяются в ресурсах приложения,
/// а страницы используют их через DynamicResource, поэтому изменения видны сразу, без перезапуска.
/// Цвет текста выбирается по яркости фона, поэтому текст читается при любом фоне.
/// </summary>
internal static class ThemeService
{
    public const string Dark = "dark";
    public const string Light = "light";

    public static readonly Color DarkBackground = Color.FromRgb(0x0C, 0x0C, 0x0F);
    public static readonly Color LightBackground = Color.FromRgb(0xF2, 0xF3, 0xF6);
    public static readonly Color DarkAccent = Color.FromRgb(0x8C, 0xF0, 0xB8);
    public static readonly Color LightAccent = Color.FromRgb(0x1E, 0x9E, 0x6A);

    public static ThemePalette Current { get; private set; } = new(false, DarkBackground, DarkAccent);

    /// <summary>Тема применена (из любого потока события приходят в потоке интерфейса).</summary>
    public static event Action<ThemePalette>? Applied;

    public static bool IsLightTheme(string? theme) => string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase);

    public static ThemePalette Resolve(LauncherSettings settings)
    {
        var lightTheme = IsLightTheme(settings.Theme);
        var background = ParseOr(settings.BackgroundColor, lightTheme ? LightBackground : DarkBackground);
        var accent = ParseOr(settings.AccentColor, lightTheme ? LightAccent : DarkAccent);
        // Светлая ли поверхность — решает фактическая яркость фона: так текст читается и при своём цвете фона.
        return new ThemePalette(Luminance(background) > 0.55, background, accent, settings.LiquidGlass);
    }

    public static void Apply(LauncherSettings settings)
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => Apply(settings));
            return;
        }

        var palette = Resolve(settings);
        if (palette == Current && _applied) return;
        _applied = true;
        Current = palette;

        var light = palette.IsLight;
        var res = app.Resources;
        var accent = palette.Accent;
        var bg = palette.Background;

        Set(res, "AccentColor", accent);
        SetBrush(res, "AccentBrush", accent);
        SetBrush(res, "AccentSoftBrush", Color.FromArgb(light ? (byte)0x30 : (byte)0x26, accent.R, accent.G, accent.B));
        SetBrush(res, "AccentOnBrush", Luminance(accent) > 0.6 ? Color.FromRgb(0x0A, 0x14, 0x0F) : Color.FromRgb(0xFA, 0xFB, 0xFA));
        SetBrush(res, "BackgroundBrush", bg);

        if (light)
        {
            SetBrush(res, "TextBrush", Color.FromRgb(0x1A, 0x1B, 0x20));
            SetBrush(res, "MutedBrush", Color.FromRgb(0x5E, 0x61, 0x6D));
            SetBrush(res, "DangerBrush", Color.FromRgb(0xC9, 0x2A, 0x2A));
            SetBrush(res, "WarnBrush", Color.FromRgb(0x9A, 0x5B, 0x00));
            SetBrush(res, "PanelBrush", Color.FromArgb(0x0E, 0, 0, 0));
            SetBrush(res, "PanelStrongBrush", Color.FromArgb(0x1A, 0, 0, 0));
            SetBrush(res, "PanelBorderBrush", Color.FromArgb(0x24, 0, 0, 0));
            SetBrush(res, "DialogBrush", WithAlpha(Mix(bg, Colors.White, 0.6), 0xF2));
            SetBrush(res, "ScrimBrush", Color.FromArgb(0x66, 0, 0, 0));
            SetBrush(res, "HoverOverlayBrush", Color.FromArgb(0x16, 0, 0, 0));
            SetBrush(res, "TrackBrush", Color.FromArgb(0x1C, 0, 0, 0));
            SetBrush(res, "ToggleTrackBrush", Color.FromArgb(0x38, 0, 0, 0));
            SetBrush(res, "ScrollThumbBrush", Color.FromArgb(0x40, 0, 0, 0));
            SetBrush(res, "ScrollThumbHoverBrush", Color.FromArgb(0x70, 0, 0, 0));
            Set(res, "WordmarkDimColor", Color.FromRgb(0x77, 0x79, 0x86));
            Set(res, "WordmarkBrightColor", Color.FromRgb(0x14, 0x15, 0x1A));
        }
        else
        {
            SetBrush(res, "TextBrush", Color.FromRgb(0xF2, 0xF2, 0xF4));
            SetBrush(res, "MutedBrush", Color.FromRgb(0x9B, 0x9B, 0xA5));
            SetBrush(res, "DangerBrush", Color.FromRgb(0xFF, 0x80, 0x80));
            SetBrush(res, "WarnBrush", Color.FromRgb(0xFF, 0xD0, 0x7A));
            SetBrush(res, "PanelBrush", Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelStrongBrush", Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelBorderBrush", Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
            SetBrush(res, "DialogBrush", WithAlpha(Mix(bg, Colors.White, 0.05), 0xF0));
            SetBrush(res, "ScrimBrush", Color.FromArgb(0x99, 0, 0, 0));
            SetBrush(res, "HoverOverlayBrush", Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            SetBrush(res, "TrackBrush", Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            SetBrush(res, "ToggleTrackBrush", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            SetBrush(res, "ScrollThumbBrush", Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
            SetBrush(res, "ScrollThumbHoverBrush", Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF));
            Set(res, "WordmarkDimColor", Color.FromRgb(0x9C, 0x9C, 0xA6));
            Set(res, "WordmarkBrightColor", Colors.White);
        }

        if (palette.Liquid) ApplyLiquidGlass(res, light, bg);

        Applied?.Invoke(palette);
    }

    private static bool _applied;

    /// <summary>
    /// «Жидкое стекло»: панели получают диагональный блик (ярче в левом верхнем углу), а кромка — переливающуюся светлую
    /// линию. Это обычные градиентные кисти: размытия и эффектов нет, поэтому лишних ресурсов не требуется.
    /// </summary>
    private static void ApplyLiquidGlass(ResourceDictionary res, bool light, Color bg)
    {
        if (light)
        {
            SetGradient(res, "PanelBrush", Color.FromArgb(0xC8, 255, 255, 255), Color.FromArgb(0x30, 255, 255, 255), Color.FromArgb(0x78, 255, 255, 255));
            SetGradient(res, "PanelStrongBrush", Color.FromArgb(0xE0, 255, 255, 255), Color.FromArgb(0x50, 255, 255, 255), Color.FromArgb(0x90, 255, 255, 255));
            SetGradient(res, "PanelBorderBrush", Color.FromArgb(0xFF, 255, 255, 255), Color.FromArgb(0x18, 0, 0, 0), Color.FromArgb(0x55, 0, 0, 0));
            SetGradient(res, "DialogBrush", WithAlpha(Mix(bg, Colors.White, 0.85), 0xF4), WithAlpha(Mix(bg, Colors.White, 0.55), 0xEC), WithAlpha(Mix(bg, Colors.White, 0.75), 0xF2));
        }
        else
        {
            SetGradient(res, "PanelBrush", Color.FromArgb(0x38, 255, 255, 255), Color.FromArgb(0x08, 255, 255, 255), Color.FromArgb(0x1C, 255, 255, 255));
            SetGradient(res, "PanelStrongBrush", Color.FromArgb(0x4A, 255, 255, 255), Color.FromArgb(0x10, 255, 255, 255), Color.FromArgb(0x2C, 255, 255, 255));
            SetGradient(res, "PanelBorderBrush", Color.FromArgb(0x90, 255, 255, 255), Color.FromArgb(0x14, 255, 255, 255), Color.FromArgb(0x48, 255, 255, 255));
            SetGradient(res, "DialogBrush", WithAlpha(Mix(bg, Colors.White, 0.12), 0xEE), WithAlpha(Mix(bg, Colors.White, 0.03), 0xEA), WithAlpha(Mix(bg, Colors.White, 0.08), 0xEE));
        }
    }

    /// <summary>Диагональный градиент из трёх цветов: левый верхний угол → середина → правый нижний угол.</summary>
    private static void SetGradient(ResourceDictionary res, string key, Color start, Color middle, Color end)
    {
        var brush = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(start, 0),
            new GradientStop(middle, 0.55),
            new GradientStop(end, 1)
        }, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        res[key] = brush;
    }

    // ----- цвета -----

    /// <summary>Разбирает #RRGGBB (допускается без решётки); null, если запись некорректна.</summary>
    public static Color? TryParse(string? text)
    {
        var normalized = ThemeColors.TryNormalizeHex(text);
        if (normalized is null) return null;
        return Color.FromRgb(
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseOr(string? text, Color fallback) => TryParse(text) ?? fallback;

    /// <summary>Воспринимаемая яркость 0…1.</summary>
    public static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static void Set(ResourceDictionary res, string key, Color color) => res[key] = color;

    private static void SetBrush(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }
}
