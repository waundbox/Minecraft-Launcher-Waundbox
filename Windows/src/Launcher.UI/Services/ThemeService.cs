using System.Windows;
using System.Windows.Media;
using Launcher.Core.Configuration;

namespace Launcher.UI.Services;

/// <summary>Итоговые цвета оформления: фон, основной цвет и признак светлой темы.</summary>
internal sealed record ThemePalette(bool IsLight, Color Background, Color Accent, bool Liquid = false, Color? Panel = null, bool Image = false, double ImageLuminance = 0.5, Color? Text = null);

/// <summary>
/// Тема интерфейса: светлая/тёмная, основной цвет, цвет фона. Кисти подменяются в ресурсах приложения,
/// а страницы используют их через DynamicResource, поэтому изменения видны сразу, без перезапуска.
/// Цвет текста выбирается по яркости фона, поэтому текст читается при любом фоне.
/// </summary>
internal static class ThemeService
{
    public const string Dark = "dark";
    public const string Light = "light";

    public static readonly Color DarkBackground = Color.FromRgb(0x00, 0x00, 0x00); // как в макете: чёрный фон
    public static readonly Color LightBackground = Color.FromRgb(0xFF, 0xFF, 0xFF); // как в макете: белый фон
    public static readonly Color DarkAccent = Color.FromRgb(0x60, 0x8C, 0xFF); // цвет значка Waundbox
    public static readonly Color LightAccent = Color.FromRgb(0x60, 0x8C, 0xFF); // как в макете: у обычной темы один цвет в обоих вариантах

    public static ThemePalette Current { get; private set; } = new(false, DarkBackground, DarkAccent);

    /// <summary>Тема применена (из любого потока события приходят в потоке интерфейса).</summary>
    public static event Action<ThemePalette>? Applied;

