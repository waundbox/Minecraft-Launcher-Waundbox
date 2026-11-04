using System.Collections.ObjectModel;
using System.Diagnostics;
using Launcher.Core.Configuration;
using System.Windows.Media;
using Launcher.UI.Controls;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Главное окно: страницы, навигация, диалоги и уведомления.</summary>
internal sealed class MainViewModel : ObservableObject
{
    private readonly UiContext _ctx;
    private PageViewModel _current;
    private int _currentIndex;
    private readonly Dictionary<string, PageViewModel> _byId;
    private string _fpsText = "";
    private bool _showFps;
    private int _frames;
    private long _fpsStart;

    public MainViewModel(UiContext ctx)
    {
        _ctx = ctx;
        Home = new HomeViewModel(ctx);
        Profiles = new ProfilesViewModel(ctx);
        Settings = new SettingsViewModel(ctx);

        Developer = new DeveloperViewModel(ctx);
        _byId = new Dictionary<string, PageViewModel>
        {
            ["home"] = Home, ["profiles"] = Profiles, ["logs"] = ctx.Console,
            ["account"] = ctx.Account, ["settings"] = Settings, ["developer"] = Developer
        };
        Pages = new ObservableCollection<PageViewModel> { Home, Profiles, ctx.Console, ctx.Account, Settings };
        _current = Home;
        Home.IsSelected = true;

        NavigateCommand = new RelayCommand(p =>
        {
            if (p is PageViewModel page) Navigate(page);
        });
        CloseDialogCommand = new RelayCommand(() =>
        {
            switch (ctx.Dialogs.Current)
            {
                case NewProfileViewModel create: create.CancelCommand.Execute(null); break;
                case ConfirmViewModel confirm: confirm.CancelCommand.Execute(null); break;
                case not null: ctx.Dialogs.Close(); break;
            }
        });
        ctx.HomeEditor.OpenRequested += () => Navigate(Home);
        NextCommand = new RelayCommand(() => Step(1));
        PreviousCommand = new RelayCommand(() => Step(-1));

        ctx.Settings.Changed += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(ApplyDeveloperAndIcons);

        AsyncCommand.UnhandledError = ex =>
        {
            ctx.Services.Logger.Error("Необработанная ошибка в команде интерфейса.", ex);
            ctx.Toasts.Show("Что-то пошло не так: " + ex.Message, ToastKind.Error);
        };

        // игра закрылась с ошибкой — показываем консоль
        ctx.Launcher.Crashed += profile =>
        {
            ctx.Console.Select(profile);
            Navigate(ctx.Console);
        };
    }

    public HomeViewModel Home { get; }
    public ProfilesViewModel Profiles { get; }
    public SettingsViewModel Settings { get; }
    public DeveloperViewModel Developer { get; }
    public ObservableCollection<PageViewModel> Pages { get; }

    public UiContext Context => _ctx;
    public ToastService Toasts => _ctx.Toasts;
    public DialogService Dialogs => _ctx.Dialogs;
    public AccountViewModel Account => _ctx.Account;
    public LaunchController Launcher => _ctx.Launcher;

    /// <summary>Счётчик кадров в углу окна (включается во вкладке разработчика).</summary>
    public bool ShowFps
    {
        get => _showFps;
        private set => Set(ref _showFps, value);
    }

    public string FpsText { get => _fpsText; private set => Set(ref _fpsText, value); }

    private void OnRendering(object? sender, EventArgs e)
    {
        _frames++;
        var elapsed = Stopwatch.GetElapsedTime(_fpsStart).TotalSeconds;
        if (elapsed < 0.5) return;
        FpsText = $"{_frames / elapsed:0} FPS · модель {SkinViewer.LastRenderMs:0.0} мс";
        _frames = 0;
        _fpsStart = Stopwatch.GetTimestamp();
    }

