using System.Collections.ObjectModel;
using Launcher.Core.Configuration;
using Launcher.UI.Controls;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

public sealed record JavaChoice(string Title, string? Path);

public sealed record ThemeChoice(string Id, string Title);

/// <summary>Всё, что сохраняет кнопка «Сохранить тему» кроме названия, варианта, основного цвета и картинки. Пусто — значение не менять.</summary>
public sealed record ThemeExtras(string? Background, string? Panel, string? Text, double? Dim, bool? Auto, bool? Glow, bool? Liquid, string? Layout, string? LayoutNoModel);

/// <summary>Готовая тема: фото, светлая или тёмная основа и основной цвет. <see cref="IsPending"/> — тема применена на пробу и ждёт «Принять» или «Отмена».</summary>
public sealed class ThemePreset : ObservableObject
{
    private bool _isPending;

    public ThemePreset(string id, string name, string description, string imageFile, string theme, string? accent, string? filePath = null, ThemeExtras? extras = null)
    {
        Extras = extras;
        FilePath = filePath;
        Id = id;
        Name = name;
        Description = description;
        ImageFile = imageFile;
        Theme = theme;
        Accent = accent;
    }

    /// <summary>Остальные настройки из сохранённой темы (цвета, затемнение, раскладка); у встроенных тем нет.</summary>
    public ThemeExtras? Extras { get; }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string ImageFile { get; }
    public string Theme { get; }
    public string? Accent { get; }

    /// <summary>Полный путь к картинке, если тема лежит в папке themes; у встроенных тем пусто.</summary>
    public string? FilePath { get; }

    /// <summary>У темы нет картинки: остаётся обычный фон лаунчера.</summary>
    public bool IsPlain => string.IsNullOrEmpty(ImageFile) && FilePath is null;

    /// <summary>Фон карточки: настоящая картинка темы (без интерфейса) или сплошной цвет для обычной темы; зависит от режима.</summary>
    public System.Windows.Media.Brush? Backdrop { get { EnsureLoaded(); return _backdrop; } }

    /// <summary>Интерфейс лаунчера поверх фона (прозрачный PNG из папки preview): под режим и расположение меню.</summary>
    public System.Windows.Media.ImageSource? Overlay { get { EnsureLoaded(); return _overlay; } }

    private System.Windows.Media.Brush? _backdrop;
    private System.Windows.Media.ImageSource? _overlay;

    private void EnsureLoaded()
    {
        var light = ThemeService.Current.IsLight;
        if (_thumbLight == light && _thumbLayout == ThemePreviews.Layout) return;
        _thumbLight = light;
        _thumbLayout = ThemePreviews.Layout;
        // готовая картинка предпросмотра темы (preview/<режим>/<имя>.png) целиком заменяет фон и интерфейс
        if (ThemePreviews.Find(PreviewKey, light) is { } full)
        {
            _backdrop = BrushOf(LoadImage(new Uri(full, UriKind.Absolute), 1000));
            _overlay = null;
            return;
        }
        if (IsPlain)
        {
            var solid = new System.Windows.Media.SolidColorBrush(light ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black);
            solid.Freeze();
            _backdrop = solid;
        }
        else
        {
            var uri = FilePath is not null
                ? new Uri(FilePath, UriKind.Absolute)
                : new Uri($"pack://application:,,,/Resources/Themes/{ImageFile}", UriKind.Absolute);
            _backdrop = BrushOf(LoadImage(uri, 800));
        }
        _overlay = ThemePreviews.FindOverlay(light) is { } overlay ? LoadImage(new Uri(overlay, UriKind.Absolute), 1000) : null;
    }