    public static bool IsLightTheme(string? theme) => string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase);

    public static ThemePalette Resolve(LauncherSettings settings)
    {
        var lightTheme = IsLightTheme(settings.Theme);
        var background = ParseOr(settings.BackgroundColor, lightTheme ? LightBackground : DarkBackground);
        var accent = ParseOr(settings.AccentColor, lightTheme ? LightAccent : DarkAccent);
        if (settings.BackgroundImage is not null && settings.BackgroundImageAuto)
        {
            // автоподбор под своё изображение: картинка остаётся как есть, а интерфейс светлеет или темнеет, чтобы текст читался
            var lightImage = settings.BackgroundImageLuminance > 0.5;
            background = lightImage ? LightBackground : DarkBackground;
            accent = ParseOr(settings.AccentColor, lightImage ? LightAccent : DarkAccent);
        }
        // Светлая ли поверхность — решает фактическая яркость фона: так текст читается и при своём цвете фона.
        return new ThemePalette(Luminance(background) > 0.55, background, accent, settings.LiquidGlass, TryParse(settings.PanelColor), settings.BackgroundImage is not null, settings.BackgroundImageLuminance, TryParse(settings.TextColor));
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
        // название на главной: готовая картинка под светлый или тёмный вариант, от фото не зависит
        res["WordmarkImage"] = WordmarkFor(palette.Image ? !(palette.ImageLuminance <= 0.55) : light); // на фото надпись зависит от яркости фото: светлое фото — тёмная, тёмное — белая
        var accent = palette.Accent;
        var bg = palette.Background;

        Set(res, "AccentColor", accent);
        SetBrush(res, "AccentBrush", accent);
        SetBrush(res, "AccentSoftBrush", Color.FromArgb(light ? (byte)0x30 : (byte)0x26, accent.R, accent.G, accent.B));
        SetBrush(res, "AccentOnBrush", Luminance(accent) > 0.6 ? Color.FromRgb(0x0A, 0x14, 0x0F) : Color.FromRgb(0xFA, 0xFB, 0xFA));
        SetBrush(res, "BackgroundBrush", bg);

        if (light)
        {
            SetBrush(res, "TextBrush", Color.FromRgb(0x0F, 0x11, 0x17));
            SetBrush(res, "MutedBrush", Color.FromRgb(0x2B, 0x31, 0x40)); // тёмный, не серый: на светлых панелях текст должен читаться
            SetBrush(res, "DangerBrush", Color.FromRgb(0xC9, 0x2A, 0x2A));
            SetBrush(res, "WarnBrush", Color.FromRgb(0x9A, 0x5B, 0x00));
            // обычная светлая тема по макету: белый фон, белые панели с тонкой серо-синей рамкой
            SetBrush(res, "PanelBrush", Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelStrongBrush", Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
            SetBrush(res, "PanelBorderBrush", Color.FromArgb(0x54, 0x2E, 0x3A, 0x4D));
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
            // обычная тёмная тема по макету: чёрный фон, панели #090D12, рамка #2E3A4D 33%
            SetBrush(res, "PanelBrush", Color.FromArgb(0x78, 0x0D, 0x12, 0x19));
            SetBrush(res, "PanelStrongBrush", Color.FromArgb(0xB0, 0x0D, 0x12, 0x19));
            SetBrush(res, "PanelBorderBrush", Color.FromArgb(0x54, 0x2E, 0x3A, 0x4D));
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

        if (palette.Image)
        {
            // на фото плотность панелей зависит от яркости картинки (замерено по контрасту текста): чем светлее фото,
            // тем плотнее тёмная панель, иначе серый текст сливается с ярким фоном
            var lum = Math.Clamp(palette.ImageLuminance, 0, 1);
            // цвета взяты из Figma-макета тем: тёмная панель #0D1219, светлая — белая 72%, рамка #2E3A4D 33%
            if (light)
            {
                var a = 0.48 + 0.10 * (1 - lum);
                SetBrush(res, "PanelBrush", Color.FromArgb(Alpha(a), 0xFF, 0xFF, 0xFF));
                SetBrush(res, "PanelStrongBrush", Color.FromArgb(Alpha(a + 0.1), 0xFF, 0xFF, 0xFF));
                SetBrush(res, "PanelBorderBrush", Color.FromArgb(0x54, 0x2E, 0x3A, 0x4D));
                SetBrush(res, "MutedBrush", Color.FromRgb(0x26, 0x2F, 0x3E));
            }
            else
            {
                var a = Math.Min(0.68, 0.27 + 0.34 * lum);
                SetBrush(res, "PanelBrush", Color.FromArgb(Alpha(a), 0x0D, 0x12, 0x19));
                SetBrush(res, "PanelStrongBrush", Color.FromArgb(Alpha(a + 0.1), 0x0D, 0x12, 0x19));
                SetBrush(res, "PanelBorderBrush", Color.FromArgb(0x70, 0x2E, 0x3A, 0x4D));
                SetBrush(res, "MutedBrush", Color.FromRgb(0xD2, 0xDD, 0xEF));
            }
        }
        // верхняя панель с вкладками и текст: у каждого варианта (светлый/тёмный) свои, на фото плотнее
        // активная вкладка — заливка основного цвета (как в макете), заголовок страницы — по яркости картинки, потому что лежит прямо на фоне
        SetBrush(res, "NavActiveBrush", WithAlpha(accent, 0xB8));
        // темы на светлых фото (Небо, Шёлк, Сакура): «Импорт сборки» с тёмной надписью на светлом стекле, ползунок прокрутки — полупрозрачный белый,
        // подпись «Лаунчер Minecraft» не зависит от картинки; на остальных темах всё как у панелей
        var brightPhoto = palette.Image && palette.ImageLuminance > 0.55;
        if (brightPhoto)
        {
            SetBrush(res, "ImportButtonBgBrush", Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
            SetBrush(res, "ImportButtonFgBrush", Color.FromRgb(0x2F, 0x2D, 0x2D));
            SetBrush(res, "ScrollThumbBrush", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            SetBrush(res, "ScrollThumbHoverBrush", Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            SetBrush(res, "ImportButtonBgBrush", res["PanelBrush"] is SolidColorBrush pbb ? pbb.Color : Colors.Transparent);
            SetBrush(res, "ImportButtonFgBrush", ((SolidColorBrush)res["TextBrush"]).Color);
        }
        SetBrush(res, "TaglineBrush", (palette.Image ? brightPhoto : light) ? Color.FromArgb(0xCC, 0x2F, 0x2D, 0x2D) : Color.FromArgb(0xCC, 0xE5, 0xE5, 0xE5));
        SetBrush(res, "SelectedFillBrush", WithAlpha(accent, 0xE6)); // выбранная кнопка (вкладки, «Тёмная/Светлая») — сплошная заливка основным цветом
        if (palette.Image)
        {
            var imgLum = Math.Clamp(palette.ImageLuminance, 0, 1);
            var brightImage = imgLum > 0.55;
            // заголовок страницы: на светлом фото #2F2D2D (сплошной, по просьбе пользователя), на тёмном фото — #E5E5E5
            var title = brightImage ? Color.FromRgb(0x2F, 0x2D, 0x2D) : Color.FromRgb(0xE5, 0xE5, 0xE5);
            SetBrush(res, "PageTitleBrush", title);
            SetBrush(res, "PageSubtitleBrush", title);
            SetBrush(res, "TopBarBorderBrush", light ? Color.FromRgb(0x41, 0x49, 0x55) : Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)); // в тёмном режиме — светлая тонкая линия
            SetBrush(res, "TopBarBrush", light ? Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(Alpha(Math.Min(0.82, 0.42 + 0.34 * imgLum)), 0x0D, 0x12, 0x19));
            SetBrush(res, "TextBrush", light ? Color.FromRgb(0x0B, 0x10, 0x18) : Color.FromRgb(0xED, 0xF3, 0xFF));
        }
        else
        {
            SetBrush(res, "TopBarBrush", light ? Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x80, 0x0D, 0x12, 0x19));
            SetBrush(res, "TopBarBorderBrush", light ? Color.FromRgb(0x41, 0x49, 0x55) : Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
            var plainTitle = light ? Color.FromRgb(0x2F, 0x2D, 0x2D) : ((SolidColorBrush)res["TextBrush"]).Color;
            SetBrush(res, "PageTitleBrush", plainTitle);
            SetBrush(res, "PageSubtitleBrush", light ? plainTitle : ((SolidColorBrush)res["MutedBrush"]).Color);
        }
        if (palette.Text is { } own)
        {
            // свой цвет текста: если задан, заменяет автоматический (заголовки, подписи, кнопка импорта)
            SetBrush(res, "TextBrush", own);
            SetBrush(res, "MutedBrush", WithAlpha(own, 0xD0));
            SetBrush(res, "PageTitleBrush", own);
            SetBrush(res, "PageSubtitleBrush", WithAlpha(own, 0xE0));
            SetBrush(res, "TaglineBrush", WithAlpha(own, 0xCC));
            SetBrush(res, "ImportButtonFgBrush", own);
        }
        if (palette.Panel is { } tint) ApplyPanelColor(res, light, bg, tint);
        if (palette.Liquid) ApplyLiquidGlass(res, light, bg, palette.Panel);

        Applied?.Invoke(palette);
    }

    private static bool _applied;

    /// <summary>
    /// «Жидкое стекло»: панели получают диагональный блик (ярче в левом верхнем углу), а кромка — переливающуюся светлую
    /// линию. Это обычные градиентные кисти: размытия и эффектов нет, поэтому лишних ресурсов не требуется.
    /// </summary>
    private static void ApplyLiquidGlass(ResourceDictionary res, bool light, Color bg, Color? tint)
    {
        // как у стекла Apple: молочно-светлая прозрачная заливка (не тёмная), блик в левом верхнем углу,
        // отсвет у правого нижнего и тонкая яркая кромка
        var body = tint is { } t
            ? WithAlpha(t, 0x40)
            : res["PanelBrush"] is SolidColorBrush pb ? pb.Color : Color.FromArgb(0x14, 255, 255, 255);
        var strong = tint is { } t2
            ? WithAlpha(t2, 0x60)
            : res["PanelStrongBrush"] is SolidColorBrush ps ? ps.Color : body;
        var milk = light ? 0.55 : 0.22; // насколько цвет панели разбавляется белым
        void Milky(string key, Color c)
        {
            var rgb = Mix(Color.FromRgb(c.R, c.G, c.B), Colors.White, milk);
            var a0 = c.A;
            SetStops(res, key,
                (WithAlpha(rgb, (byte)Math.Min(255, a0 + 0x34)), 0),
                (WithAlpha(rgb, (byte)(a0 * 0.78)), 0.22),
                (WithAlpha(rgb, (byte)(a0 * 0.70)), 0.62),
                (WithAlpha(rgb, (byte)Math.Min(255, a0 + 0x1C)), 1));
        }
        Milky("PanelBrush", body);
        Milky("PanelStrongBrush", strong);

        // кромка: тонкая яркая линия, сильнее в двух противоположных углах, слабая по бокам
        var shade = light ? Color.FromRgb(0x2E, 0x3A, 0x4D) : Color.FromRgb(255, 255, 255);
        SetStops(res, "PanelBorderBrush",
            (Color.FromArgb(light ? (byte)0xFF : (byte)0xE6, 255, 255, 255), 0),
            (Color.FromArgb(0x66, 255, 255, 255), 0.18),
            (WithAlpha(shade, light ? (byte)0x22 : (byte)0x16), 0.5),
            (Color.FromArgb(0x62, 255, 255, 255), 0.82),
            (Color.FromArgb(light ? (byte)0xE0 : (byte)0xC8, 255, 255, 255), 1));

        var dialogBase = tint is { } t3 ? Mix(bg, t3, 0.2) : Mix(bg, Colors.White, light ? 0.7 : 0.08);
        SetStops(res, "DialogBrush",
            (WithAlpha(Mix(dialogBase, Colors.White, light ? 0.3 : 0.1), 0xF0), 0),
            (WithAlpha(dialogBase, 0xE6), 0.5),
            (WithAlpha(Mix(dialogBase, Colors.White, light ? 0.2 : 0.06), 0xEE), 1));
    }

    /// <summary>Диагональный градиент из любого числа остановок (смещение 0…1, от левого верхнего угла к правому нижнему).</summary>
    private static void SetStops(ResourceDictionary res, string key, params (Color Color, double Offset)[] stops)
    {
        var collection = new GradientStopCollection();
        foreach (var (color, offset) in stops) collection.Add(new GradientStop(color, offset));
        var brush = new LinearGradientBrush(collection, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        res[key] = brush;
    }

    /// <summary>Свой цвет фона виджетов: панели и окна окрашиваются в него (с прозрачностью, чтобы фон просвечивал).</summary>
    private static void ApplyPanelColor(ResourceDictionary res, bool light, Color bg, Color tint)
    {
        SetBrush(res, "PanelBrush", WithAlpha(tint, light ? (byte)0x34 : (byte)0x3C));
        SetBrush(res, "PanelStrongBrush", WithAlpha(tint, light ? (byte)0x50 : (byte)0x58));
        SetBrush(res, "DialogBrush", WithAlpha(Mix(bg, tint, light ? 0.14 : 0.18), light ? (byte)0xF2 : (byte)0xF0));
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

    private static byte Alpha(double a) => (byte)Math.Round(Math.Clamp(a, 0, 1) * 255);

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static readonly Dictionary<bool, ImageSource> WordmarkCache = new();

    private static ImageSource WordmarkFor(bool light)
    {
        if (WordmarkCache.TryGetValue(light, out var cached)) return cached;
        var file = light ? "waundbox-light.png" : "waundbox-dark.png";
        var image = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Resources/Wordmark/{file}", UriKind.Absolute));
        image.Freeze();
        return WordmarkCache[light] = image;
    }

    private static void Set(ResourceDictionary res, string key, Color color) => res[key] = color;

    private static void SetBrush(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }
}
