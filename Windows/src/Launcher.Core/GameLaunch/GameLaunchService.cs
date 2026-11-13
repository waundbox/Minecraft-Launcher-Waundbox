using Launcher.Core.Assets;
using Launcher.Core.Authentication;
using Launcher.Core.Configuration;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.JavaRuntime;
using Launcher.Core.Libraries;
using Launcher.Core.Logging;
using Launcher.Core.Profiles;
using Launcher.Core.Versions;

namespace Launcher.Core.GameLaunch;

/// <summary>
/// Подготавливает и запускает установленную версию Minecraft:
/// проверяет файлы и Java, собирает classpath и аргументы по формату версии, запускает процесс.
/// </summary>
public sealed class GameLaunchService
{
    public const string LauncherName = "Waundbox";

    private sealed record ClasspathEntry(string Path, long? Size, string Name);

    private readonly InstalledVersionRepository _installed;
    private readonly MinecraftLayout _layout;
    private readonly JavaResolver _javaResolver;
    private readonly ISettingsStore _settings;
    private readonly IGameProcessStarter _starter;
    private readonly PlatformInfo _platform;
    private readonly ILauncherLogger _logger;
    private readonly string _launcherVersion;
    private readonly IAuthlibInjectorProvider? _authlib;

    public GameLaunchService(
        InstalledVersionRepository installed,
        MinecraftLayout layout,
        JavaResolver javaResolver,
        ISettingsStore settings,
        IGameProcessStarter starter,
        PlatformInfo platform,
        ILauncherLogger? logger = null,
        string launcherVersion = "0.5",
        IAuthlibInjectorProvider? authlib = null)
    {
        _installed = installed;
        _layout = layout;
        _javaResolver = javaResolver;
        _settings = settings;
        _starter = starter;
        _platform = platform;
        _logger = logger ?? NullLauncherLogger.Instance;
        _launcherVersion = launcherVersion;
        _authlib = authlib;
    }

