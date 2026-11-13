using Launcher.Core.Errors;
using Launcher.Core.Logging;
using Launcher.Core.Versions;

namespace Launcher.Infrastructure.Versions;

/// <summary>
/// Получает официальный манифест версий Mojang и кэширует его на диске.
/// Порядок: свежий кэш → сеть → устаревший кэш (если сети нет) → ошибка.
/// </summary>
public sealed class VersionManifestProvider : IVersionManifestProvider
{
    /// <summary>Официальный адрес манифеста версий.</summary>
    public static readonly Uri DefaultManifestUri =
        new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    private readonly HttpClient _http;
    private readonly string _cacheFile;
    private readonly TimeSpan _cacheTtl;
    private readonly ILauncherLogger _logger;
    private readonly Uri _uri;

    public VersionManifestProvider(
        HttpClient http,
        string cacheFile,
        TimeSpan cacheTtl,
        ILauncherLogger? logger = null,
        Uri? manifestUri = null)
    {
        _http = http;
        _cacheFile = cacheFile;
        _cacheTtl = cacheTtl;
        _logger = logger ?? NullLauncherLogger.Instance;
        _uri = manifestUri ?? DefaultManifestUri;
    }

    public async Task<ManifestResult> GetManifestAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var cached = TryReadCache();

        if (!forceRefresh && cached is not null && DateTimeOffset.UtcNow - cached.Value.WrittenAt < _cacheTtl)
        {
            _logger.Info("Манифест версий взят из свежего кэша.");
            return new ManifestResult(cached.Value.Manifest, ManifestSource.FreshCache, cached.Value.WrittenAt);
        }

        try
        {
            using var response = await _http.GetAsync(_uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var manifest = VersionManifestParser.Parse(json); // проверяем ДО записи в кэш
            await TryWriteCacheAsync(json, cancellationToken).ConfigureAwait(false);

            _logger.Info($"Манифест версий загружен с сервера ({manifest.Versions.Count} версий).");
            return new ManifestResult(manifest, ManifestSource.Network, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                   ex is HttpRequestException or TaskCanceledException or ManifestFormatException)
        {
            if (cached is not null)
            {
                _logger.Warn("Сервер манифеста недоступен или вернул неверные данные, используется устаревший кэш.", ex);
                return new ManifestResult(cached.Value.Manifest, ManifestSource.StaleCache, cached.Value.WrittenAt);
            }

            _logger.Error("Не удалось получить манифест версий и локальной копии нет.", ex);
            throw new ManifestUnavailableException(
                "Не удалось получить список версий Minecraft: проверьте подключение к интернету. " +
                "Локальной копии списка пока нет.", ex);
        }
    }

    private (VersionManifest Manifest, DateTimeOffset WrittenAt)? TryReadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return null;
            var manifest = VersionManifestParser.Parse(File.ReadAllText(_cacheFile));
            return (manifest, new DateTimeOffset(File.GetLastWriteTimeUtc(_cacheFile), TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ManifestFormatException)
        {
            _logger.Warn("Кэш манифеста повреждён или недоступен, он будет проигнорирован.", ex);
            return null;
        }
    }

    private async Task TryWriteCacheAsync(string json, CancellationToken ct)
    {
        var tmp = _cacheFile + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cacheFile))!);
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _cacheFile, overwrite: true); // безопасная замена: файл не бывает «наполовину записан»
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn("Не удалось сохранить кэш манифеста (диск полон или нет прав). Работаем без кэша.", ex);
            try { File.Delete(tmp); } catch (Exception) { /* уже не важно */ }
        }
    }
}
