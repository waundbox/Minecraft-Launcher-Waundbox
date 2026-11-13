using System.Windows;
using System.Windows.Threading;
using Launcher.Core.Configuration;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Элемент главной страницы, который можно править в редакторе.</summary>
public sealed record EditorElement(string Id, string Title);

/// <summary>
/// Редактор главной страницы: выбор элемента, его положение, масштаб, прозрачность, скругление, видимость.
/// Менять можно только внешний вид — что делает кнопка, не настраивается.
/// </summary>
internal sealed class HomeEditorViewModel : ObservableObject
{
    private readonly UiContext _ctx;
    private readonly DispatcherTimer _saveTimer;
    // две раскладки («пространства»): 0 — главная с 3D-моделью, 1 — без неё
    private readonly string?[] _saved = new string?[2];
    private int _space = -1;

    public HomeEditorViewModel(UiContext ctx)
    {
        _ctx = ctx;
        Elements = BuildElements("home", true);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => SaveNow();

        LayoutState.Changed += _ =>
        {
            RaiseValues();
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        LayoutState.SelectionChanged += () =>
        {
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(HasSelection));
            RaiseValues();
        };
        LayoutState.EditingChanged += () =>
        {
            OnPropertyChanged(nameof(IsEditing));
            if (!LayoutState.Editing) SaveNow();
        };
        ctx.Settings.Changed += () => Application.Current.Dispatcher.BeginInvoke(Reload);

        StartCommand = new RelayCommand(() =>
        {
            OpenRequested?.Invoke();
            LayoutState.SetEditing(true);
        });
        DoneCommand = new RelayCommand(() => LayoutState.SetEditing(false));
        ResetElementCommand = new RelayCommand(() =>
        {
            if (LayoutState.Selected is not { } id) return;
            LayoutState.Checkpoint(force: true);
            LayoutState.Set(id, new ElementLayout());
        });
        ResetAllCommand = new RelayCommand(ResetAll);
        InitBackground();
        LoadTabs();
        ScaleUpCommand = new RelayCommand(() => Edit(l => l with { Scale = Math.Round(l.Scale * 10 + 1) / 10 }));
        ScaleDownCommand = new RelayCommand(() => Edit(l => l with { Scale = Math.Round(l.Scale * 10 - 1) / 10 }));
        ScaleResetCommand = new RelayCommand(() => Edit(l => l with { Scale = 1 }));
        UndoCommand = new RelayCommand(LayoutState.Undo);
        RedoCommand = new RelayCommand(LayoutState.Redo);
        LayoutState.GridChanged += () => OnPropertyChanged(nameof(SnapToGrid));
    }

    // ----- переход по страницам и вкладкам кнопками редактора -----

    public sealed record NavButton(string Id, string Title);

    private SettingsViewModel? _settingsPage;
    private Action<string>? _goToPage;

    public IReadOnlyList<NavButton> PageButtons { get; private set; } = Array.Empty<NavButton>();

    public IReadOnlyList<NavButton> SettingsTabButtons { get; } = new[]
    {
        new NavButton("0", "Общее"), new NavButton("1", "Оформление"), new NavButton("2", "Готовые темы")
    };