    /// <summary>Проверяет всё необходимое и собирает план запуска, ничего не запуская.</summary>
    /// <exception cref="LauncherException">Версия не установлена, не хватает файлов, нет подходящей Java и т.п. Сообщение понятно пользователю.</exception>
    public async Task<GameLaunchPlan> PrepareAsync(GameProfile profile, GameAccount account, CancellationToken ct)
    {
        profile.Validate();

        var details = await _installed.LoadAsync(profile.VersionId, ct).ConfigureAwait(false);
        var warnings = new List<string>();

        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var java = await _javaResolver.ResolveAsync(
            JavaRequirement.For(details), profile.JavaPath ?? settings.JavaPath, ct).ConfigureAwait(false);
        if (java.Warning is not null) warnings.Add(java.Warning);
        if (!java.Installation.Is64Bit && profile.MaxMemoryMb > 1536)
            warnings.Add($"Найденная Java 32-битная и не сможет использовать {profile.MaxMemoryMb} МБ памяти. Установите 64-битную Java.");

        // --- файлы версии
        var libraries = LibraryResolver.Resolve(details.Libraries, _platform);
        var classpath = BuildClasspath(details, libraries);
        var required = new List<ClasspathEntry>(classpath);
        foreach (var lib in libraries)
            if (lib.NativeArtifact?.Path is { } nativePath)
                required.Add(new ClasspathEntry(_layout.LibraryPath(nativePath), lib.NativeArtifact.Size, lib.Name + " (natives)"));
        EnsureFilesExist(details.Id, required);

        // Natives лежат в папке базовой версии Minecraft: именно там их распаковала установка.
        var nativesDir = _layout.NativesDirectory(details.ClientJarId);
        if (NativesPreparer.IsMissing(_layout, details.ClientJarId, libraries))
        {
            _logger.Info("Папка natives отсутствует, распаковываю заново.");
            NativesPreparer.Prepare(_layout, details.ClientJarId, libraries);
        }
        Directory.CreateDirectory(nativesDir);

        var gameDirectory = _layout.InstanceDirectory(profile.Name);
        Directory.CreateDirectory(gameDirectory);

        var assetsRoot = await PrepareAssetsAsync(details, gameDirectory, ct).ConfigureAwait(false);
        var loggingArgument = PrepareLoggingArgument(details);

        // --- аргументы
        var variables = BuildVariables(profile, account, details, classpath, nativesDir, gameDirectory, assetsRoot);
        // Цепочка наследования: сначала аргументы базовой версии, затем дополнения (Fabric/Forge).
        var jvmFromVersion = new List<string>();
        var gameFromVersion = new List<string>();
        // В JVM-аргументах Forge есть -DignoreList=...,${version_name}.jar: так загрузчик модулей пропускает jar клиента.
        // В classpath лежит jar базовой версии (1.20.1.jar), поэтому для JVM-аргументов version_name — идентификатор
        // этого jar; иначе jar попадает в модули и Forge падает с ResolutionException. В игровых аргументах (--version)
        // остаётся имя профиля версии.
        var jvmVariables = new Dictionary<string, string>(variables) { ["version_name"] = details.ClientJarId };
        foreach (var json in details.AllRawJson)
        {
            var parsed = LaunchArgumentsParser.Parse(json);
            var features = new HashSet<string>(StringComparer.Ordinal);
            var (jvm, _) = LaunchArgumentBuilder.Build(parsed, _platform, jvmVariables, features);
            var (_, game) = LaunchArgumentBuilder.Build(parsed, _platform, variables, features);
            jvmFromVersion.AddRange(jvm);
            gameFromVersion.AddRange(game);
        }

        var arguments = new List<string> { $"-Xmx{profile.MaxMemoryMb}M" };
        if (profile.MinMemoryMb is { } minMemory) arguments.Add($"-Xms{minMemory}M");
        var userJvmArguments = CommandLineSplitter.Split(profile.JvmArguments);
        AddGarbageCollector(arguments, profile, userJvmArguments, java.Installation.MajorVersion, warnings);
        if (account.AuthlibServer is { } authlibServer)
        {
            // Сторонний сервер авторизации (Ely.by): authlib-injector подменяет адреса Mojang на адреса этого сервера.
            if (_authlib is null)
                throw new LaunchException("Для входа через Ely.by нужен authlib-injector, но он не подключён.");
            var jar = await _authlib.GetJarPathAsync(ct).ConfigureAwait(false);
            arguments.Add($"-javaagent:{jar}={authlibServer}");
        }
        arguments.AddRange(userJvmArguments); // позже в списке — приоритетнее для JVM
        if (loggingArgument is not null) arguments.Add(loggingArgument);
        arguments.AddRange(jvmFromVersion);
        arguments.Add(details.MainClass);
        arguments.AddRange(gameFromVersion);
        arguments.AddRange(CommandLineSplitter.Split(profile.GameArguments));

        return new GameLaunchPlan(
            details.Id, profile.Name, java.Installation.Path, java.Installation.MajorVersion,
            arguments, gameDirectory, warnings, [account.AccessToken]);
    }

    private static void AddGarbageCollector(
        List<string> arguments, GameProfile profile, IReadOnlyList<string> userJvmArguments, int javaMajor, List<string> warnings)
    {
        var gc = GarbageCollectors.Find(profile.GarbageCollector);
        if (gc is null) return;

        if (GarbageCollectors.ContainsGcFlag(userJvmArguments))
            warnings.Add("В аргументах Java уже указан сборщик мусора, поэтому выбор в профиле пропущен.");
        else if (javaMajor < gc.MinJava)
            warnings.Add($"Сборщик {gc.Title} требует Java {gc.MinJava}+, а найдена Java {javaMajor}: используется стандартный.");
        else
            arguments.Add(gc.Flag);
    }

