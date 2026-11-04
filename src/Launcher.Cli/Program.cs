using Launcher.Cli.Commands;
using Launcher.Core.Errors;
using Launcher.Core.Logging;

namespace Launcher.Cli;

/// <summary>Точка входа временного консольного интерфейса.</summary>
public static class Program
{
    private const string Usage = """
        waundbox — консольный интерфейс лаунчера (этап 6: вход Microsoft)

        Команды:
          versions list [--type release|snapshot|beta|alpha|all] [--limit N] [--refresh]
              Показать список версий Minecraft (по умолчанию: 20 последних релизов).

          install <версия> [--refresh] [--concurrency N]
              Скачать и установить версию: клиент, библиотеки, ресурсы, natives.
              Повторный запуск проверяет файлы и докачивает только повреждённые или отсутствующие.
              Пример: install 1.21.1

          java detect [--for <версия>]
              Найти установленные Java; с --for показать, какая будет использована для установленной версии.
          java set <путь>      Указать Java вручную (java.exe или папка установки).
          java clear           Вернуться к автоматическому поиску Java.

          profiles list | show <имя> | delete <имя>
          profiles create <имя> --version <версия> [--memory 4G] [--min-memory 1G] [--jvm-args "..."] [--game-args "..."] [--java <путь>]
          profiles edit <имя> [те же параметры]
              Игровые профили: версия, память, дополнительные аргументы, своя Java.

          fabric loaders <версия Minecraft>
              Показать доступные версии загрузчика Fabric (официальный сервис meta.fabricmc.net).
          fabric install <версия Minecraft> [--loader <версия>] [--concurrency N]
              Установить Fabric (последний стабильный загрузчик, если --loader не указан). Игра ставится автоматически.
              Затем: profiles create <имя> --version fabric-loader-<загрузчик>-<версия Minecraft>
              Пример: fabric install 1.21.1

          forge versions <версия Minecraft>
              Показать рекомендованную и последнюю сборки Forge (официальный список files.minecraftforge.net).
          forge install <версия Minecraft> [--forge <версия>]
              Установить Forge официальным установщиком (Minecraft 1.13 и новее); игра ставится автоматически.
              Установка может занять несколько минут. Пример: forge install 1.21.1

          auth setup <client-id>   Указать client id своего приложения Azure (один раз; см. README, «Авторизация»).
          auth login               Войти в аккаунт Microsoft (код + страница Microsoft; пароль лаунчеру не нужен).
          auth status              Показать, выполнен ли вход.
          auth logout              Выйти: удалить сохранённые токены.

          launch <профиль> [--dev] [--username <ник>] [--dry-run] [--quiet]
              Запустить игру. Если выполнен вход (auth login) — с вашим аккаунтом Microsoft; иначе в development-режиме.
              --dev (или --username) — принудительно development-режим без проверки лицензии.
              --dry-run — только показать команду запуска, --quiet — не выводить логи игры в консоль.

        Ctrl+C — отмена. Данные и журнал: %APPDATA%\.waundbox (можно сменить переменной WAUNDBOX_HOME).
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        switch (args[0])
        {
            case "versions" when args.Length >= 2 && args[1] == "list":
            {
                var options = VersionsListOptions.TryParse(args.Skip(2).ToList(), out var error);
                if (options is null) return UsageError(error);
                return await RunGuardedAsync(
                    (services, ct) => new VersionsListCommand(services.Manifest, Console.Out).RunAsync(options, ct),
                    cts.Token);
            }
            case "install":
            {
                var options = InstallCliOptions.TryParse(args.Skip(1).ToList(), out var error);
                if (options is null) return UsageError(error);
                return await RunGuardedAsync(
                    (services, ct) => new InstallCommand(services.Installer, Console.Out, !Console.IsOutputRedirected).RunAsync(options, ct),
                    cts.Token);
            }
            case "java":
            {
                var options = JavaCliOptions.TryParse(args.Skip(1).ToList(), out var error);
                if (options is null) return UsageError(error);
                return await RunGuardedAsync(
                    (s, ct) => new JavaCommand(s.Settings, s.JavaDetector, s.JavaProbe, s.JavaResolver, s.InstalledVersions, Console.Out)
                        .RunAsync(options, ct),
                    cts.Token);
            }
            case "fabric":
            {
                var options = FabricCliOptions.TryParse(args.Skip(1).ToList(), out var error);
                if (options is null) return UsageError(error);
                return await RunGuardedAsync(
                    (s, ct) => new FabricCommand(s.FabricMeta, s.FabricInstaller, Console.Out).RunAsync(options, ct),
                    cts.Token);
            }
            case "forge":
            {
                var options = ForgeCliOptions.TryParse(args.Skip(1).ToList(), out var error);
                if (options is null) return UsageError(error);
                return await RunGuardedAsync(
                    (s, ct) => new ForgeCommand(s.ForgeMeta, s.ForgeInstaller, Console.Out).RunAsync(options, ct),
                    cts.Token);
            }
            case "auth":
            {
                var options = AuthCliOptions.TryParse(args.Skip(1).ToList(), out var error);
                if (options is null) return UsageError(error);
                return await RunGuardedAsync(
                    (s, ct) => new AuthCommand(s.Accounts, s.Settings, Console.Out).RunAsync(options, ct),
                    cts.Token);
            }
            case "profiles":
            {
                var options = ProfilesCliOptions.TryParse(args.Skip(1).ToList(), out var error);
                if (options is null) return UsageError(error);
                return await RunGuardedAsync(
                    (s, ct) => new ProfilesCommand(s.Profiles, s.Layout, Console.Out).RunAsync(options, ct),
                    cts.Token);
            }
            case "launch":
            {
                var options = LaunchCliOptions.TryParse(args.Skip(1).ToList(), out var error);
                if (options is null) return UsageError(error);
                return await RunGuardedAsync(
                    (s, ct) => new LaunchCommand(s.Profiles, s.LaunchService, s.Accounts, s.GameLogPath, Console.Out).RunAsync(options, ct),
                    cts.Token);
            }
            default:
                return UsageError($"Неизвестная команда \"{string.Join(' ', args)}\".");
        }
    }

    private static int UsageError(string? error)
    {
        Console.Error.WriteLine($"Ошибка: {error}");
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);
        return 1;
    }

    /// <summary>Выполняет команду и превращает исключения в понятные сообщения и коды возврата.</summary>
    private static async Task<int> RunGuardedAsync(Func<CliServices, CancellationToken, Task<int>> run, CancellationToken ct)
    {
        using var services = CliServices.Create();
        try
        {
            return await run(services, ct);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Операция отменена. Уже скачанные файлы сохранены, при повторном запуске они не скачиваются заново.");
            return 130;
        }
        catch (LauncherException ex)
        {
            services.Logger.Error("Команда завершилась ошибкой.", ex);
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            services.Logger.Error("Непредвиденная ошибка.", ex);
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Непредвиденная ошибка: {ex.Message}");
            Console.Error.WriteLine("Подробности записаны в журнал launcher.log.");
            return 3;
        }
    }
}
