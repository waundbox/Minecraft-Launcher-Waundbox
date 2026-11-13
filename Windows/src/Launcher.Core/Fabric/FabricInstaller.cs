using Launcher.Core.Downloads;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Libraries;
using Launcher.Core.Logging;
using Launcher.Core.Versions;

namespace Launcher.Core.Fabric;

public sealed record FabricInstallResult(
    string VersionId, string GameVersion, string LoaderVersion, int FilesTotal, int FilesDownloaded);

/// <summary>
/// Установка Fabric: базовая версия Minecraft → профиль запуска Fabric с официального сервиса → библиотеки загрузчика.
/// Профиль записывается последним: если загрузка оборвалась, «наполовину установленной» версии не появляется.
/// </summary>
public sealed class FabricInstaller
{
    private readonly IFabricMetaClient _meta;
    private readonly IMinecraftInstaller _vanilla;
    private readonly IFileDownloader _downloader;
    private readonly MinecraftLayout _layout;
    private readonly PlatformInfo _platform;
    private readonly ILauncherLogger _logger;

    public FabricInstaller(
        IFabricMetaClient meta, IMinecraftInstaller vanilla, IFileDownloader downloader,
        MinecraftLayout layout, PlatformInfo platform, ILauncherLogger? logger = null)
    {
        _meta = meta;
        _vanilla = vanilla;
        _downloader = downloader;
        _layout = layout;
        _platform = platform;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    /// <param name="loaderVersion">Версия загрузчика или null — последняя стабильная.</param>
    public async Task<FabricInstallResult> InstallAsync(
        string gameVersion, string? loaderVersion, InstallOptions options,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var loaders = await _meta.GetLoaderVersionsAsync(gameVersion, ct).ConfigureAwait(false);
        if (loaders.Count == 0)
            throw new FabricException($"Fabric не поддерживает версию Minecraft \"{gameVersion}\" (или такой версии нет).");

        var loader = ChooseLoader(loaders, loaderVersion);
        _logger.Info($"Установка Fabric Loader {loader.Version} для Minecraft {gameVersion}.");

        await _vanilla.InstallAsync(gameVersion, options, progress, ct).ConfigureAwait(false);

        var json = await _meta.GetProfileJsonAsync(gameVersion, loader.Version, ct).ConfigureAwait(false);
        var overlay = VersionDetailsParser.ParseOverlay(json);
        if (!string.Equals(overlay.InheritsFrom, gameVersion, StringComparison.Ordinal))
            throw new FabricException(
                $"Профиль Fabric ссылается на версию \"{overlay.InheritsFrom}\", а запрошена \"{gameVersion}\". Установка остановлена.");
        if (string.Equals(overlay.Id, gameVersion, StringComparison.Ordinal))
            throw new FabricException("Профиль Fabric имеет тот же идентификатор, что и версия игры. Установка остановлена.");

        var jsonPath = _layout.VersionJsonPath(overlay.Id); // проверяет, что путь не выходит за пределы папки игры

        var requests = new Dictionary<string, DownloadRequest>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in LibraryResolver.Resolve(overlay.Libraries, _platform))
        {
            if (lib.Artifact is not { Path: { } path, Url: { } url } artifact) continue;
            requests.TryAdd(_layout.LibraryPath(path), new DownloadRequest(url, _layout.LibraryPath(path), artifact.Sha1, artifact.Size));
        }

        progress?.Report(new InstallProgress(InstallStage.Files, 0, requests.Count));
        var runner = new DownloadBatchRunner(_downloader, options.MaxConcurrency);
        var batchProgress = progress is null ? null : new InlineProgress<BatchProgress>(p =>
            progress.Report(new InstallProgress(InstallStage.Files, p.FilesDone, p.FilesTotal, p.BytesDone, p.BytesTotal)));
        var batch = await runner.RunAsync(requests.Values.ToList(), batchProgress, ct).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        var temp = jsonPath + ".part";
        await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
        File.Move(temp, jsonPath, overwrite: true);

        progress?.Report(new InstallProgress(InstallStage.Completed, requests.Count, requests.Count));
        _logger.Info($"Fabric {overlay.Id} установлен: скачано {batch.Downloaded}, уже были в порядке {batch.AlreadyValid}.");
        return new FabricInstallResult(overlay.Id, gameVersion, loader.Version, requests.Count, batch.Downloaded);
    }

    private static FabricLoaderVersion ChooseLoader(IReadOnlyList<FabricLoaderVersion> loaders, string? requested)
    {
        if (requested is null)
            return loaders.FirstOrDefault(l => l.Stable) ?? loaders[0];

        var found = loaders.FirstOrDefault(l => string.Equals(l.Version, requested, StringComparison.Ordinal));
        if (found is not null) return found;

        var sample = string.Join(", ", loaders.Take(5).Select(l => l.Version));
        throw new FabricException($"Загрузчик Fabric \"{requested}\" недоступен для этой версии Minecraft. Доступны, например: {sample}.");
    }
}
