using Launcher.Core.Assets;
using Launcher.Core.Downloads;
using Launcher.Core.Errors;
using Launcher.Core.Libraries;
using Launcher.Core.Logging;
using Launcher.Core.Versions;

namespace Launcher.Core.Installation;

/// <summary>
/// Установка версии: JSON версии → индекс ресурсов → клиентский JAR, библиотеки, ресурсы → распаковка natives.
/// Повторный запуск проверяет все файлы и докачивает только отсутствующие или повреждённые.
/// </summary>
public sealed class MinecraftInstaller : IMinecraftInstaller, ILocalVersionInstaller
{
    /// <summary>
    /// Официальное хранилище игровых ресурсов Mojang. Метаданные содержат только хеши ресурсов,
    /// а адрес файла строится как {база}/{первые 2 символа хеша}/{хеш}.
    /// </summary>
    public static readonly Uri DefaultAssetBaseUri = new("https://resources.download.minecraft.net/");

    private readonly IVersionManifestProvider _manifest;
    private readonly IFileDownloader _downloader;
    private readonly MinecraftLayout _layout;
    private readonly PlatformInfo _platform;
    private readonly ILauncherLogger _logger;
    private readonly Uri _assetBaseUri;

    public MinecraftInstaller(
        IVersionManifestProvider manifest,
        IFileDownloader downloader,
        MinecraftLayout layout,
        PlatformInfo platform,
        ILauncherLogger? logger = null,
        Uri? assetBaseUri = null)
    {
        _manifest = manifest;
        _downloader = downloader;
        _layout = layout;
        _platform = platform;
        _logger = logger ?? NullLauncherLogger.Instance;
        _assetBaseUri = assetBaseUri ?? DefaultAssetBaseUri;
    }

    public async Task<InstallResult> InstallAsync(
        string versionId, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress(InstallStage.VersionList));
        var manifestResult = await _manifest.GetManifestAsync(options.RefreshManifest, ct).ConfigureAwait(false);
        var entry = manifestResult.Manifest.Find(versionId) ?? throw NotFound(versionId, manifestResult.Manifest);

        _logger.Info($"Установка версии {entry.Id}.");

        progress?.Report(new InstallProgress(InstallStage.VersionMetadata));
        var jsonPath = _layout.VersionJsonPath(entry.Id);
        await _downloader.DownloadAsync(new DownloadRequest(entry.Url, jsonPath, entry.Sha1), null, ct).ConfigureAwait(false);
        var details = VersionDetailsParser.Parse(await File.ReadAllTextAsync(jsonPath, ct).ConfigureAwait(false));

        return await InstallDetailsAsync(entry.Id, jsonPath, details, skipClient: false, options, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ставит версию по готовому JSON (не из списка Mojang): библиотеки, ресурсы, natives. Клиентский jar не скачивается —
    /// его кладёт вызывающий (своя версия из jar-файла).
    /// </summary>
    public async Task<InstallResult> InstallLocalAsync(
        string versionId, string versionJson, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var jsonPath = _layout.VersionJsonPath(versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(jsonPath, versionJson, ct).ConfigureAwait(false);
        var details = VersionDetailsParser.Parse(versionJson);
        _logger.Info($"Установка своей версии {versionId} по готовому JSON.");
        return await InstallDetailsAsync(versionId, jsonPath, details, skipClient: true, options, progress, ct).ConfigureAwait(false);
    }

    private async Task<InstallResult> InstallDetailsAsync(
        string versionId, string jsonPath, VersionDetails details, bool skipClient,
        InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress(InstallStage.AssetIndex));
        var indexPath = _layout.AssetIndexPath(details.AssetIndex.Id);
        await _downloader.DownloadAsync(
            new DownloadRequest(details.AssetIndex.Url, indexPath, details.AssetIndex.Sha1, details.AssetIndex.Size),
            null, ct).ConfigureAwait(false);
        var assets = AssetIndexParser.Parse(await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false));

        var resolved = LibraryResolver.Resolve(details.Libraries, _platform);
        var requests = BuildRequests(versionId, details, resolved, assets, skipClient);

        progress?.Report(new InstallProgress(InstallStage.Files, 0, requests.Count));
        var runner = new DownloadBatchRunner(_downloader, options.MaxConcurrency);
        var batchProgress = progress is null ? null : new InlineProgress<BatchProgress>(p =>
            progress.Report(new InstallProgress(InstallStage.Files, p.FilesDone, p.FilesTotal, p.BytesDone, p.BytesTotal)));
        var batch = await runner.RunAsync(requests, batchProgress, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress(InstallStage.Natives));
        var nativesDir = _layout.NativesDirectory(versionId);
        NativesPreparer.Prepare(_layout, versionId, resolved);

        progress?.Report(new InstallProgress(InstallStage.Completed, requests.Count, requests.Count));
        _logger.Info($"Версия {versionId} установлена: скачано {batch.Downloaded}, уже были в порядке {batch.AlreadyValid}.");

        return new InstallResult(
            versionId, jsonPath, _layout.ClientJarPath(versionId), nativesDir,
            requests.Count, batch.Downloaded, batch.AlreadyValid, batch.BytesDownloaded);
    }

    private List<DownloadRequest> BuildRequests(
        string versionId, VersionDetails details, IReadOnlyList<ResolvedLibrary> libraries, IReadOnlyList<AssetObject> assets, bool skipClient)
    {
        // Ключ — путь назначения: одинаковые файлы (например, ресурсы с одним хешем) качаются один раз.
        var requests = new Dictionary<string, DownloadRequest>(StringComparer.OrdinalIgnoreCase);
        void Add(DownloadRequest r) => requests.TryAdd(r.DestinationPath, r);

        if (!skipClient)
        {
            var client = details.ClientDownload;
            Add(new DownloadRequest(client.Url ?? throw new VersionMetadataException("У клиента нет адреса скачивания."),
                _layout.ClientJarPath(versionId), client.Sha1, client.Size));
        }

        foreach (var lib in libraries)
        {
            if (lib.Artifact is not null) Add(ForLibrary(lib.Name, lib.Artifact));
            if (lib.NativeArtifact is not null) Add(ForLibrary(lib.Name, lib.NativeArtifact));
        }

        foreach (var asset in assets)
        {
            var url = new Uri(_assetBaseUri, $"{asset.Hash[..2]}/{asset.Hash}");
            Add(new DownloadRequest(url, _layout.AssetObjectPath(asset.Hash), asset.Hash, asset.Size));
        }

        if (details.Logging is { } logging)
            Add(new DownloadRequest(logging.Url, _layout.LogConfigPath(logging.FileId), logging.Sha1, logging.Size));

        return requests.Values.ToList();
    }

    private DownloadRequest ForLibrary(string name, DownloadInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Path))
            throw new VersionMetadataException($"Библиотека {name}: в метаданных не указан путь файла.");
        if (info.Url is null)
            throw new VersionMetadataException($"Библиотека {name}: в метаданных не указан адрес скачивания.");
        return new DownloadRequest(info.Url, _layout.LibraryPath(info.Path), info.Sha1, info.Size);
    }

    private static VersionNotFoundException NotFound(string versionId, VersionManifest manifest)
    {
        var similar = manifest.Versions
            .Where(v => v.Id.Contains(versionId, StringComparison.OrdinalIgnoreCase))
            .Take(5).Select(v => v.Id).ToList();
        var hint = similar.Count > 0 ? $" Похожие версии: {string.Join(", ", similar)}." : " Список версий: команда \"versions list\".";
        return new VersionNotFoundException($"Версия \"{versionId}\" не найдена в официальном списке Mojang.{hint}");
    }
}
