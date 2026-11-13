using Launcher.Core.Authentication;
using Launcher.Core.Errors;
using Launcher.Core.GameLaunch;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>
/// Запуск и остановка игр. Можно играть в несколько профилей одновременно: у каждого профиля своё состояние
/// (<see cref="ProfileItem.State"/>), своя кнопка «Играть/Стоп» и свой сеанс консоли. Один и тот же профиль
/// второй раз запустить нельзя (у него общая папка игры).
/// Подготовка к запуску (проверка файлов, natives, ресурсы) идёт по одному профилю за раз, чтобы два запуска
/// не писали в одни и те же общие файлы; сами игры после старта работают независимо.
/// </summary>
internal sealed class LaunchController : ObservableObject
{
    private readonly UiContext _ctx;
    private readonly Dictionary<string, IGameProcess> _processes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _prepareGate = new(1, 1);
    private int _runningCount;
    private bool _devNoticeShown;

    public LaunchController(UiContext ctx)
    {
        _ctx = ctx;
        // Не AsyncCommand: он не даёт повторного запуска, пока команда выполняется, а игра идёт часами.
        PlayCommand = new RelayCommand(p => Play(p), p => Resolve(p) is { IsIdle: true });
        StopCommand = new RelayCommand(p => Stop(Resolve(p)), p => Resolve(p) is { IsPlaying: true });
    }

    /// <summary>Запускает профиль из параметра (ProfileItem) или выбранный.</summary>
    public RelayCommand PlayCommand { get; }

    /// <summary>Останавливает игру профиля из параметра (ProfileItem) или выбранного.</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>Игра закрылась с ошибкой (не по просьбе пользователя): имя профиля.</summary>
    public event Action<string>? Crashed;

    /// <summary>Сколько игр сейчас работает.</summary>
    public int RunningCount => _runningCount;

    /// <summary>Работает хотя бы одна игра.</summary>
    public bool IsRunning => _runningCount > 0;

    private ProfileItem? Resolve(object? parameter) => parameter as ProfileItem ?? _ctx.Catalog.Selected;

