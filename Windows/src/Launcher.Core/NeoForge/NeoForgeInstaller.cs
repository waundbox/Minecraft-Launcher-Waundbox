using System.IO.Compression;
using System.Text;
using Launcher.Core.Configuration;
using Launcher.Core.Downloads;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.JavaRuntime;
using Launcher.Core.Logging;
using Launcher.Core.Versions;

namespace Launcher.Core.NeoForge;

public sealed record NeoForgeInstallResult(string VersionId, string GameVersion, string NeoForgeVersion);

/// <summary>
/// Установка NeoForge (Minecraft 1.20.2 и новее; для 1.20.1 — отдельная сборка net.neoforged:forge) официальным установщиком в режиме «--install-client»:
/// игра → установщик с maven.neoforged.net (проверка SHA-1, если репозиторий её публикует) → запуск установщика
/// нужной Java → проверка, что профиль версии появился и читается.
/// Установщик сам скачивает библиотеки и выполняет обработку (processors), поэтому установка может занять несколько минут.
/// </summary>
public sealed class NeoForgeInstaller
{
    public static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(20);

    private readonly INeoForgeMetaClient _meta;
    private readonly IMinecraftInstaller _vanilla;
    private readonly IFileDownloader _downloader;
    private readonly IProcessRunner _processes;
    private readonly JavaResolver _javaResolver;
    private readonly ISettingsStore _settings;
    private readonly InstalledVersionRepository _installed;
    private readonly MinecraftLayout _layout;
    private readonly string _installersDirectory;
    private readonly ILauncherLogger _logger;

