using System.IO.Compression;
using System.Text;
using Launcher.Core.Configuration;
using Launcher.Core.Downloads;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.JavaRuntime;
using Launcher.Core.Logging;
using Launcher.Core.Versions;

namespace Launcher.Core.Forge;

public sealed record ForgeInstallResult(string VersionId, string GameVersion, string ForgeVersion);

/// <summary>
/// Установка Forge (1.13 и новее) официальным установщиком в режиме "--installClient":
/// игра → установщик с maven.minecraftforge.net (проверка SHA-1, если репозиторий её публикует) → запуск установщика
/// нужной Java → проверка, что профиль версии появился и читается.
/// Установщик сам скачивает библиотеки и выполняет обработку (processors), поэтому установка может занять несколько минут.
/// </summary>
public sealed class ForgeInstaller
{
    public static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(20);

    private readonly IForgeMetaClient _meta;
    private readonly IMinecraftInstaller _vanilla;
    private readonly IFileDownloader _downloader;
    private readonly IProcessRunner _processes;
    private readonly JavaResolver _javaResolver;
    private readonly ISettingsStore _settings;
    private readonly InstalledVersionRepository _installed;
    private readonly MinecraftLayout _layout;
    private readonly string _installersDirectory;
    private readonly ILauncherLogger _logger;

    public ForgeInstaller(
        IForgeMetaClient meta, IMinecraftInstaller vanilla, IFileDownloader downloader, IProcessRunner processes,
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

    /// <param name="forgeVersion">Версия Forge или null — рекомендованная (если нет — последняя).</param>
    /// <param name="status">Короткие сообщения о ходе установки.</param>
    public async Task<ForgeInstallResult> InstallAsync(
        string gameVersion, string? forgeVersion, InstallOptions options, IProgress<string>? status, CancellationToken ct)
    {
        var promos = await _meta.GetPromotionsAsync(ct).ConfigureAwait(false);
        var forge = ForgeVersions.Choose(promos, gameVersion, forgeVersion);
        var installerUri = ForgeVersions.InstallerUri(gameVersion, forge);
        _logger.Info($"Установка Forge {forge} для Minecraft {gameVersion}.");

        status?.Report($"Проверяю игру {gameVersion}...");
        await _vanilla.InstallAsync(gameVersion, options, null, ct).ConfigureAwait(false);
        var details = await _installed.LoadAsync(gameVersion, ct).ConfigureAwait(false);

        var sha1 = await _meta.GetInstallerSha1Async(gameVersion, forge, ct).ConfigureAwait(false);
        if (sha1 is null)
            _logger.Warn("Репозиторий Forge не опубликовал SHA-1 установщика: проверка целостности пропущена.");

        Directory.CreateDirectory(_installersDirectory);
        var installerPath = Path.Combine(_installersDirectory, ForgeVersions.InstallerFileName(gameVersion, forge));
        status?.Report($"Скачиваю установщик Forge {forge}...");
        try
        {
            await _downloader.DownloadAsync(new DownloadRequest(installerUri, installerPath, sha1), null, ct).ConfigureAwait(false);
        }
        catch (DownloadException ex)
        {
            throw new ForgeException(
                $"Не удалось скачать установщик Forge {forge} для Minecraft {gameVersion}. " +
                "Возможно, такой сборки нет или сервер недоступен.", ex);
        }

        var versionId = ReadVersionId(installerPath, gameVersion);

        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var java = await _javaResolver.ResolveAsync(JavaRequirement.For(details), settings.JavaPath, ct).ConfigureAwait(false);

        // Установщик требует файл launcher_profiles.json в папке игры (так устроен официальный лаунчер).
        var profilesFile = Path.Combine(_layout.Root, "launcher_profiles.json");
        if (!File.Exists(profilesFile))
            await File.WriteAllTextAsync(profilesFile, """{ "profiles": {}, "version": 3 }""", ct).ConfigureAwait(false);

        status?.Report("Запускаю установщик Forge (это может занять несколько минут)...");
        var result = await _processes.RunAsync(
            java.Installation.Path, ["-jar", installerPath, "--installClient", _layout.Root], InstallerTimeout, ct).ConfigureAwait(false);

        if (result.TimedOut)
            throw new ForgeException($"Установщик Forge не завершился за {InstallerTimeout.TotalMinutes:F0} минут и был остановлен.");
        if (result.ExitCode != 0)
            throw new ForgeException(
                $"Установщик Forge завершился с ошибкой (код {result.ExitCode}). Последние строки вывода:\n{Tail(result)}");

        if (!File.Exists(_layout.VersionJsonPath(versionId)))
            throw new ForgeException(
                $"Установщик Forge завершился без ошибки, но версия {versionId} не появилась. Последние строки вывода:\n{Tail(result)}");

        await _installed.LoadAsync(versionId, ct).ConfigureAwait(false); // проверка: профиль читается и цепочка наследования цела
        _logger.Info($"Forge {versionId} установлен.");
        return new ForgeInstallResult(versionId, gameVersion, forge);
    }

    /// <summary>Идентификатор версии берётся из version.json внутри установщика — так он совпадёт с тем, что создаст сам установщик.</summary>
    private static string ReadVersionId(string installerPath, string gameVersion)
    {
        try
        {
            using var zip = ZipFile.OpenRead(installerPath);
            var entry = zip.GetEntry("version.json")
                ?? throw new ForgeException(
                    "Этот установщик Forge старого формата (Minecraft до 1.13). Такие версии пока не поддерживаются.");
            if (entry.Length > 5 * 1024 * 1024)
                throw new ForgeException("Установщик Forge содержит слишком большой version.json.");

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var overlay = VersionDetailsParser.ParseOverlay(reader.ReadToEnd());
            if (!string.Equals(overlay.InheritsFrom, gameVersion, StringComparison.Ordinal))
                throw new ForgeException(
                    $"Установщик Forge рассчитан на Minecraft \"{overlay.InheritsFrom}\", а запрошена \"{gameVersion}\".");
            if (string.Equals(overlay.Id, gameVersion, StringComparison.Ordinal))
                throw new ForgeException("Установщик Forge имеет тот же идентификатор версии, что и игра.");
            return overlay.Id;
        }
        catch (InvalidDataException ex)
        {
            throw new ForgeException("Скачанный установщик Forge повреждён (это не JAR-архив).", ex);
        }
    }

    private static string Tail(ProcessResult result)
    {
        var lines = (result.StdErr + "\n" + result.StdOut)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("\n", lines.TakeLast(15).Select(l => "  " + l));
    }
}