    private static System.Windows.Media.Brush? BrushOf(System.Windows.Media.ImageSource? image)
    {
        if (image is null) return null;
        var brush = new System.Windows.Media.ImageBrush(image) { Stretch = System.Windows.Media.Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.ImageSource? LoadImage(Uri uri, int decodeWidth)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = decodeWidth;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.IO.FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool? _thumbLight;
    private string? _thumbLayout;

    /// <summary>Имя файла предпросмотра: у встроенной темы её id, у темы из папки — имя папки.</summary>
    public string PreviewKey => Id.StartsWith("folder:", StringComparison.Ordinal) ? Id["folder:".Length..] : Id;

    /// <summary>Режим лаунчера сменился: карточка берёт картинку из другой папки предпросмотра.</summary>
    public void RefreshThumbnail()
    {
        OnPropertyChanged(nameof(Backdrop));
        OnPropertyChanged(nameof(Overlay));
    }

    public bool IsPending { get => _isPending; set => Set(ref _isPending, value); }
}

/// <summary>Папка предпросмотра <c>preview/dark</c> и <c>preview/light</c>: картинки карточек готовых тем (показ, как будет выглядеть лаунчер).</summary>
internal static class ThemePreviews
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    /// <summary>Папка данных лаунчера: сначала ищем в её <c>preview</c>, потом рядом с лаунчером.</summary>
    public static string? DataRoot { get; set; }

    /// <summary>Расположение меню сейчас: от него зависит папка предпросмотра (сверху, слева значками, слева с названиями).</summary>
    public static string Layout { get; set; } = NavLayouts.Top;

    private static string? LayoutFolder(string layout) => layout switch
    {
        NavLayouts.Side => "side",
        _ => null
    };

    /// <summary>Прозрачный PNG с интерфейсом (gui.png) для режима и расположения меню; нет — берётся вариант «сверху».</summary>
    public static string? FindOverlay(bool light)
    {
        var variant = light ? "light" : "dark";
        var baseDirs = new List<string>();
        if (DataRoot is not null) baseDirs.Add(Path.Combine(DataRoot, "preview"));
        baseDirs.Add(Path.Combine(AppContext.BaseDirectory, "preview"));
        var folders = LayoutFolder(Layout) is { } layoutFolder ? new[] { layoutFolder, null } : new string?[] { null };
        foreach (var folder in folders)
            foreach (var baseDir in baseDirs)
            {
                var path = folder is null ? Path.Combine(baseDir, variant, "gui.png") : Path.Combine(baseDir, folder, variant, "gui.png");
                if (File.Exists(path)) return path;
            }
        return null;
    }

    public static string? Find(string key, bool light)
    {
        var variant = light ? "light" : "dark";
        var baseDirs = new List<string>();
        if (DataRoot is not null) baseDirs.Add(Path.Combine(DataRoot, "preview"));
        baseDirs.Add(Path.Combine(AppContext.BaseDirectory, "preview"));
        // сначала папка выбранного расположения меню, потом общая («сверху»)
        var folders = LayoutFolder(Layout) is { } layoutFolder ? new[] { layoutFolder, null } : new string?[] { null };
        foreach (var folder in folders)
        {
            foreach (var baseDir in baseDirs)
            {
                var dir = folder is null ? Path.Combine(baseDir, variant) : Path.Combine(baseDir, folder, variant);
                foreach (var ext in Extensions)
                {
                    var path = Path.Combine(dir, key + ext);
                    if (File.Exists(path)) return path;
                }
            }
        }
        return null;
    }
}

/// <summary>Образец цвета: запись #RRGGBB и кисть для показа.</summary>
public sealed record ColorChoice(string Hex, System.Windows.Media.Brush Brush)
{
    public static ColorChoice Of(string hex)
    {
        var color = ThemeService.TryParse(hex) ?? System.Windows.Media.Colors.Gray;
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return new ColorChoice(hex, brush);
    }
}

/// <summary>Страница «Настройки»: Java, экономный режим, папки.</summary>
internal sealed class SettingsViewModel : PageViewModel
{
    private readonly UiContext _ctx;
    private bool _eco;
    private bool _applying;
    private bool _scanned;
    private bool _isScanning;
    private JavaChoice? _selectedJava;

    /// <summary>Нужен странице настроек для блока «Модель персонажа» (выбор модели Blockbench).</summary>
    public AccountViewModel Account => _ctx.Account;

    private int _tab; // 0 — «Общее», 1 — «Оформление», 2 — «Готовые темы»

    /// <summary>Вкладки страницы: «Общее», «Оформление» (свечение, тема и цвета, фон виджетов) и «Готовые темы».</summary>
    private int Tab
    {
        get => _tab;
        set
        {
            var previous = _tab;
            if (!Set(ref _tab, value)) return;
            if (previous == 2) _ = CancelPendingPresetAsync(); // ушли со страницы тем без «Принять» — проба отменяется
            OnPropertyChanged(nameof(ShowGeneralTab));
            OnPropertyChanged(nameof(ShowCustomTab));
            OnPropertyChanged(nameof(ShowPresetsTab));
            OnPropertyChanged(nameof(TabIndex));
        }
    }