    private static void Refresh() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    private void SetRunningCount(int value)
    {
        if (_runningCount == value) return;
        _runningCount = value;
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(IsRunning));
    }

    private void Play(object? parameter)
    {
        var item = Resolve(parameter);
        if (item is null)
        {
            _ctx.Toasts.Show("Сначала создайте профиль.", ToastKind.Warning);
            return;
        }
        if (!item.IsIdle) return;

        if (!ReferenceEquals(_ctx.Catalog.Selected, item)) _ctx.Catalog.Selected = item;
        _ = RunGuardedAsync(item);
    }

    private async Task RunGuardedAsync(ProfileItem item)
    {
        try
        {
            await RunAsync(item);
        }
        catch (Exception ex)
        {
            // не должно случаться: ожидаемые ошибки обработаны в RunAsync
            AsyncCommand.UnhandledError?.Invoke(ex);
        }
    }

    private async Task RunAsync(ProfileItem item)
    {
        item.State = LaunchState.Preparing;
        item.StatusText = "Проверяю файлы игры…";
        Refresh();

        var console = _ctx.Console;
        ConsoleSession? session = null;
        var counted = false;

        try
        {
            if (_prepareGate.CurrentCount == 0) item.StatusText = "Жду, пока подготовится другая игра…";
            IGameProcess process;
            await _prepareGate.WaitAsync();
            try
            {
                item.StatusText = "Проверяю файлы игры…";
                var account = await ChooseAccountAsync();

                // Свой скин подключается только в локальном режиме: у аккаунтов Microsoft и Ely.by скин берётся с их серверов.
                var skin = await _ctx.Services.Skins.ApplyAsync(
                    item.Name, item.Label.GameVersion, account.IsDevelopment, CancellationToken.None);
                if (skin == Core.Skins.SkinApplyResult.Unsupported)
                    _ctx.Toasts.Show("Свой скин работает только в версиях 1.13 и новее: в этом профиле он не подключён.", ToastKind.Warning);
                var plan = await _ctx.Services.Launch.PrepareAsync(item.Profile, account, CancellationToken.None);

                session = console.BeginSession(item.Name, _ctx.Services.Paths.GameLogFile(item.Name));
                console.WriteLauncher(session, $"Профиль: {plan.ProfileName}, версия {plan.VersionId}");
                console.WriteLauncher(session, $"Игрок: {account.Username}{AccountSuffix(account)}");
                console.WriteLauncher(session, $"Java {plan.JavaMajorVersion}: {plan.JavaPath}");
                console.WriteLauncher(session, $"Папка игры: {plan.WorkingDirectory}");
                foreach (var warning in plan.Warnings) console.WriteLauncher(session, "Внимание: " + warning);

                item.StatusText = "Запускаю игру…";
                var captured = session;
                process = _ctx.Services.Launch.Launch(plan, line => console.Write(captured, line));
            }
            finally
            {
                _prepareGate.Release();
            }

            _processes[item.Name] = process;
            item.State = LaunchState.Running;
            item.StatusText = "Игра запущена";
            SetRunningCount(_runningCount + 1);
            counted = true;
            Refresh();
            console.WriteLauncher(session!, $"Игра запущена (PID {process.ProcessId}).");
            _ctx.Toasts.Show($"Игра запущена: {item.Name}", ToastKind.Success);

            var exit = await process.Completion;
            if (exit.Killed)
            {
                console.WriteLauncher(session!, "Игра остановлена лаунчером.");
                _ctx.Toasts.Show($"Игра остановлена: {item.Name}", ToastKind.Info);
            }
            else if (exit.ExitCode == 0)
            {
                console.WriteLauncher(session!, "Игра закрыта (код 0).");
                _ctx.Toasts.Show($"Игра закрыта: {item.Name}", ToastKind.Info);
            }
            else
            {
                console.WriteLauncher(session!, $"Игра завершилась с ошибкой: код {exit.ExitCode}.", isError: true);
                _ctx.Toasts.Show($"{item.Name}: игра завершилась с ошибкой (код {exit.ExitCode}). Подробности — на вкладке «Логи».", ToastKind.Error);
                Crashed?.Invoke(item.Name);
            }
        }
        catch (LauncherException ex)
        {
            _ctx.Services.Logger.Warn("Запуск не удался: " + ex.Message);
            session ??= console.BeginSession(item.Name, _ctx.Services.Paths.GameLogFile(item.Name));
            console.WriteLauncher(session, ex.Message, isError: true);
            _ctx.Toasts.Show(ex.Message, ToastKind.Error);
            Crashed?.Invoke(item.Name);
        }
        finally
        {
            _processes.Remove(item.Name);
            if (session is not null) console.EndSession(session);
            if (counted) SetRunningCount(_runningCount - 1);
            item.StatusText = "";
            item.State = LaunchState.Idle;
            Refresh();
        }
    }

    private static string AccountSuffix(GameAccount account) =>
        account.IsDevelopment ? " (локальный режим)" : account.AuthlibServer is not null ? " (Ely.by)" : "";

    private async Task<GameAccount> ChooseAccountAsync()
    {
        var preferred = _ctx.Settings.Current.PreferredAccount;
        var microsoft = await _ctx.Services.Accounts.GetStatusAsync(CancellationToken.None);
        var ely = await _ctx.Services.Ely.GetStatusAsync(CancellationToken.None);

        switch (AccountChoice.Resolve(preferred, microsoft.SignedIn, ely.SignedIn))
        {
            case AccountKind.Microsoft:
                if (await _ctx.Services.Accounts.GetAccountAsync(CancellationToken.None) is { } signedIn) return signedIn;
                break;
            case AccountKind.ElyBy:
                if (await _ctx.Services.Ely.GetGameAccountAsync(CancellationToken.None) is { } elyAccount) return elyAccount;
                break;
        }

        var name = string.IsNullOrWhiteSpace(_ctx.Settings.Current.DevUsername)
            ? DevelopmentAccount.DefaultUsername
            : _ctx.Settings.Current.DevUsername!;
        var account = DevelopmentAccount.Create(name);

        if (!_devNoticeShown && preferred != AccountChoice.DevelopmentId)
        {
            _devNoticeShown = true;
            _ctx.Toasts.Show(
                "Вход в Microsoft или Ely.by не выполнен: игра запускается в локальном режиме. На серверы с проверкой лицензии зайти не получится.",
                ToastKind.Warning);
        }
        return account;
    }

    private void Stop(ProfileItem? item)
    {
        if (item is null || !_processes.TryGetValue(item.Name, out var process)) return;
        item.StatusText = "Останавливаю игру…";
        process.Kill();
    }
}