    private void UpdateFps(bool show)
    {
        if (show == _showFps) return;
        ShowFps = show;
        if (show)
        {
            _frames = 0;
            _fpsStart = Stopwatch.GetTimestamp();
            FpsText = "… FPS";
            CompositionTarget.Rendering += OnRendering;
        }
        else CompositionTarget.Rendering -= OnRendering;
    }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand CloseDialogCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }

    public PageViewModel Current
    {
        get => _current;
        private set => Set(ref _current, value);
    }

    public int CurrentIndex
    {
        get => _currentIndex;
        private set => Set(ref _currentIndex, value);
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _ctx.Settings.LoadAsync();
            Motion.SetEco(_ctx.Settings.Current.EcoMode);
            ProfileIcons.Configure(Path.Combine(_ctx.Services.Paths.Root, "icons"), _ctx.Settings.Current.IconSize);
            ApplyDeveloperAndIcons();
            await _ctx.Catalog.ReloadAsync();
            await _ctx.Account.RefreshAsync();
        }
        catch (Exception ex)
        {
            _ctx.Services.Logger.Error("Ошибка при запуске интерфейса.", ex);
            _ctx.Toasts.Show("Не удалось прочитать настройки: " + ex.Message, ToastKind.Error);
        }

        if (!_ctx.Catalog.HasItems) _ctx.Toasts.Show("Создайте первый профиль — кнопка «Создать профиль» на главной.", ToastKind.Info);
    }

    /// <summary>
    /// Применяет настройки интерфейса: порядок и видимость вкладок, вкладка разработчика, счётчик кадров,
    /// разрешение значков (при смене перечитывает каталог профилей).
    /// </summary>
    private void ApplyDeveloperAndIcons()
    {
        var settings = _ctx.Settings.Current;
        RebuildPages(settings);
        UpdateFps(settings.ShowFps);
        SkinViewer.SetMaxGraphics(settings.MaxGraphics && settings.DeveloperMode);

        if (settings.IconSize != ProfileIcons.Size && ProfileIcons.Directory.Length > 0)
        {
            ProfileIcons.Configure(ProfileIcons.Directory, settings.IconSize);
            _ = _ctx.Catalog.ReloadAsync();
        }
    }

    private void RebuildPages(LauncherSettings settings)
    {
        var hidden = TabCatalog.Hidden(settings.HiddenTabs);
        var desired = new List<PageViewModel>();
        foreach (var id in TabCatalog.Order(settings.TabOrder))
        {
            if (id == "developer" && !settings.DeveloperMode) continue;
            var tab = TabCatalog.All.First(t => t.Id == id);
            if (tab.CanHide && hidden.Contains(id)) continue;
            desired.Add(_byId[id]);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Pages.Count && ReferenceEquals(Pages[i], desired[i])) continue;
            var existing = Pages.IndexOf(desired[i]);
            if (existing >= 0) Pages.Move(existing, i);
            else Pages.Insert(i, desired[i]);
        }
        while (Pages.Count > desired.Count) Pages.RemoveAt(Pages.Count - 1);

        if (!Pages.Contains(_current)) Navigate(Home);
        else
        {
            foreach (var p in Pages) p.IsSelected = ReferenceEquals(p, _current);
            CurrentIndex = Pages.IndexOf(_current);
        }
    }

    private bool CanStep(int step)
    {
        var target = _currentIndex + step;
        return target >= 0 && target < Pages.Count;
    }

    private void Step(int step)
    {
        if (CanStep(step)) Navigate(Pages[_currentIndex + step]);
    }

    /// <summary>Переносит вкладку на новое место (перетаскивание в редакторе) и запоминает порядок, не трогая скрытые вкладки.</summary>
    public void MoveTab(PageViewModel page, int newIndex)
    {
        var from = Pages.IndexOf(page);
        if (from < 0 || newIndex < 0 || newIndex >= Pages.Count || from == newIndex) return;
        Pages.Move(from, newIndex);
        CurrentIndex = Pages.IndexOf(_current);

        var ids = _byId.ToDictionary(p => p.Value, p => p.Key);
        var shown = new Queue<string>(Pages.Select(p => ids[p]));
        var shownSet = shown.ToHashSet();
        var order = TabCatalog.Order(_ctx.Settings.Current.TabOrder)
            .Select(id => shownSet.Contains(id) ? shown.Dequeue() : id)
            .ToList();
        var joined = TabCatalog.Join(order);
        _ = _ctx.Settings.UpdateAsync(s => s with { TabOrder = joined });
    }

    public void Navigate(PageViewModel page)
    {
        if (!ReferenceEquals(page, Home)) LayoutState.SetEditing(false); // режим правки относится к главной странице
        if (ReferenceEquals(page, _current)) return;
        foreach (var p in Pages) p.IsSelected = ReferenceEquals(p, page);
        CurrentIndex = Pages.ToList().IndexOf(page);
        Current = page;
        if (page is SettingsViewModel settings) _ = settings.EnsureScannedAsync();
    }
}