    /// <summary>Номер открытой вкладки (для кнопок в редакторе интерфейса).</summary>
    public int TabIndex
    {
        get => _tab;
        set
        {
            if (value == 2) LoadFolderPresets();
            Tab = value;
        }
    }

    public bool ShowGeneralTab => _tab == 0;
    public bool ShowCustomTab => _tab == 1;
    public bool ShowPresetsTab => _tab == 2;
    public RelayCommand ShowGeneralTabCommand { get; }
    public RelayCommand ShowCustomTabCommand { get; }

    /// <summary>Фон виджетов настраивается только на обычной теме; на готовых темах с картинкой он не нужен.</summary>
    public bool HasNoBackgroundImage => _ctx.Settings.Current.BackgroundImage is null;

    public bool DarkChipActive => !IsLightTheme;
    public bool LightChipActive => IsLightTheme;

    private void RaiseThemeChips()
    {
        OnPropertyChanged(nameof(DarkChipActive));
        OnPropertyChanged(nameof(LightChipActive));
    }

    public RelayCommand SelectThemeCommand { get; }
    public AsyncCommand PreviewPresetCommand { get; }
    public AsyncCommand AcceptPresetCommand { get; }
    public AsyncCommand CancelPresetCommand { get; }
    public RelayCommand OpenPresetsCommand { get; }
    public RelayCommand OpenThemesFolderCommand { get; }
    public RelayCommand BackFromPresetsCommand { get; }

    /// <summary>Готовые темы: фото, основа (светлая или тёмная) и основной цвет. Нажатие применяет тему на пробу, дальше «Принять» или «Отмена».</summary>
    public IReadOnlyList<ThemePreset> Presets { get; } = new[]
    {
        new ThemePreset("sky", "Небо", "Светлое небо и облака", "sky.jpg", ThemeService.Light, "#1E88E5"),
        new ThemePreset("cozy", "Уютный дом", "Тёплая комната с видом на лес", "cozy.jpg", ThemeService.Dark, "#FFB86B"),
        new ThemePreset("forest", "Вечерний лес", "Сумрак, цветы и тёплые лучи", "forest.jpg", ThemeService.Dark, "#B07CF0"),
        new ThemePreset("sakura", "Сакура", "Розовые лепестки на рассвете", "sakura.jpg", ThemeService.Light, "#E0457B"),
        new ThemePreset("silk", "Шёлк", "Светлые складки ткани", "silk.jpg", ThemeService.Light, "#5B6BDF"),
        new ThemePreset("plain", "Обычный фон", "Тёмный фон лаунчера без картинки", "", ThemeService.Dark, null)
    };

    /// <summary>Темы из папки themes (рядом с лаунчером и в папке данных): у каждой своя папка с theme.json и картинкой.</summary>
    public IReadOnlyList<ThemePreset> FolderPresets { get; private set; } = Array.Empty<ThemePreset>();
    /// <summary>«Сделать свою тему»: открывает редактор интерфейса, где внизу есть «Сохранить тему».</summary>
    public RelayCommand MakeOwnThemeCommand { get; }

    // ----- расположение меню -----

    public string NavLayout => NavLayouts.Normalize(_ctx.Settings.Current.NavLayout);
    public bool NavTopActive => NavLayout == NavLayouts.Top;
    public bool NavSideActive => NavLayout == NavLayouts.Side;

    public RelayCommand SetNavLayoutCommand { get; }

    private void RaiseNavLayout()
    {
        if (ThemePreviews.Layout != NavLayout)
        {
            ThemePreviews.Layout = NavLayout;
            foreach (var p in Presets.Concat(FolderPresets)) p.RefreshThumbnail(); // картинки предпросмотра подстраиваются под расположение меню
        }
        OnPropertyChanged(nameof(NavTopActive));
        OnPropertyChanged(nameof(NavSideActive));
    }

    public bool HasFolderPresets => FolderPresets.Count > 0;
    public bool HasNoFolderPresets => FolderPresets.Count == 0;

