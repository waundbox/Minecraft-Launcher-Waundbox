using System.Collections.ObjectModel;
using Launcher.Core.Configuration;
using Launcher.UI.Controls;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

public sealed record JavaChoice(string Title, string? Path);

public sealed record ThemeChoice(string Id, string Title);

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

    public SettingsViewModel(UiContext ctx) : base("Настройки", "")
    {
        _ctx = ctx;
        ScanCommand = new AsyncCommand(ScanAsync, () => !_isScanning);
        OpenLauncherFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_ctx.Services.Paths.Root));
        OpenLogsFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(Path.Combine(_ctx.Services.Paths.Root, "logs")));
        OpenGameFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_ctx.Services.Paths.GameRoot));
        RestartCommand = new RelayCommand(Restart);
        ThemeChoices = new[] { new ThemeChoice(ThemeService.Dark, "Тёмная"), new ThemeChoice(ThemeService.Light, "Светлая") };
        _selectedTheme = ThemeChoices[0];
        SetAccentCommand = new RelayCommand(p => ApplyColor(accent: p as string));
        SetBackgroundCommand = new RelayCommand(p => ApplyColor(background: p as string));
        ResetColorsCommand = new RelayCommand(() =>
            _ = _ctx.Settings.UpdateAsync(s => s with { AccentColor = null, BackgroundColor = null }));
        _ctx.Settings.Changed += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(SyncFromSettings);
    }

    // ----- тема и цвета -----

    public IReadOnlyList<ThemeChoice> ThemeChoices { get; }
    public RelayCommand SetAccentCommand { get; }
    public RelayCommand SetBackgroundCommand { get; }
    public RelayCommand ResetColorsCommand { get; }

    public IReadOnlyList<ColorChoice> AccentSwatches { get; } = new[]
    {
        "#8CF0B8", "#1E9E6A", "#7AB8FF", "#3B82F6", "#B79CFF", "#8B5CF6", "#FF8FB1", "#E5484D", "#FFB86B", "#F59E0B", "#FFE27A", "#6EE7E7"
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
    public bool IdleEco
    {
        get => _ctx.Settings.Current.IdleEco;
        set
        {
            if (_ctx.Settings.Current.IdleEco == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { IdleEco = value });
        }
    }

    private string _curseForgeKey = "";

    /// <summary>Ключ API CurseForge для импорта сборок CurseForge; сохраняется при каждом изменении.</summary>
    public string CurseForgeKey
    {
        get => _curseForgeKey;
        set
        {
            if (!Set(ref _curseForgeKey, value) || _applying) return;
            var key = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _ = _ctx.Settings.UpdateAsync(s => s with { CurseForgeApiKey = key });
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
            OnPropertyChanged(nameof(BackgroundGlow));
            OnPropertyChanged(nameof(IdleEco));
            CurseForgeKey = _ctx.Settings.Current.CurseForgeApiKey ?? "";
            Motion.SetEco(_eco);
            var current = _ctx.Settings.Current;
            SelectedTheme = ThemeChoices.First(t => ThemeService.IsLightTheme(t.Id) == ThemeService.IsLightTheme(current.Theme));
            var palette = ThemeService.Resolve(current);
            // не затираем то, что человек сейчас набирает, если запись уже означает тот же цвет
            if (ThemeService.TryParse(_accentText) != palette.Accent) AccentText = ThemeService.ToHex(palette.Accent);
            if (ThemeService.TryParse(_backgroundText) != palette.Background) BackgroundText = ThemeService.ToHex(palette.Background);
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