    public NeoForgeInstaller(
        INeoForgeMetaClient meta, IMinecraftInstaller vanilla, IFileDownloader downloader, IProcessRunner processes,
        JavaResolver javaResolver, ISettingsStore settings, InstalledVersionRepository installed,
        MinecraftLayout layout, string installersDirectory, ILauncherLogger? logger = null)
    {
        _meta = meta;
        _vanilla = vanilla;
        _downloader = downloader;
        _processes = processes;
        _javaResolver = javaResolver;
        _settings = settings;
        _installed = installed;
        _layout = layout;
        _installersDirectory = installersDirectory;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    /// <param name="neoForgeVersion">Версия NeoForge или null — последняя стабильная (если нет — последняя).</param>
    /// <param name="status">Короткие сообщения о ходе установки.</param>
    public async Task<NeoForgeInstallResult> InstallAsync(
        string gameVersion, string? neoForgeVersion, InstallOptions options, IProgress<string>? status, CancellationToken ct)
    {
        var all = await _meta.GetVersionsAsync(ct).ConfigureAwait(false);
        var neo = NeoForgeVersions.Choose(all, gameVersion, neoForgeVersion);
        var installerUri = NeoForgeVersions.InstallerUri(neo);
        _logger.Info($"Установка NeoForge {neo} для Minecraft {gameVersion}.");

        status?.Report($"Проверяю игру {gameVersion}...");
        await _vanilla.InstallAsync(gameVersion, options, null, ct).ConfigureAwait(false);
        var details = await _installed.LoadAsync(gameVersion, ct).ConfigureAwait(false);

        var sha1 = await _meta.GetInstallerSha1Async(neo, ct).ConfigureAwait(false);
        if (sha1 is null)
            _logger.Warn("Репозиторий NeoForge не опубликовал SHA-1 установщика: проверка целостности пропущена.");

        Directory.CreateDirectory(_installersDirectory);
        var installerPath = Path.Combine(_installersDirectory, NeoForgeVersions.InstallerFileName(neo));
        status?.Report($"Скачиваю установщик NeoForge {neo}...");
        try
        {
            await _downloader.DownloadAsync(new DownloadRequest(installerUri, installerPath, sha1), null, ct).ConfigureAwait(false);
        }
        catch (DownloadException ex)
        {
            throw new NeoForgeException(
                $"Не удалось скачать установщик NeoForge {neo} для Minecraft {gameVersion}. " +
                "Возможно, такой сборки нет или сервер недоступен.", ex);
        }

        var versionId = ReadVersionId(installerPath, gameVersion);

        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var java = await _javaResolver.ResolveAsync(JavaRequirement.For(details), settings.JavaPath, ct).ConfigureAwait(false);

        // Установщик ищет launcher_profiles.json в папке игры (так устроен официальный лаунчер).
        var profilesFile = Path.Combine(_layout.Root, "launcher_profiles.json");
        if (!File.Exists(profilesFile))
            await File.WriteAllTextAsync(profilesFile, """{ "profiles": {}, "version": 3 }""", ct).ConfigureAwait(false);

        status?.Report("Запускаю установщик NeoForge (это может занять несколько минут)...");
        var result = await _processes.RunAsync(
            java.Installation.Path, ["-jar", installerPath, "--install-client", _layout.Root], InstallerTimeout, ct).ConfigureAwait(false);

        // Установщик для 1.20.1 собран по старой схеме Forge: если он не знает "--install-client", пробуем "--installClient".
        // (Какой из ключей принимает именно эта сборка, проверить заранее не удалось.)
        if (NeoForgeVersions.IsLegacy(neo) && !result.TimedOut && result.ExitCode != 0)
        {
            _logger.Warn("Установщик NeoForge для 1.20.1 завершился с ошибкой; пробую запустить с ключом --installClient.");
            result = await _processes.RunAsync(
                java.Installation.Path, ["-jar", installerPath, "--installClient", _layout.Root], InstallerTimeout, ct).ConfigureAwait(false);
        }

        if (result.TimedOut)
            throw new NeoForgeException($"Установщик NeoForge не завершился за {InstallerTimeout.TotalMinutes:F0} минут и был остановлен.");
        if (result.ExitCode != 0)
            throw new NeoForgeException(
                $"Установщик NeoForge завершился с ошибкой (код {result.ExitCode}). Последние строки вывода:\n{Tail(result)}");

        if (!File.Exists(_layout.VersionJsonPath(versionId)))
            throw new NeoForgeException(
                $"Установщик NeoForge завершился без ошибки, но версия {versionId} не появилась. Последние строки вывода:\n{Tail(result)}");

        await _installed.LoadAsync(versionId, ct).ConfigureAwait(false); // проверка: профиль читается и цепочка наследования цела
        _logger.Info($"NeoForge {versionId} установлен.");
        return new NeoForgeInstallResult(versionId, gameVersion, neo);
    }

    /// <summary>Идентификатор версии берётся из version.json внутри установщика — так он совпадёт с тем, что создаст сам установщик.</summary>
    private static string ReadVersionId(string installerPath, string gameVersion)
    {
        try
        {
            using var zip = ZipFile.OpenRead(installerPath);
            var entry = zip.GetEntry("version.json")
                ?? throw new NeoForgeException("В установщике NeoForge нет version.json: формат установщика не распознан.");
            if (entry.Length > 5 * 1024 * 1024)
                throw new NeoForgeException("Установщик NeoForge содержит слишком большой version.json.");

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var overlay = VersionDetailsParser.ParseOverlay(reader.ReadToEnd());
            if (!string.Equals(overlay.InheritsFrom, gameVersion, StringComparison.Ordinal))
                throw new NeoForgeException(
                    $"Установщик NeoForge рассчитан на Minecraft \"{overlay.InheritsFrom}\", а запрошена \"{gameVersion}\".");
            if (string.Equals(overlay.Id, gameVersion, StringComparison.Ordinal))
                throw new NeoForgeException("Установщик NeoForge имеет тот же идентификатор версии, что и игра.");
            return overlay.Id;
        }
        catch (InvalidDataException ex)
        {
            throw new NeoForgeException("Скачанный установщик NeoForge повреждён (это не JAR-архив).", ex);
        }
    }

    private static string Tail(ProcessResult result)
    {
        var lines = (result.StdErr + "\n" + result.StdOut)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("\n", lines.TakeLast(15).Select(l => "  " + l));
    }
}