    private void LoadFolderPresets()
    {
        var found = new List<ThemePreset>();
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "themes"), Path.Combine(_ctx.Services.Paths.Root, "themes") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var preset = ThemeFolder.Read(sub);
                if (preset is not null && found.All(p => p.Id != preset.Id)) found.Add(preset);
            }
        }
        FolderPresets = found;
        OnPropertyChanged(nameof(FolderPresets));
        OnPropertyChanged(nameof(HasFolderPresets));
        OnPropertyChanged(nameof(HasNoFolderPresets));
    }

    private LauncherSettings? _presetSnapshot; // настройки до пробы темы: к ним возвращает «Отмена»
    private string? _presetImage;              // файл картинки, поставленный пробой

    private async Task PreviewPresetAsync(ThemePreset preset)
    {
        try
        {
            var root = _ctx.Services.Paths.Root;
            _presetSnapshot ??= _ctx.Settings.Current;
            string? image = null;
            var luminance = 0.3;
            if (!preset.IsPlain)
            {
                image = preset.FilePath is not null
                    ? await BackgroundImages.ImportAsync(root, preset.FilePath)
                    : await BackgroundImages.ImportResourceAsync(root, preset.ImageFile);
                luminance = await Task.Run(() => BackgroundImages.Luminance(BackgroundImages.PathFor(root, image)!));
            }
            var x = preset.Extras;
            var previous = _presetImage;
            _presetImage = image;
            foreach (var p in Presets.Concat(FolderPresets)) p.IsPending = ReferenceEquals(p, preset);
            await _ctx.Settings.UpdateAsync(s =>
            {
                var own = ThemeLayoutStore.Find(s, preset.Id); // что человек уже правил в этой теме, возвращается
                return s with
            {
                ThemeKey = preset.Id,
                NavLayout = own?.Nav is { } nav ? NavLayouts.ToSetting(nav) : s.NavLayout,
                Theme = x is null ? s.Theme : preset.Theme, AccentColor = preset.Accent, BackgroundColor = x?.Background, PanelColor = x?.Panel, TextColor = x?.Text,
                BackgroundImage = image, BackgroundImageAuto = x?.Auto ?? preset.IsPlain, BackgroundImageLuminance = luminance,
                BackgroundImageDim = x?.Dim ?? 0,
                BackgroundGlow = x?.Glow ?? (preset.IsPlain ? true : s.BackgroundGlow),
                LiquidGlass = x?.Liquid ?? s.LiquidGlass,
                HomeLayout = own?.Layout ?? x?.Layout, HomeLayoutNoModel = own?.NoModel ?? x?.LayoutNoModel // у темы своя раскладка; у темы без неё — стандартная
                };
            });
            BackgroundImages.Delete(root, previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or System.IO.FileFormatException or InvalidOperationException)
        {
            _ctx.Toasts.Show("Не удалось применить тему: " + ex.Message, ToastKind.Error);
        }
    }

    /// <summary>Проба темы живёт, пока человек на странице тем: любой уход (другая вкладка или страница) отменяет её, если не нажато «Принять».</summary>
    public Task CancelPendingPresetAsync() => _presetSnapshot is null ? Task.CompletedTask : CancelPresetAsync();

    private Task AcceptPresetAsync()
    {
        var root = _ctx.Services.Paths.Root;
        // прежняя картинка больше не нужна, если она не совпадает с новой
        if (_presetSnapshot?.BackgroundImage is { } old && old != _presetImage) BackgroundImages.Delete(root, old);
        FinishPreset();
        return Task.CompletedTask;
    }

    private async Task CancelPresetAsync()
    {
        var snap = _presetSnapshot;
        var preview = _presetImage;
        FinishPreset();
        if (snap is null) return;
        await _ctx.Settings.UpdateAsync(s => s with
        {
            Theme = snap.Theme, AccentColor = snap.AccentColor, BackgroundColor = snap.BackgroundColor, PanelColor = snap.PanelColor,
            BackgroundImage = snap.BackgroundImage, BackgroundImageAuto = snap.BackgroundImageAuto,
            BackgroundImageLuminance = snap.BackgroundImageLuminance, BackgroundImageDim = snap.BackgroundImageDim,
            BackgroundGlow = snap.BackgroundGlow, TextColor = snap.TextColor, LiquidGlass = snap.LiquidGlass,
            HomeLayout = snap.HomeLayout, HomeLayoutNoModel = snap.HomeLayoutNoModel,
            ThemeKey = snap.ThemeKey, NavLayout = snap.NavLayout, ThemeLayouts = snap.ThemeLayouts
        });
        BackgroundImages.Delete(_ctx.Services.Paths.Root, preview);
    }

    /// <summary>Возвращает обычную тему лаунчера: тёмная, цвет со значка, без картинки на фоне, со свечением фона.</summary>
    private async Task RestoreDefaultThemeAsync()
    {
        var root = _ctx.Services.Paths.Root;
        var used = new[] { _ctx.Settings.Current.BackgroundImage, _presetImage, _presetSnapshot?.BackgroundImage };
        FinishPreset();
        await _ctx.Settings.UpdateAsync(s => s with
        {
            Theme = ThemeService.Dark, AccentColor = null, BackgroundColor = null, PanelColor = null,
            BackgroundImage = null, BackgroundImageAuto = true, BackgroundImageLuminance = 0.3,
            BackgroundGlow = true, LiquidGlass = false, TextColor = null, ThemeKey = null
        });
        foreach (var name in used.Where(n => n is not null).Distinct()) BackgroundImages.Delete(root, name);
    }

    private void FinishPreset()
    {
        _presetSnapshot = null;
        _presetImage = null;
        foreach (var p in Presets.Concat(FolderPresets)) p.IsPending = false;
    }

    public SettingsViewModel(UiContext ctx) : base("Настройки", "")
    {
        _ctx = ctx;
        ThemePreviews.DataRoot = ctx.Services.Paths.Root;
        ThemeService.Applied += _ => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var p in Presets.Concat(FolderPresets)) p.RefreshThumbnail();
        });
        SetNavLayoutCommand = new RelayCommand(p => _ = _ctx.Settings.UpdateAsync(s => ThemeLayoutStore.WithNav(s, NavLayouts.ToSetting(p as string))));
        SelectThemeCommand = new RelayCommand(p =>
        {
            if (p is string id) SelectedTheme = ThemeChoices.First(t => t.Id == id);
        });
        PreviewPresetCommand = new AsyncCommand(p => PreviewPresetAsync((ThemePreset)p!));
        AcceptPresetCommand = new AsyncCommand(AcceptPresetAsync);
        CancelPresetCommand = new AsyncCommand(CancelPresetAsync);
        OpenPresetsCommand = new RelayCommand(() => { LoadFolderPresets(); Tab = 2; });
        OpenThemesFolderCommand = new RelayCommand(() =>
        {
            var dir = Path.Combine(_ctx.Services.Paths.Root, "themes");
            Directory.CreateDirectory(dir);
            ShellHelper.OpenFolder(dir);
        });
        MakeOwnThemeCommand = new RelayCommand(() =>
        {
            FinishPreset(); // редактор начинается с темы, которую сейчас смотрели
            _ctx.HomeEditor.StartCommand.Execute(null);
        });
        BackFromPresetsCommand = new RelayCommand(() => Tab = 1);
        ShowGeneralTabCommand = new RelayCommand(() => Tab = 0);
        ShowCustomTabCommand = new RelayCommand(() => Tab = 1);
        ScanCommand = new AsyncCommand(ScanAsync, () => !_isScanning);
        OpenLauncherFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_ctx.Services.Paths.Root));
        OpenLogsFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(Path.Combine(_ctx.Services.Paths.Root, "logs")));
        OpenGameFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_ctx.Services.Paths.GameRoot));
        RestartCommand = new RelayCommand(Restart);
        _selectedTheme = ThemeChoices[0];
        SetAccentCommand = new RelayCommand(p => ApplyColor(accent: p as string));
        SetBackgroundCommand = new RelayCommand(p => ApplyColor(background: p as string));
        ResetColorsCommand = new RelayCommand(() => _ = RestoreDefaultThemeAsync());
        SetPanelCommand = new RelayCommand(p => ApplyPanel(p as string));
        ResetPanelCommand = new RelayCommand(() => _ = _ctx.Settings.UpdateAsync(s => s with { PanelColor = null }));
        _ctx.Settings.Changed += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(SyncFromSettings);
    }

    // ----- тема и цвета -----

    public IReadOnlyList<ThemeChoice> ThemeChoices { get; } =
        new[] { new ThemeChoice(ThemeService.Dark, "Тёмная"), new ThemeChoice(ThemeService.Light, "Светлая") };
    public RelayCommand SetAccentCommand { get; }
    public RelayCommand SetBackgroundCommand { get; }
    public RelayCommand ResetColorsCommand { get; }
    public RelayCommand SetPanelCommand { get; }
    public RelayCommand ResetPanelCommand { get; }

    /// <summary>Образцы цвета фона виджетов (панелей и окон).</summary>
    public IReadOnlyList<ColorChoice> PanelSwatches { get; } = new[]
    {
        "#FFFFFF", "#000000", "#1C1C22", "#3B82F6", "#8B5CF6", "#E5484D", "#1E9E6A", "#F59E0B", "#FF8FB1", "#6EE7E7"
    }.Select(ColorChoice.Of).ToArray();

    private string _panelText = "";

    /// <summary>Цвет фона виджетов в виде #RRGGBB; пусто — стандартный.</summary>
    public string PanelText
    {
        get => _panelText;
        set
        {
            if (!Set(ref _panelText, value ?? "")) return;
            OnPropertyChanged(nameof(PanelTextInvalid));
            if (!_applying && ThemeColors.TryNormalizeHex(value) is { } hex) ApplyPanel(hex);
        }
    }

    public bool PanelTextInvalid => _panelText.Length > 0 && ThemeColors.TryNormalizeHex(_panelText) is null;

    private void ApplyPanel(string? color)
    {
        if (ThemeColors.TryNormalizeHex(color) is not { } hex) return;
        _ = _ctx.Settings.UpdateAsync(s => s with { PanelColor = hex });
    }

    public IReadOnlyList<ColorChoice> AccentSwatches { get; } = new[]
    {
        "#608CFF", "#3A6BE6", "#8CF0B8", "#1E9E6A", "#7AB8FF", "#3B82F6", "#B79CFF", "#8B5CF6", "#FF8FB1", "#E5484D", "#FFB86B", "#F59E0B", "#FFE27A", "#6EE7E7"
    }.Select(ColorChoice.Of).ToArray();

    private static readonly IReadOnlyList<ColorChoice> DarkBackgrounds = new[]
    {
        "#0C0C0F", "#000000", "#0B1220", "#120B1A", "#0B1712", "#1A0F0F", "#1C1C22"
    }.Select(ColorChoice.Of).ToArray();

    private static readonly IReadOnlyList<ColorChoice> LightBackgrounds = new[]
    {
        "#F2F3F6", "#FFFFFF", "#EAF1FB", "#F6EFFA", "#EDF6F0", "#FBF0EC", "#E4E6EB"
    }.Select(ColorChoice.Of).ToArray();

    /// <summary>Образцы фона зависят от выбранной темы.</summary>
    public IReadOnlyList<ColorChoice> BackgroundSwatches => IsLightTheme ? LightBackgrounds : DarkBackgrounds;

    private ThemeChoice _selectedTheme;
    private string _accentText = "";
    private string _backgroundText = "";

    public bool IsLightTheme => ThemeService.IsLightTheme(_selectedTheme.Id);

    /// <summary>При смене темы цвет фона возвращается к стандартному для неё, основной цвет остаётся вашим.</summary>
    public ThemeChoice SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is null || !Set(ref _selectedTheme, value)) return;
            OnPropertyChanged(nameof(IsLightTheme));
            RaiseThemeChips();
            OnPropertyChanged(nameof(BackgroundSwatches));
            if (_applying) return;
            var id = value.Id;
            _ = _ctx.Settings.UpdateAsync(s => s with { Theme = id, BackgroundColor = null });
        }
    }

    public string AccentText
    {
        get => _accentText;
        set
        {
            if (!Set(ref _accentText, value ?? "")) return;
            OnPropertyChanged(nameof(AccentTextInvalid));
            if (!_applying && ThemeColors.TryNormalizeHex(value) is { } hex) ApplyColor(accent: hex);
        }
    }

    public string BackgroundText
    {
        get => _backgroundText;
        set
        {
            if (!Set(ref _backgroundText, value ?? "")) return;
            OnPropertyChanged(nameof(BackgroundTextInvalid));
            if (!_applying && ThemeColors.TryNormalizeHex(value) is { } hex) ApplyColor(background: hex);
        }
    }

    public bool AccentTextInvalid => _accentText.Length > 0 && ThemeColors.TryNormalizeHex(_accentText) is null;
    public bool BackgroundTextInvalid => _backgroundText.Length > 0 && ThemeColors.TryNormalizeHex(_backgroundText) is null;

    private void ApplyColor(string? accent = null, string? background = null)
    {
        var a = ThemeColors.TryNormalizeHex(accent);
        var b = ThemeColors.TryNormalizeHex(background);
        if (a is null && b is null) return;
        _ = _ctx.Settings.UpdateAsync(s => s with { AccentColor = a ?? s.AccentColor, BackgroundColor = b ?? s.BackgroundColor });
    }

    public ObservableCollection<JavaChoice> JavaChoices { get; } = new();

    public RelayCommand RestartCommand { get; }

    private static void Restart()
    {
        var path = Environment.ProcessPath;
        if (path is null) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, "--restart") { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

    public AsyncCommand ScanCommand { get; }
    public RelayCommand OpenLauncherFolderCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand OpenGameFolderCommand { get; }

    private int _versionClicks;

    /// <summary>Версия лаунчера для плашки внизу настроек.</summary>
    public string VersionText { get; } = "Waundbox " + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0");

    /// <summary>Папка со значками профилей (для подсказки в настройках).</summary>
    public RelayCommand OpenIconsFolderCommand => new(() => ShellHelper.OpenFolder(ProfileIcons.Directory));

    /// <summary>Десятое нажатие на плашку версии открывает вкладку разработчика (без подсказок и анимаций).</summary>
    public void RegisterVersionClick()
    {
        if (_ctx.Settings.Current.DeveloperMode) return;
        if (++_versionClicks < 10) return;
        _versionClicks = 0;
        _ = _ctx.Settings.UpdateAsync(s => s with { DeveloperMode = true });
    }

    public string RootPath => _ctx.Services.Paths.Root;

    public bool EcoMode
    {
        get => _eco;
        set
        {
            if (!Set(ref _eco, value) || _applying) return;
            Motion.SetEco(value);
            _ = _ctx.Settings.UpdateAsync(s => s with { EcoMode = value });
        }
    }

    /// <summary>Фоновое свечение («дым»): можно отключить совсем.</summary>
    public bool BackgroundGlow
    {
        get => _ctx.Settings.Current.BackgroundGlow;
        set
        {
            if (_ctx.Settings.Current.BackgroundGlow == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { BackgroundGlow = value });
        }
    }

    /// <summary>Через 10 секунд без действий лаунчер переходит в экономный режим (анимации и вращение модели останавливаются).</summary>
    /// <summary>Обновление лаунчера: кнопка «Проверить обновления» и сведения о последней проверке.</summary>
    public UpdateController Updates => _ctx.Updates;

    /// <summary>«Жидкое стекло» (раздел «Тема и цвета»): включается и выключается сразу.</summary>
    public bool LiquidGlass
    {
        get => _ctx.Settings.Current.LiquidGlass;
        set
        {
            if (_ctx.Settings.Current.LiquidGlass == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { LiquidGlass = value });
        }
    }

    public bool AutoCheckUpdates
    {
        get => _ctx.Settings.Current.AutoCheckUpdates;
        set
        {
            if (_ctx.Settings.Current.AutoCheckUpdates == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { AutoCheckUpdates = value });
        }
    }

    public bool IdleEco
    {
        get => _ctx.Settings.Current.IdleEco;
        set
        {
            if (_ctx.Settings.Current.IdleEco == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { IdleEco = value });
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => Set(ref _isScanning, value);
    }

    public JavaChoice? SelectedJava
    {
        get => _selectedJava;
        set
        {
            if (!Set(ref _selectedJava, value) || _applying || value is null) return;
            var path = value.Path;
            _ = _ctx.Settings.UpdateAsync(s => s with { JavaPath = path });
        }
    }

    private void SyncFromSettings()
    {
        _applying = true;
        try
        {
            EcoMode = _ctx.Settings.Current.EcoMode;
            RaiseNavLayout();
            OnPropertyChanged(nameof(BackgroundGlow));
            OnPropertyChanged(nameof(IdleEco));
            OnPropertyChanged(nameof(AutoCheckUpdates));
            OnPropertyChanged(nameof(LiquidGlass));
            Motion.SetEco(_eco);
            var current = _ctx.Settings.Current;
            SelectedTheme = ThemeChoices.First(t => ThemeService.IsLightTheme(t.Id) == ThemeService.IsLightTheme(current.Theme));
            OnPropertyChanged(nameof(HasNoBackgroundImage));
            var palette = ThemeService.Resolve(current);
            // не затираем то, что человек сейчас набирает, если запись уже означает тот же цвет
            if (ThemeService.TryParse(_accentText) != palette.Accent) AccentText = ThemeService.ToHex(palette.Accent);
            if (ThemeService.TryParse(_backgroundText) != palette.Background) BackgroundText = ThemeService.ToHex(palette.Background);
            if (palette.Panel is { } panel)
            {
                if (ThemeService.TryParse(_panelText) != panel) PanelText = ThemeService.ToHex(panel);
            }
            else PanelText = "";
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Поиск Java выполняется при первом открытии страницы (запускает java -version у найденных установок).</summary>
    public Task EnsureScannedAsync() => _scanned ? Task.CompletedTask : ScanAsync();

    private async Task ScanAsync()
    {
        IsScanning = true;
        _scanned = true;
        try
        {
            var found = await Task.Run(() => _ctx.Services.JavaDetector.DetectAsync(CancellationToken.None));
            var configured = _ctx.Settings.Current.JavaPath;

            _applying = true;
            try
            {
                JavaChoices.Clear();
                var auto = new JavaChoice("Автоматически (подбирается под версию игры)", null);
                JavaChoices.Add(auto);
                foreach (var java in found)
                {
                    var vendor = string.IsNullOrWhiteSpace(java.Vendor) ? "" : $" · {java.Vendor}";
                    JavaChoices.Add(new JavaChoice($"Java {java.MajorVersion}{vendor} · {java.Path}", java.Path));
                }

                var current = string.IsNullOrWhiteSpace(configured)
                    ? auto
                    : JavaChoices.FirstOrDefault(c => string.Equals(c.Path, configured, StringComparison.OrdinalIgnoreCase));
                if (current is null)
                {
                    current = new JavaChoice($"Своя: {configured}", configured);
                    JavaChoices.Add(current);
                }
                SelectedJava = current;
            }
            finally
            {
                _applying = false;
            }
        }
        finally
        {
            IsScanning = false;
        }
    }
}

/// <summary>Чтение темы из папки: theme.json (name, description, image, theme, accent) и картинка рядом.</summary>
internal static class ThemeFolder
{
    public static ThemePreset? Read(string dir)
    {
        try
        {
            var json = Path.Combine(dir, "theme.json");
            if (!File.Exists(json)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            var r = doc.RootElement;
            string? Str(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            var id = "folder:" + Path.GetFileName(dir);
            var name = Str("name") ?? Path.GetFileName(dir);
            var theme = string.Equals(Str("theme"), "light", StringComparison.OrdinalIgnoreCase) ? ThemeService.Light : ThemeService.Dark;
            var accent = Str("accent");
            string? image = null;
            if (Str("image") is { Length: > 0 } file)
            {
                var full = Path.GetFullPath(Path.Combine(dir, file));
                if (full.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase) && File.Exists(full)
                    && BackgroundImages.Extensions.Contains(Path.GetExtension(full).ToLowerInvariant())) image = full;
            }
            string? Hex(string key) => Str(key) is { } c && ThemeService.TryParse(c) is { } parsed ? ThemeService.ToHex(parsed) : null;
            double? Num(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? Math.Clamp(v.GetDouble(), 0, 0.9) : null;
            bool? Flag(string key) => r.TryGetProperty(key, out var v) && v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? v.GetBoolean() : null;
            var extras = new ThemeExtras(Hex("background"), Hex("panel"), Hex("text"), Num("dim"), Flag("auto"), Flag("glow"), Flag("liquid"), Str("layout"), Str("layoutNoModel"));
            return new ThemePreset(id, name, Str("description") ?? "", "", theme, accent, image, extras);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
