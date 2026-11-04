using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

public sealed record IconSizeChoice(int Size, string Title);

/// <summary>Строка редактора: вкладка навигации (порядок и видимость).</summary>
public sealed class TabEditItem : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<TabEditItem, int> _move;
    private bool _isVisible;

    internal TabEditItem(string id, string title, bool canHide, bool visible, Action changed, Action<TabEditItem, int> move)
    {
        Id = id;
        Title = title;
        CanHide = canHide;
        _isVisible = visible;
        _changed = changed;
        _move = move;
        MoveUpCommand = new RelayCommand(() => _move(this, -1));
        MoveDownCommand = new RelayCommand(() => _move(this, 1));
    }

    public string Id { get; }
    public string Title { get; }
    public bool CanHide { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set { if (Set(ref _isVisible, value)) _changed(); }
    }
}

/// <summary>Вкладка разработчика: проверочные параметры, счётчик кадров, диагностика и редактор интерфейса.</summary>
internal sealed class DeveloperViewModel : PageViewModel
{
    private readonly UiContext _ctx;
    private IconSizeChoice _selected;
    private string _info = "";
    private bool _loading = true;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private bool _autoRefresh = true;

    public DeveloperViewModel(UiContext ctx) : base("Разработчик", "")
    {
        _ctx = ctx;
        SizeChoices = new[] { 16, 24, 32, 48, 64, 96, 128 }
            .Select(s => new IconSizeChoice(s, s == 32 ? "32 px · стандарт" : $"{s} px"))
            .ToArray();
        _selected = FindSize(ctx.Settings.Current.IconSize);

        ExitCommand = new RelayCommand(() => _ = _ctx.Settings.UpdateAsync(s => s with { DeveloperMode = false, ShowFps = false }));
        RefreshInfoCommand = new RelayCommand(RefreshInfo);
        CopyInfoCommand = new RelayCommand(CopyInfo);
        ToastCommand = new RelayCommand(p =>
        {
            var key = p as string;
            var kind = key switch
            {
                "success" => ToastKind.Success,
                "warning" => ToastKind.Warning,
                "error" => ToastKind.Error,
                _ => ToastKind.Info
            };
            _ctx.Toasts.Show("Проверочное уведомление: " + kind, kind);
        });
        ReloadProfilesCommand = new RelayCommand(() => _ = _ctx.Catalog.ReloadAsync());
        ReloadIconsCommand = new RelayCommand(() =>
        {
            ProfileIcons.Refresh();
            _ = _ctx.Catalog.ReloadAsync();
            RefreshInfo();
        });
        CollectGarbageCommand = new RelayCommand(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            RefreshInfo();
        });
        OpenLauncherFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_ctx.Services.Paths.Root));
        OpenLogsFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(System.IO.Path.Combine(_ctx.Services.Paths.Root, "logs")));
        ResetLayoutCommand = new RelayCommand(() =>
        {
            _ = _ctx.Settings.UpdateAsync(s => s with { TabOrder = null, HiddenTabs = null, HiddenHomeParts = null });
            _ctx.HomeEditor.ResetAll();
            LoadLayout();
        });

        LoadLayout();
        RefreshInfo();

        // диагностика обновляется сама, пока вкладка открыта (раз в 2 секунды)
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshInfo();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsSelected)) UpdateTimer();
        };

        ctx.Settings.Changed += () => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var found = FindSize(ctx.Settings.Current.IconSize);
            if (!ReferenceEquals(found, _selected)) { _selected = found; OnPropertyChanged(nameof(SelectedSize)); }
            OnPropertyChanged(nameof(ShowFps));
            OnPropertyChanged(nameof(MaxGraphics));
        });
    }

    // ----- значки -----

    public IReadOnlyList<IconSizeChoice> SizeChoices { get; }

    private IconSizeChoice FindSize(int size) => SizeChoices.FirstOrDefault(c => c.Size == size) ?? SizeChoices.First(c => c.Size == 32);

    /// <summary>Разрешение, в котором загружаются значки профилей.</summary>
    public IconSizeChoice SelectedSize
    {
        get => _selected;
        set
        {
            if (value is null || !Set(ref _selected, value)) return;
            var size = value.Size;
            _ = _ctx.Settings.UpdateAsync(s => s with { IconSize = size });
        }
    }

    // ----- проверка -----

    /// <summary>Счётчик кадров в правом нижнем углу окна. В покое WPF не перерисовывает окно, поэтому число тогда низкое — это нормально.</summary>
    public bool ShowFps
    {
        get => _ctx.Settings.Current.ShowFps;
        set
        {
            if (_ctx.Settings.Current.ShowFps == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { ShowFps = value });
        }
    }

    /// <summary>Максимальная графика: 3D-модель всегда рисуется в полном размере окна без потери качества (грузит процессор сильнее).</summary>
    public bool MaxGraphics
    {
        get => _ctx.Settings.Current.MaxGraphics;
        set
        {
            if (_ctx.Settings.Current.MaxGraphics == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { MaxGraphics = value });
        }
    }

    public RelayCommand ExitCommand { get; }
    public RelayCommand RefreshInfoCommand { get; }
    public RelayCommand CopyInfoCommand { get; }
    public RelayCommand ToastCommand { get; }
    public RelayCommand ReloadProfilesCommand { get; }
    public RelayCommand ReloadIconsCommand { get; }
    public RelayCommand CollectGarbageCommand { get; }
    public RelayCommand OpenLauncherFolderCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }

    /// <summary>Обновлять диагностику автоматически, пока открыта вкладка.</summary>
    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            if (!Set(ref _autoRefresh, value)) return;
            UpdateTimer();
        }
    }

    private void UpdateTimer()
    {
        if (IsSelected && _autoRefresh)
        {
            RefreshInfo();
            _timer.Start();
        }
        else _timer.Stop();
    }

    public string Info { get => _info; private set => Set(ref _info, value); }

    private void RefreshInfo()
    {
        var s = _ctx.Settings.Current;
        var paths = _ctx.Services.Paths;
        var text = new StringBuilder();
        text.AppendLine($"Waundbox {typeof(DeveloperViewModel).Assembly.GetName().Version?.ToString(3)}");
        text.AppendLine($"Среда: {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}");
        text.AppendLine($"Память процесса: {Environment.WorkingSet / 1024 / 1024} МБ · управляемая куча: {GC.GetTotalMemory(false) / 1024 / 1024} МБ");
        text.AppendLine($"Анимации: {App.AppliedFps} кадров/с · экономный режим: {(s.EcoMode ? "да" : "нет")}");
        text.AppendLine($"Профилей: {_ctx.Catalog.Items.Count} · сеансов логов: {_ctx.Console.Sessions.Count}");
        text.AppendLine($"Значков в папке: {ProfileIcons.Names().Count} · разрешение: {ProfileIcons.Size} px");
        text.AppendLine($"Тема: {s.Theme ?? "dark"} · 3D-персонаж: {(s.ShowModel3D ? "вкл" : "выкл")}, вращение: {(s.ModelSpin ? "вкл" : "выкл")}");
        text.AppendLine($"Аккаунт для запуска: {s.PreferredAccount ?? "автоматически"}");
        text.AppendLine($"Ключ CurseForge: {(string.IsNullOrWhiteSpace(s.CurseForgeApiKey) ? "встроенный" : "из настроек")}");
        text.AppendLine($"Папка лаунчера: {paths.Root}");
        text.AppendLine($"Папка игры: {paths.GameRoot}");
        text.AppendLine($"Файл настроек: {paths.SettingsFile}");
        text.Append($"Значки: {ProfileIcons.Directory}");
        Info = text.ToString();
    }

    private void CopyInfo()
    {
        try
        {
            Clipboard.SetText(Info);
            _ctx.Toasts.Show("Диагностика скопирована.", ToastKind.Success);
        }
        catch (COMException)
        {
            _ctx.Toasts.Show("Буфер обмена занят другой программой.", ToastKind.Warning);
        }
    }

    // ----- редактор интерфейса -----

    public ObservableCollection<TabEditItem> Tabs { get; } = new();
    public RelayCommand ResetLayoutCommand { get; }

    /// <summary>Редактор главной: перетаскивание элементов и точная настройка каждого свойства.</summary>
    public HomeEditorViewModel Editor => _ctx.HomeEditor;

    private void LoadLayout()
    {
        _loading = true;
        try
        {
            var s = _ctx.Settings.Current;
            var hiddenTabs = TabCatalog.Hidden(s.HiddenTabs);
            Tabs.Clear();
            foreach (var id in TabCatalog.Order(s.TabOrder))
            {
                var tab = TabCatalog.All.First(t => t.Id == id);
                Tabs.Add(new TabEditItem(tab.Id, tab.Title, tab.CanHide, !(tab.CanHide && hiddenTabs.Contains(id)), SaveLayout, MoveTab));
            }

        }
        finally
        {
            _loading = false;
        }
    }

    private void MoveTab(TabEditItem item, int delta)
    {
        var from = Tabs.IndexOf(item);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Tabs.Count) return;
        Tabs.Move(from, to);
        SaveLayout();
    }

    private void SaveLayout()
    {
        if (_loading) return;
        var order = TabCatalog.Join(Tabs.Select(t => t.Id));
        var hiddenTabs = TabCatalog.Join(Tabs.Where(t => t.CanHide && !t.IsVisible).Select(t => t.Id));
        _ = _ctx.Settings.UpdateAsync(s => s with { TabOrder = order, HiddenTabs = hiddenTabs });
    }
}