    /// <summary>Запускает процесс по плану. Вывод игры передаётся в <paramref name="onOutput"/>.</summary>
    public IGameProcess Launch(GameLaunchPlan plan, Action<GameOutputLine>? onOutput)
    {
        _logger.Info($"Запуск: {plan.ToDisplayCommandLine()}");
        var process = _starter.Start(plan, onOutput);
        _logger.Info($"Игра {plan.VersionId} запущена, PID {process.ProcessId}.");

        _ = process.Completion.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                _logger.Info($"Игра {plan.VersionId} (PID {process.ProcessId}) завершилась: код {t.Result.ExitCode}, остановлена лаунчером: {t.Result.Killed}.");
        }, TaskScheduler.Default);

        return process;
    }

    private List<ClasspathEntry> BuildClasspath(VersionDetails details, IReadOnlyList<ResolvedLibrary> libraries)
    {
        var entries = new List<ClasspathEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lib in libraries)
        {
            if (lib.Artifact?.Path is not { } relative) continue;
            var path = _layout.LibraryPath(relative);
            if (seen.Add(path)) entries.Add(new ClasspathEntry(path, lib.Artifact.Size, lib.Name));
        }

        var clientJar = _layout.ClientJarPath(details.ClientJarId);
        entries.Add(new ClasspathEntry(clientJar, details.ClientDownload.Size, $"клиент {details.ClientJarId}"));
        return entries;
    }

    private static void EnsureFilesExist(string versionId, IReadOnlyList<ClasspathEntry> entries)
    {
        var problems = new List<string>();
        foreach (var entry in entries)
        {
            var info = new FileInfo(entry.Path);
            if (!info.Exists) problems.Add(entry.Name);
            else if (entry.Size is { } size && info.Length != size) problems.Add($"{entry.Name} (повреждён)");
        }
        if (problems.Count > 0) throw MissingFiles(versionId, problems);
    }

    private async Task<string> PrepareAssetsAsync(VersionDetails details, string gameDirectory, CancellationToken ct)
    {
        var indexPath = _layout.AssetIndexPath(details.AssetIndex.Id);
        if (!File.Exists(indexPath)) throw MissingFiles(details.Id, [$"индекс ресурсов {details.AssetIndex.Id}"]);

        var index = AssetIndexParser.ParseIndex(await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false));

        var missing = index.Objects.Count(o => !File.Exists(_layout.AssetObjectPath(o.Hash)));
        if (missing > 0) throw MissingFiles(details.Id, [$"игровые ресурсы ({missing} шт.)"]);

        var prepared = await LegacyAssetsPreparer.PrepareAsync(
            _layout, details.AssetIndex.Id, index, gameDirectory, ct).ConfigureAwait(false);

        // Для «виртуальных» индексов игра ищет ресурсы по именам в разложенной папке.
        return index.IsVirtual && prepared is not null ? prepared : _layout.AssetsDirectory;
    }

    private string? PrepareLoggingArgument(VersionDetails details)
    {
        if (details.Logging is not { } logging) return null;
        var path = _layout.LogConfigPath(logging.FileId);
        if (!File.Exists(path)) throw MissingFiles(details.Id, [$"конфигурация логирования {logging.FileId}"]);
        return logging.Argument.Replace("${path}", path, StringComparison.Ordinal);
    }

    private Dictionary<string, string> BuildVariables(
        GameProfile profile, GameAccount account, VersionDetails details, IReadOnlyList<ClasspathEntry> classpath,
        string nativesDir, string gameDirectory, string assetsRoot) =>
        new(StringComparer.Ordinal)
        {
            ["auth_player_name"] = account.Username,
            ["auth_uuid"] = account.Uuid,
            ["auth_access_token"] = account.AccessToken,
            ["auth_session"] = $"token:{account.AccessToken}:{account.Uuid}",
            ["auth_xuid"] = "0",
            ["clientid"] = "0",
            ["user_type"] = account.UserType,
            ["user_properties"] = "{}",
            ["version_name"] = details.Id,
            ["version_type"] = details.Type ?? "release",
            ["profile_name"] = profile.Name,
            ["game_directory"] = gameDirectory,
            ["assets_root"] = assetsRoot,
            ["game_assets"] = assetsRoot,
            ["assets_index_name"] = details.AssetIndex.Id,
            ["natives_directory"] = nativesDir,
            ["library_directory"] = _layout.LibrariesDirectory,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["classpath"] = string.Join(Path.PathSeparator, classpath.Select(e => e.Path)),
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = _launcherVersion,
            ["resolution_width"] = "854",
            ["resolution_height"] = "480"
        };

    private static LaunchException MissingFiles(string versionId, IReadOnlyList<string> problems)
    {
        var shown = string.Join(", ", problems.Take(5)) + (problems.Count > 5 ? $" и ещё {problems.Count - 5}" : "");
        return new LaunchException(
            $"Не хватает файлов игры или они повреждены: {shown}. Выполните: install {versionId} — недостающее будет докачано. " +
            "Для Fabric или Forge выполните: fabric install / forge install <версия Minecraft>.");
    }
}
