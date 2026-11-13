using Launcher.Core.Downloads;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Libraries;
using Launcher.Core.Logging;
using Launcher.Core.Versions;

namespace Launcher.Core.Quilt;

public sealed record QuiltInstallResult(
    string VersionId, string GameVersion, string LoaderVersion, int FilesTotal, int FilesDownloaded);

/// <summary>
/// Установка Quilt: базовая версия Minecraft → профиль запуска Quilt с официального сервиса → библиотеки загрузчика.
/// Профиль записывается последним: если загрузка оборвалась, «наполовину установленной» версии не появляется.
/// </summary>
public sealed class QuiltInstaller
{
    private readonly IQuiltMetaClient _meta;
    private readonly IMinecraftInstaller _vanilla;
    private readonly IFileDownloader _downloader;
    private readonly MinecraftLayout _layout;
    private readonly PlatformInfo _platform;
    private readonly ILauncherLogger _logger;

    public QuiltInstaller(
        IQuiltMetaClient meta, IMinecraftInstaller vanilla, IFileDownloader downloader,
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
    public async Task<QuiltInstallResult> InstallAsync(
        string gameVersion, string? loaderVersion, InstallOptions options,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var loaders = await _meta.GetLoaderVersionsAsync(gameVersion, ct).ConfigureAwait(false);
        if (loaders.Count == 0)
            throw new QuiltException($"Quilt не поддерживает версию Minecraft \"{gameVersion}\" (или такой версии нет).");

        var loader = ChooseLoader(loaders, loaderVersion);
        _logger.Info($"Установка Quilt Loader {loader.Version} для Minecraft {gameVersion}.");

        await _vanilla.InstallAsync(gameVersion, options, progress, ct).ConfigureAwait(false);

        var json = await _meta.GetProfileJsonAsync(gameVersion, loader.Version, ct).ConfigureAwait(false);
        var overlay = VersionDetailsParser.ParseOverlay(json);
        if (!string.Equals(overlay.InheritsFrom, gameVersion, StringComparison.Ordinal))
            throw new QuiltException(
                $"Профиль Quilt ссылается на версию \"{overlay.InheritsFrom}\", а запрошена \"{gameVersion}\". Установка остановлена.");
        if (string.Equals(overlay.Id, gameVersion, StringComparison.Ordinal))
            throw new QuiltException("Профиль Quilt имеет тот же идентификатор, что и версия игры. Установка остановлена.");

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
        _logger.Info($"Quilt {overlay.Id} установлен: скачано {batch.Downloaded}, уже были в порядке {batch.AlreadyValid}.");
        return new QuiltInstallResult(overlay.Id, gameVersion, loader.Version, requests.Count, batch.Downloaded);
    }

    /// <summary>Самая новая версия загрузчика (числа сравниваются как числа, не как текст).</summary>
    public static QuiltLoaderVersion Newest(IReadOnlyList<QuiltLoaderVersion> loaders)
    {
        var best = loaders[0];
        foreach (var l in loaders.Skip(1))
            if (Compare(l.Version, best.Version) > 0) best = l;
        return best;
    }

    /// <summary>Сравнение «0.26.0» / «0.20.0-beta.9»: сначала числа до дефиса, при равенстве предварительная сборка старше релиза.</summary>
    public static int Compare(string a, string b)
    {
        var (na, pa) = Split(a);
        var (nb, pb) = Split(b);
        var c = na.CompareTo(nb);
        if (c != 0) return c;
        if (pa is null && pb is null) return 0;
        if (pa is null) return 1;
        if (pb is null) return -1;
        return Compare(pa.Replace("beta.", "").Replace("alpha.", "").Replace("pre.", ""), pb.Replace("beta.", "").Replace("alpha.", "").Replace("pre.", ""));
    }

    private static (Version Number, string? Pre) Split(string v)
    {
        var dash = v.IndexOf('-');
        var core = dash < 0 ? v : v[..dash];
        var pre = dash < 0 ? null : v[(dash + 1)..];
        while (core.Count(ch => ch == '.') < 1) core += ".0";
        return (Version.TryParse(core, out var n) ? n : new Version(0, 0), pre);
    }

    private static QuiltLoaderVersion ChooseLoader(IReadOnlyList<QuiltLoaderVersion> loaders, string? requested)
    {
        if (requested is null)
        {
            // Порядок в ответе сервиса не гарантирован — выбираем самую новую стабильную по номеру (иначе самую новую вообще).
            var stable = loaders.Where(l => l.Stable).ToList();
            return Newest(stable.Count > 0 ? stable : loaders);
        }

        var found = loaders.FirstOrDefault(l => string.Equals(l.Version, requested, StringComparison.Ordinal));
        if (found is not null) return found;

        var sample = string.Join(", ", loaders.Take(5).Select(l => l.Version));
        throw new QuiltException($"Загрузчик Quilt \"{requested}\" недоступен для этой версии Minecraft. Доступны, например: {sample}.");
    }
}
