using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Launcher.Core.Updates;
using Launcher.Infrastructure.Updates;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>
/// Обновление лаунчера: проверка новой версии на GitHub, окно «Доступно обновление», кнопка «Обновление» справа от ника,
/// установка прямо из лаунчера и обязательные обновления (окно нельзя закрыть, оно остаётся после перезапуска).
/// </summary>
internal sealed partial class UpdateController : ObservableObject
{
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromHours(4);

    private readonly UiContext _ctx;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _installCts;
    private ReleaseInfo? _latest;
    private Version? _pending;       // обязательная версия, запомненная в настройках (когда сети нет)
    private Version? _announced;     // версия, о которой окно уже показывалось в этом запуске
    private bool _required;
    private bool _checking;
    private bool _installing;
    private bool _dialogOpen;
    private bool _confirmedRunning;
    private bool _started;
    private double _progress;
    private string _status = "";
    private string _error = "";
    private string _lastCheck = "";
    private UpdateCheckResult? _lastResult;   // свежий ответ GitHub: частые нажатия «Проверить обновления» не тратят лимит запросов
    private DateTime _lastResultAt;

    public UpdateController(UiContext ctx)
    {
        _ctx = ctx;
        Current = AppVersion.Normalize(typeof(UpdateController).Assembly.GetName().Version ?? new Version(0, 0, 0));
        _lastCheck = $"Установлена версия {Current.ToString(3)}.";

        InstallCommand = new AsyncCommand(InstallAsync, () => !_installing && CanInstallHere);
        OpenPageCommand = new RelayCommand(OpenPage);
        LaterCommand = new RelayCommand(() => { if (_dialogOpen && !_required) _ctx.Dialogs.Close(); }, () => !_installing);
        ExitCommand = new RelayCommand(Exit);
        CancelInstallCommand = new RelayCommand(() => _installCts?.Cancel(), () => _installing);
        CheckNowCommand = new AsyncCommand(() => CheckAsync(manual: true), () => !_checking);
        ShowDialogCommand = new RelayCommand(ShowDialog);

        _ctx.Dialogs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DialogService.Current)) return;
            var open = _ctx.Dialogs.Current is UpdateDialogViewModel;
            if (open == _dialogOpen) return;
            _dialogOpen = open;
            OnPropertyChanged(nameof(IsDialogOpen));
            OnPropertyChanged(nameof(CanDismiss));
        };

        _timer = new DispatcherTimer { Interval = RecheckEvery };
        _timer.Tick += (_, _) =>
        {
            if (_required || _ctx.Settings.Current.AutoCheckUpdates) _ = CheckAsync(manual: false);
        };
    }

    // ----- состояние для окна, вкладки и настроек -----

    public Version Current { get; }
    public string CurrentText => Current.ToString(3);

    public ReleaseInfo? Latest => _latest;

    /// <summary>Есть что показывать: новая версия найдена или обязательное обновление запомнено.</summary>
    public bool HasUpdate => _latest is not null || _required;

    public bool IsRequired => _required;
    public bool IsDialogOpen => _dialogOpen;
    public bool IsInstalling => _installing;
    public bool IsNotInstalling => !_installing;
    public bool IsChecking { get => _checking; private set => Set(ref _checking, value); }

    public string Heading => _required ? "Требуется обновление" : "Доступно обновление";
    public string RequiredHint => "Эта версия обязательна: пока лаунчер не обновлён, пользоваться им нельзя.";

    public string VersionLine
    {
        get
        {
            var target = _latest?.Version ?? _pending;
            return target is null ? $"Сейчас установлена версия {CurrentText}" : $"Версия {target.ToString(3)} · у вас {CurrentText}";
        }
    }

    /// <summary>«Что нового» из описания релиза, без разметки Markdown.</summary>
    public string Notes => _latest is null ? "" : CleanNotes(_latest.Notes);
    public bool HasNotes => Notes.Length > 0;

    public bool CanInstallHere => _latest is not null && UpdateDownloader.CanInstall(_latest);
    public bool ShowInstall => CanInstallHere && !_installing;
    public bool ShowManual => !_installing;
    public bool ShowRetry => _required && _latest is null && !_installing;
    public bool ShowExit => _required && !_installing;
    public bool CanDismiss => _dialogOpen && !_required && !_installing;

    public string Hint =>
        _latest is null && _required ? "Не удалось проверить обновление. Подключитесь к интернету или скачайте установщик со страницы релиза."
        : _latest is not null && !CanInstallHere ? "Установить прямо из лаунчера не получится: у этого выпуска нет проверочной суммы установщика. Скачайте установщик со страницы релиза."
        : "";
    public bool HasHint => Hint.Length > 0;

    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string StatusText { get => _status; private set { if (Set(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
    public string Error { get => _error; private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    /// <summary>Итог последней проверки для страницы настроек.</summary>
    public string LastCheckText { get => _lastCheck; private set => Set(ref _lastCheck, value); }

    public AsyncCommand InstallCommand { get; }
    public RelayCommand OpenPageCommand { get; }
    public RelayCommand LaterCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand CancelInstallCommand { get; }
    public AsyncCommand CheckNowCommand { get; }

    /// <summary>Кнопка «Обновление» в верхней панели (справа от ника): открывает окно обновления.</summary>
    public RelayCommand ShowDialogCommand { get; }

    private void Refresh() => OnPropertyChanged(string.Empty); // обновить всё сразу

    // ----- запуск и проверка -----

    /// <summary>Вызывается один раз после загрузки настроек: поднимает запомненное обязательное обновление и проверяет сеть.</summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        var settings = _ctx.Settings.Current;
        if (settings.RequiredUpdateVersion is { } saved)
        {
            if (AppVersion.TryParse(saved, out var pending, out _) && pending > Current)
            {
                _pending = pending;
                _required = true;
                Refresh();
                ShowDialog();
            }
            else Persist(null); // нужная версия уже установлена
        }

        _timer.Start();
        if (_required || settings.AutoCheckUpdates)
        {
            await Task.Delay(TimeSpan.FromSeconds(2)); // не мешаем запуску
            await CheckAsync(manual: false);
        }
    }

    /// <summary>Спрашивает GitHub о новой версии. Ошибки сети не выбрасываются: при ручной проверке они показываются, при автоматической — только в журнале.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_checking || _installing) return;
        if (manual && _lastResult is { } cached && DateTime.UtcNow - _lastResultAt < TimeSpan.FromMinutes(2))
        {
            Apply(cached, manual: true);
            return;
        }

        IsChecking = true;
        try
        {
            var result = await _ctx.Services.UpdateChecker.CheckAsync(Current, CancellationToken.None);
            _lastResult = result;
            _lastResultAt = DateTime.UtcNow;
            Apply(result, manual);
        }
        catch (UpdateException ex)
        {
            _ctx.Services.Logger.Warn("Проверка обновлений не удалась: " + ex.Message);
            LastCheckText = "Проверить не удалось: " + ex.Message;
            if (manual) _ctx.Toasts.Show(ex.Message, ToastKind.Warning);
            if (_required) { Error = ex.Message; Refresh(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ctx.Services.Logger.Error("Непредвиденная ошибка при проверке обновлений.", ex);
            LastCheckText = "Проверить не удалось.";
            if (manual) _ctx.Toasts.Show("Не удалось проверить обновления.", ToastKind.Warning);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void Apply(UpdateCheckResult result, bool manual)
    {
        Error = "";
        var stamp = DateTime.Now.ToString("HH:mm");

        if (result.Latest is not { } latest)
        {
            _latest = null;
            _pending = null;
            _required = false;
            _ctx.Dialogs.Unlock();
            if (_dialogOpen) _ctx.Dialogs.Close();
            Persist(null);
            Refresh();
            LastCheckText = $"Проверено в {stamp}: у вас последняя версия.";
            if (manual) _ctx.Toasts.Show($"У вас последняя версия: {CurrentText}.", ToastKind.Success);
            return;
        }

        var wasRequired = _required;
        _latest = latest;
        _pending = result.IsRequired ? latest.Version : null;
        _required = result.IsRequired;
        Persist(_required ? latest.Version.ToString(3) : null);
        Refresh();
        LastCheckText = $"Проверено в {stamp}: доступна версия {latest.Version.ToString(3)}.";

        if (wasRequired && !_required)
        {
            _ctx.Dialogs.Unlock(); // обязательность сняли в описании релиза — блокировка уходит
            if (_dialogOpen) _ctx.Dialogs.Close();
        }

        if (_dialogOpen)
        {
            if (_required) _ctx.Dialogs.Lock();
            return;
        }

        var firstTime = _announced != latest.Version;
        if (_required || manual || (firstTime && !_ctx.Dialogs.IsOpen)) ShowDialog();
    }

    private void ShowDialog()
    {
        if (_latest is { } latest) _announced = latest.Version;
        var dialog = new UpdateDialogViewModel(this);
        if (_required) _ctx.Dialogs.ShowLocked(dialog);
        else _ctx.Dialogs.Show(dialog);
    }

    private void Persist(string? version)
    {
        if (_ctx.Settings.Current.RequiredUpdateVersion == version) return;
        _ = _ctx.Settings.UpdateAsync(s => s with { RequiredUpdateVersion = version });
    }

    // ----- кнопки -----

    private void OpenPage()
    {
        if (!ShellHelper.Open(_latest?.PageUrl ?? UpdateSource.ReleasesPage))
            _ctx.Toasts.Show("Не удалось открыть страницу: " + UpdateSource.ReleasesPage, ToastKind.Error);
    }

    private void Exit()
    {
        if (Application.Current.MainWindow is MainWindow window) window.ForceClose();
        else Application.Current.Shutdown();
    }

    private async Task InstallAsync()
    {
        if (_latest is not { } release || !UpdateDownloader.CanInstall(release)) return;

        if (_ctx.Launcher.IsRunning && !_confirmedRunning)
        {
            _confirmedRunning = true;
            StatusText = "Сейчас запущена игра. Она продолжит работать, но лаунчер закроется и вывод игры в нём больше не будет виден. Нажмите «Установить» ещё раз, чтобы продолжить.";
            return;
        }

        _installing = true;
        _installCts = new CancellationTokenSource();
        Error = "";
        Progress = 0;
        StatusText = "Скачиваю обновление…";
        Refresh();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "Waundbox-update");
            var progress = new Progress<double>(value => Progress = value * 100);
            var path = await _ctx.Services.UpdateDownloader.DownloadAsync(release, directory, progress, _installCts.Token);

            StatusText = "Запускаю установщик…";
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS"
            });
            _ctx.Services.Logger.Info($"Запущен установщик версии {release.Version}, лаунчер закрывается.");
            await Task.Delay(700);
            Exit();
        }
        catch (OperationCanceledException)
        {
            StatusText = "";
            Error = "Скачивание отменено.";
        }
        catch (UpdateException ex)
        {
            _ctx.Services.Logger.Warn("Установка обновления не удалась: " + ex.Message);
            StatusText = "";
            Error = ex.Message;
        }
        catch (Win32Exception ex)
        {
            _ctx.Services.Logger.Warn("Не удалось запустить установщик.", ex);
            StatusText = "";
            Error = "Не удалось запустить установщик (возможно, его заблокировал антивирус). Скачайте его со страницы релиза кнопкой «Скачать самому».";
        }
        finally
        {
            _installing = false;
            _confirmedRunning = false;
            _installCts?.Dispose();
            _installCts = null;
            Refresh();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    // ----- текст релиза -----

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*", RegexOptions.Multiline)]
    private static partial Regex Headings();

    [GeneratedRegex(@"^\s*[-*]\s+", RegexOptions.Multiline)]
    private static partial Regex Bullets();

    private static string CleanNotes(string text)
    {
        var s = Headings().Replace(text, "");
        s = Bullets().Replace(s, "• ");
        return s.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
    }
}

/// <summary>Содержимое окна обновления: общее состояние лежит в <see cref="UpdateController"/>.</summary>
internal sealed class UpdateDialogViewModel : ObservableObject
{
    public UpdateDialogViewModel(UpdateController controller) => Controller = controller;
    public UpdateController Controller { get; }
}