    /// <summary>Подключает переходы: главное окно открывает страницу, настройки переключают вкладку.</summary>
    public void Attach(SettingsViewModel settings, Action<string> goToPage)
    {
        _settingsPage = settings;
        _goToPage = goToPage;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.TabIndex)) OnPropertyChanged(nameof(CurrentSettingsTab));
        };
        _ctx.Account.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountViewModel.ShowSkinsTab)) OnPropertyChanged(nameof(CurrentAccountTab));
        };
        BuildPageButtons();
    }

    private void BuildPageButtons()
    {
        var hidden = TabCatalog.Hidden(_ctx.Settings.Current.HiddenTabs);
        var buttons = TabCatalog.All
            .Where(t => t.Id != "developer" && !(t.CanHide && hidden.Contains(t.Id)))
            .Select(t => new NavButton(t.Id, t.Title)).ToArray();
        if (buttons.Select(b => b.Id).SequenceEqual(PageButtons.Select(b => b.Id))) return;
        PageButtons = buttons;
        OnPropertyChanged(nameof(PageButtons));
        OnPropertyChanged(nameof(CurrentPageButton));
    }

    public NavButton? CurrentPageButton
    {
        get => PageButtons.FirstOrDefault(b => b.Id == _pageId);
        set { if (value is not null && value.Id != _pageId) _goToPage?.Invoke(value.Id); }
    }

    public bool IsSettingsPage => _pageId == "settings";
    public bool IsAccountPage => _pageId == "account";

    public IReadOnlyList<NavButton> AccountTabButtons { get; } = new[] { new NavButton("account", "Аккаунт"), new NavButton("skins", "Скины") };

    public NavButton? CurrentAccountTab
    {
        get => AccountTabButtons[_ctx.Account.ShowSkinsTab ? 1 : 0];
        set
        {
            if (value is null) return;
            if (value.Id == "skins") _ctx.Account.ShowSkinsTabCommand.Execute(null);
            else _ctx.Account.ShowAccountTabCommand.Execute(null);
        }
    }

    public NavButton? CurrentSettingsTab
    {
        get => SettingsTabButtons.FirstOrDefault(b => b.Id == (_settingsPage?.TabIndex ?? 0).ToString());
        set { if (value is not null && _settingsPage is not null) _settingsPage.TabIndex = int.Parse(value.Id); }
    }

    /// <summary>Редактору нужна главная страница: окно переключает на неё.</summary>
    public event Action? OpenRequested;

    private IReadOnlyList<EditorElement> _elements = Array.Empty<EditorElement>();
    private string _pageId = "home";

    /// <summary>Элементы страницы, открытой сейчас.</summary>
    public IReadOnlyList<EditorElement> Elements
    {
        get => _elements;
        private set => Set(ref _elements, value);
    }

    public string PageTitle => TabCatalog.All.FirstOrDefault(t => t.Id == _pageId)?.Title ?? "";

    private static IReadOnlyList<EditorElement> BuildElements(string pageId, bool showModel) =>
        TabCatalog.ElementsOf(pageId).Where(e => showModel || e.Id != "model").Select(e => new EditorElement(e.Id, e.Title)).ToArray();

    /// <summary>Окно сообщает, какая страница открыта: список элементов меняется, выбор сбрасывается.</summary>
    public void SetPage(string pageId)
    {
        if (_pageId == pageId) return;
        _pageId = pageId;
        Elements = BuildElements(pageId, ShowModel);
        LayoutState.Select(null);
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(CurrentPageButton));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsAccountPage));
    }

    /// <summary>Показывать 3D-модель на главной. Это же выбор «пространства» раскладки: у режима с моделью и без модели свои положения элементов.</summary>
    public bool ShowModel
    {
        get => _ctx.Settings.Current.ShowModel3D;
        set
        {
            if (_ctx.Settings.Current.ShowModel3D == value) return;
            SaveNow();
            _ = _ctx.Settings.UpdateAsync(s => s with { ShowModel3D = value });
        }
    }

    public string SpaceTitle => ShowModel ? "С 3D-моделью" : "Без 3D-модели";

    // ----- своё фоновое изображение -----

    public bool HasBackgroundImage => _ctx.Settings.Current.BackgroundImage is not null;

    public string BackgroundImageText => HasBackgroundImage ? "Своё изображение выбрано" : "Изображения нет: обычный фон";

    /// <summary>Автоподбор затемнения под яркость картинки.</summary>
    public bool BackgroundAuto
    {
        get => _ctx.Settings.Current.BackgroundImageAuto;
        set
        {
            if (_ctx.Settings.Current.BackgroundImageAuto == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { BackgroundImageAuto = value });
        }
    }

    public bool ShowManualDim => HasBackgroundImage && !BackgroundAuto;

    private double _dim = LauncherSettings.DefaultImageDim;
    private readonly DispatcherTimer _dimTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    /// <summary>Затемнение изображения (0–0,9).</summary>
    public double BackgroundDim
    {
        get => _dim;
        set
        {
            value = Math.Clamp(Math.Round(value, 2), 0, 0.9);
            if (!Set(ref _dim, value)) return;
            _dimTimer.Stop();
            _dimTimer.Start();
        }
    }

    public AsyncCommand ChooseBackgroundCommand { get; private set; } = null!;
    public AsyncCommand RemoveBackgroundCommand { get; private set; } = null!;

    private void InitBackground()
    {
        _dimTimer.Tick += (_, _) =>
        {
            _dimTimer.Stop();
            var dim = _dim;
            _ = _ctx.Settings.UpdateAsync(s => s with { BackgroundImageDim = dim });
        };
        ChooseBackgroundCommand = new AsyncCommand(async () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите изображение для фона",
                Filter = "Изображения (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var root = _ctx.Services.Paths.Root;
                var name = await BackgroundImages.ImportAsync(root, dialog.FileName);
                var old = _ctx.Settings.Current.BackgroundImage;
                var luminance = await Task.Run(() => BackgroundImages.Luminance(BackgroundImages.PathFor(root, name)!));
                await _ctx.Settings.UpdateAsync(s => s with { BackgroundImage = name, BackgroundImageLuminance = luminance });
                BackgroundImages.Delete(root, old);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                           or NotSupportedException or System.IO.FileFormatException or ArgumentException)
            {
                _ctx.Toasts.Show("Не удалось поставить изображение: " + ex.Message, ToastKind.Error);
            }
        });
        RemoveBackgroundCommand = new AsyncCommand(async () =>
        {
            var old = _ctx.Settings.Current.BackgroundImage;
            await _ctx.Settings.UpdateAsync(s => s with { BackgroundImage = null });
            BackgroundImages.Delete(_ctx.Services.Paths.Root, old);
        });
    }

    // ----- расположение меню -----

    public bool NavTopActive => NavLayouts.Normalize(_ctx.Settings.Current.NavLayout) == NavLayouts.Top;
    public bool NavSideActive => NavLayouts.Normalize(_ctx.Settings.Current.NavLayout) == NavLayouts.Side;

    public RelayCommand SetNavLayoutCommand => _setNavLayoutCommand ??= new RelayCommand(p =>
        _ = _ctx.Settings.UpdateAsync(s => ThemeLayoutStore.WithNav(s, NavLayouts.ToSetting(p as string))));
    private RelayCommand? _setNavLayoutCommand;

    // ----- вкладки сверху: порядок и видимость -----

    public System.Collections.ObjectModel.ObservableCollection<TabEditItem> Tabs { get; } = new();
    private bool _tabsLoading = true;

    private void LoadTabs()
    {
        _tabsLoading = true;
        try
        {
            var s = _ctx.Settings.Current;
            var hidden = TabCatalog.Hidden(s.HiddenTabs);
            Tabs.Clear();
            foreach (var id in TabCatalog.Order(s.TabOrder))
            {
                var tab = TabCatalog.All.First(t => t.Id == id);
                Tabs.Add(new TabEditItem(tab.Id, tab.Title, tab.CanHide, !(tab.CanHide && hidden.Contains(id)), SaveTabs, MoveTab));
            }
        }
        finally { _tabsLoading = false; }
    }

    private void MoveTab(TabEditItem item, int delta)
    {
        var from = Tabs.IndexOf(item);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Tabs.Count) return;
        Tabs.Move(from, to);
        SaveTabs();
    }

    private void SaveTabs()
    {
        if (_tabsLoading) return;
        var order = TabCatalog.Join(Tabs.Select(t => t.Id));
        var hidden = TabCatalog.Join(Tabs.Where(t => t.CanHide && !t.IsVisible).Select(t => t.Id));
        _ = _ctx.Settings.UpdateAsync(s => s with { TabOrder = order, HiddenTabs = hidden });
    }

    // ----- сохранение темы в папку themes -----

    public const int ThemeNameMax = 16;

    private string _themeName = "";
    private string _themeDescription = "";
    private string _themeText = "";
    private string _themeMessage = "";

    /// <summary>Название темы: обязательно, до 16 символов; по нему называется папка.</summary>
    public string ThemeName { get => _themeName; set { if (Set(ref _themeName, value)) ThemeMessage = ""; } }

    /// <summary>Описание темы: можно не заполнять.</summary>
    public string ThemeDescription { get => _themeDescription; set => Set(ref _themeDescription, value); }

    /// <summary>Свой цвет текста (#RRGGBB): пусто — подбирается автоматически под тему. Применяется сразу.</summary>
    public string ThemeTextColor
    {
        get => _themeText;
        set
        {
            if (!Set(ref _themeText, value)) return;
            OnPropertyChanged(nameof(ThemeTextInvalid));
            var text = value.Trim();
            if (text.Length == 0) _ = _ctx.Settings.UpdateAsync(s => s with { TextColor = null });
            else if (ThemeService.TryParse(text) is { } c) _ = _ctx.Settings.UpdateAsync(s => s with { TextColor = ThemeService.ToHex(c) });
        }
    }

    public bool ThemeTextInvalid => _themeText.Trim().Length > 0 && ThemeService.TryParse(_themeText.Trim()) is null;

    public string ThemeMessage { get => _themeMessage; private set => Set(ref _themeMessage, value); }

    public RelayCommand SaveThemeCommand => _saveThemeCommand ??= new RelayCommand(SaveTheme);
    private RelayCommand? _saveThemeCommand;

    /// <summary>Тема записана в папку: окно сохранения закрывается.</summary>
    public event Action? ThemeSaved;

    /// <summary>Открывает пустое окно сохранения: сообщение прошлого раза не показывается.</summary>
    public void ResetThemeMessage() => ThemeMessage = "";

    private void SaveTheme()
    {
        var name = _themeName.Trim();
        if (name.Length == 0) { ThemeMessage = "Впишите название темы."; return; }
        if (name.Length > ThemeNameMax) { ThemeMessage = $"Название — не больше {ThemeNameMax} символов."; return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')) { ThemeMessage = "В названии нельзя использовать \\ / : * ? \" < > | и точку в конце."; return; }
        if (ThemeTextInvalid) { ThemeMessage = "Цвет текста: нужно #RRGGBB или оставьте пустым."; return; }

        try
        {
            SaveNow(); // раскладка тоже попадёт в тему
            var s = _ctx.Settings.Current;
            var root = _ctx.Services.Paths.Root;
            var dir = Path.Combine(root, "themes", name);
            Directory.CreateDirectory(dir);

            string? imageFile = null;
            foreach (var old in Directory.EnumerateFiles(dir, "background.*")) File.Delete(old);
            if (BackgroundImages.PathFor(root, s.BackgroundImage) is { } source && File.Exists(source))
            {
                imageFile = "background" + Path.GetExtension(source).ToLowerInvariant();
                File.Copy(source, Path.Combine(dir, imageFile), overwrite: true);
            }

            var data = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = string.IsNullOrWhiteSpace(_themeDescription) ? null : _themeDescription.Trim(),
                ["image"] = imageFile,
                ["theme"] = ThemeService.IsLightTheme(s.Theme) ? ThemeService.Light : ThemeService.Dark,
                ["accent"] = s.AccentColor,
                ["background"] = s.BackgroundColor,
                ["panel"] = s.PanelColor,
                ["text"] = s.TextColor, // нет — цвет текста подбирается автоматически
                ["dim"] = s.BackgroundImageDim,
                ["auto"] = s.BackgroundImageAuto,
                ["glow"] = s.BackgroundGlow,
                ["liquid"] = s.LiquidGlass,
                ["layout"] = s.HomeLayout,
                ["layoutNoModel"] = s.HomeLayoutNoModel
            };
            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
            File.WriteAllText(Path.Combine(dir, "theme.json"), System.Text.Json.JsonSerializer.Serialize(data, options));
            ThemeMessage = "";
            // дальше правки редактора идут под этой темой
            _ = _ctx.Settings.UpdateAsync(cur =>
            {
                var t = cur with { ThemeKey = "folder:" + name };
                t = ThemeLayoutStore.WithLayout(t, 0, cur.HomeLayout);
                t = ThemeLayoutStore.WithLayout(t, 1, cur.HomeLayoutNoModel);
                return ThemeLayoutStore.WithNav(t, cur.NavLayout);
            });
            _ctx.Toasts.Show($"Тема «{name}» сохранена в папку themes", ToastKind.Success);
            ThemeSaved?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            ThemeMessage = "Не удалось сохранить тему: " + ex.Message;
        }
    }

    public RelayCommand ScaleUpCommand { get; }
    public RelayCommand ScaleDownCommand { get; }
    public RelayCommand ScaleResetCommand { get; }
    public bool IsEditing => LayoutState.Editing;
    public bool HasSelection => LayoutState.Selected is not null;

    public RelayCommand StartCommand { get; }
    public RelayCommand DoneCommand { get; }
    public RelayCommand ResetElementCommand { get; }
    public RelayCommand ResetAllCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }

    /// <summary>Привязка к сетке 10 px при перетаскивании и стрелках.</summary>
    public bool SnapToGrid
    {
        get => LayoutState.SnapStep > 0;
        set => LayoutState.SetSnap(value ? 10 : 0);
    }

    /// <summary>Сдвиг выбранного элемента стрелками (шаг сетки или 1 px).</summary>
    public bool Nudge(double dx, double dy)
    {
        if (LayoutState.Selected is not { } id) return false;
        LayoutState.Checkpoint();
        var l = LayoutState.Get(id);
        LayoutState.Set(id, l with { X = l.X + dx, Y = l.Y + dy });
        return true;
    }

    public EditorElement? Selected
    {
        get => Elements.FirstOrDefault(e => string.Equals(e.Id, LayoutState.Selected, StringComparison.OrdinalIgnoreCase));
        set => LayoutState.Select(value?.Id);
    }

    private ElementLayout Current => LayoutState.Selected is { } id ? LayoutState.Get(id) : new ElementLayout();

    private void Edit(Func<ElementLayout, ElementLayout> change)
    {
        if (LayoutState.Selected is not { } id) return;
        LayoutState.Checkpoint();
        LayoutState.Set(id, change(LayoutState.Get(id)));
    }

    public int Z { get => Current.Z; set => Edit(l => l with { Z = value }); }
    public double X { get => Current.X; set => Edit(l => l with { X = value }); }
    public double Y { get => Current.Y; set => Edit(l => l with { Y = value }); }
    public double Scale { get => Current.Scale; set => Edit(l => l with { Scale = value }); }
    public double StretchX { get => Current.StretchX; set => Edit(l => l with { StretchX = value }); }
    public double StretchY { get => Current.StretchY; set => Edit(l => l with { StretchY = value }); }
    public double Opacity { get => Current.Opacity; set => Edit(l => l with { Opacity = value }); }
    public double Corner { get => Current.Corner ?? 14; set => Edit(l => l with { Corner = value }); }
    public bool ElementVisible { get => Current.Visible; set => Edit(l => l with { Visible = value }); }

    private void RaiseValues()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Z));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(StretchX));
        OnPropertyChanged(nameof(StretchY));
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(Corner));
        OnPropertyChanged(nameof(ElementVisible));
    }

    public void ResetAll()
    {
        LayoutState.Checkpoint(force: true);
        LayoutState.Load(new Dictionary<string, ElementLayout>());
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        var idx = Math.Max(_space, 0);
        var json = HomeLayout.Serialize(LayoutState.Snapshot);
        if (json == _saved[idx]) return;
        _saved[idx] = json;
        _ = _ctx.Settings.UpdateAsync(s => ThemeLayoutStore.WithLayout(s, idx, json)); // правки хранятся и под выбранной темой
    }

    private void Reload()
    {
        var settings = _ctx.Settings.Current;
        BuildPageButtons();
        OnPropertyChanged(nameof(NavTopActive));
        OnPropertyChanged(nameof(NavSideActive));
        if (Tabs.Count == 0 || TabCatalog.Join(Tabs.Select(t => t.Id)) != TabCatalog.Join(TabCatalog.Order(settings.TabOrder))
            || TabCatalog.Join(Tabs.Where(t => t.CanHide && !t.IsVisible).Select(t => t.Id)) != TabCatalog.Join(TabCatalog.Hidden(settings.HiddenTabs).Where(id => TabCatalog.All.Any(t => t.Id == id && t.CanHide))))
            LoadTabs();
        if (!_dimTimer.IsEnabled && Math.Abs(_dim - settings.BackgroundImageDim) > 0.001)
        {
            _dim = settings.BackgroundImageDim;
            OnPropertyChanged(nameof(BackgroundDim));
        }
        if (ThemeService.TryParse(_themeText.Trim()) != ThemeService.TryParse(settings.TextColor) && !(ThemeTextInvalid && settings.TextColor is null))
        {
            _themeText = settings.TextColor ?? "";
            OnPropertyChanged(nameof(ThemeTextColor));
            OnPropertyChanged(nameof(ThemeTextInvalid));
        }
        OnPropertyChanged(nameof(HasBackgroundImage));
        OnPropertyChanged(nameof(BackgroundImageText));
        OnPropertyChanged(nameof(BackgroundAuto));
        OnPropertyChanged(nameof(ShowManualDim));
        var idx = settings.ShowModel3D ? 0 : 1;
        var json = idx == 0 ? settings.HomeLayout : settings.HomeLayoutNoModel;

        if (idx != _space)
        {
            // сменилось «пространство»: несохранённые правки уходят в прежнее, потом грузится раскладка нового
            if (_space >= 0) SaveNow();
            _space = idx;
            _saved[idx] = json;
            LayoutState.Load(HomeLayout.Parse(json));
            LayoutState.ClearHistory();
            Elements = BuildElements(_pageId, ShowModel);
            LayoutState.Select(null);
            OnPropertyChanged(nameof(ShowModel));
            OnPropertyChanged(nameof(SpaceTitle));
            return;
        }

        if (json == _saved[idx]) return; // это наше же сохранение
        _saved[idx] = json;
        LayoutState.Load(HomeLayout.Parse(json));
    }

    /// <summary>Клавиши редактора: стрелки двигают выбранный элемент, Ctrl+Z / Ctrl+Y — отмена и повтор, Esc — выход.</summary>
    public bool HandleKey(System.Windows.Input.KeyEventArgs e)
    {
        if (!LayoutState.Editing || e.Handled || System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox) return false;
        var ctrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
        var step = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)
            ? 10 : Math.Max(1, LayoutState.SnapStep);
        switch (e.Key)
        {
            case System.Windows.Input.Key.Z when ctrl: LayoutState.Undo(); return true;
            case System.Windows.Input.Key.Y when ctrl: LayoutState.Redo(); return true;
            case System.Windows.Input.Key.Escape: LayoutState.SetEditing(false); return true;
            case System.Windows.Input.Key.Left: return Nudge(-step, 0);
            case System.Windows.Input.Key.Right: return Nudge(step, 0);
            case System.Windows.Input.Key.Up: return Nudge(0, -step);
            case System.Windows.Input.Key.Down: return Nudge(0, step);
            default: return false;
        }
    }
}
