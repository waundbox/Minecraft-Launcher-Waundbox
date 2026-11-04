using Launcher.Core.Authentication;
using Launcher.Core.Errors;
using Launcher.Core.GameLaunch;
using Launcher.Core.Profiles;

namespace Launcher.Cli.Commands;

/// <summary>Команда "launch": запускает игру по профилю и показывает её вывод.</summary>
public sealed class LaunchCommand
{
    private readonly IProfileStore _profiles;
    private readonly GameLaunchService _service;
    private readonly AccountService _accounts;
    private readonly Func<string, string> _gameLogPath;
    private readonly TextWriter _out;

    public LaunchCommand(IProfileStore profiles, GameLaunchService service, AccountService accounts, Func<string, string> gameLogPath, TextWriter output)
    {
        _profiles = profiles;
        _service = service;
        _accounts = accounts;
        _gameLogPath = gameLogPath;
        _out = output;
    }

    public async Task<int> RunAsync(LaunchCliOptions options, CancellationToken ct)
    {
        var profile = await _profiles.FindAsync(options.ProfileName, ct)
            ?? throw new ProfileException($"Профиль \"{options.ProfileName}\" не найден. Список профилей: profiles list");

        var account = await ChooseAccountAsync(options, ct);

        var plan = await _service.PrepareAsync(profile, account, ct);

        _out.WriteLine($"Профиль:  {plan.ProfileName}");
        _out.WriteLine($"Версия:   {plan.VersionId}");
        _out.WriteLine($"Игрок:    {account.Username}{(account.IsDevelopment ? " (development)" : " (Microsoft)")}");
        _out.WriteLine($"Java:     {plan.JavaMajorVersion} ({plan.JavaPath})");
        _out.WriteLine($"Память:   до {profile.MaxMemoryMb} МБ");
        _out.WriteLine($"Папка:    {plan.WorkingDirectory}");
        foreach (var warning in plan.Warnings)
            _out.WriteLine($"Внимание: {warning}");

        if (options.DryRun)
        {
            _out.WriteLine();
            _out.WriteLine("Режим --dry-run: игра не запускается. Команда запуска (секреты скрыты):");
            _out.WriteLine(plan.ToDisplayCommandLine());
            return 0;
        }

        var logPath = _gameLogPath(profile.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await using var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
        var outputLock = new object();

        void OnOutput(GameOutputLine line)
        {
            lock (outputLock)
            {
                logWriter.WriteLine(line.IsError ? "[ERR] " + line.Text : line.Text);
                if (options.Quiet) return;
                (line.IsError ? Console.Error : _out).WriteLine(line.Text);
            }
        }

        var process = _service.Launch(plan, OnOutput);
        _out.WriteLine();
        _out.WriteLine($"Игра запущена (PID {process.ProcessId}). Остановить: Ctrl+C. Журнал игры: {logPath}");

        GameExitResult exit;
        try
        {
            exit = await process.Completion.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            _out.WriteLine("Останавливаю игру...");
            process.Kill();
            await process.Completion;
            return 130;
        }

        _out.WriteLine();
        if (exit.IsSuccess)
        {
            _out.WriteLine("Игра закрыта (код 0).");
            return 0;
        }

        _out.WriteLine($"Игра завершилась с ошибкой: код {exit.ExitCode}. Подробности — в журнале игры: {logPath}");
        return exit.ExitCode == 0 ? 1 : exit.ExitCode;
    }

    /// <summary>Официальный аккаунт, если выполнен вход; иначе (или с --dev/--username) — development-режим с предупреждением.</summary>
    private async Task<GameAccount> ChooseAccountAsync(LaunchCliOptions options, CancellationToken ct)
    {
        if (!options.Dev && await _accounts.GetAccountAsync(ct) is { } signedIn)
            return signedIn;

        var account = DevelopmentAccount.Create(options.Username ?? DevelopmentAccount.DefaultUsername);
        _out.WriteLine("ВНИМАНИЕ: development-режим. Это не официальная авторизация Microsoft: игра запускается без проверки лицензии,");
        _out.WriteLine("на серверы с проверкой лицензии зайти не получится." +
                       (options.Dev ? "" : " Чтобы войти в аккаунт, выполните: auth login"));
        _out.WriteLine();
        return account;
    }
}
