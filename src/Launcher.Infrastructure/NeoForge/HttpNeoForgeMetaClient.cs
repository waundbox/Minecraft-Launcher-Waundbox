using System.Net;
using Launcher.Core.Errors;
using Launcher.Core.NeoForge;

namespace Launcher.Infrastructure.NeoForge;

/// <summary>Клиент Maven-репозитория NeoForge (maven.neoforged.net).</summary>
public sealed class HttpNeoForgeMetaClient : INeoForgeMetaClient
{
    private readonly HttpClient _http;
    private readonly Uri _metadataUri;
    private readonly Uri _legacyMetadataUri;

    public HttpNeoForgeMetaClient(HttpClient http, Uri? metadataUri = null, Uri? legacyMetadataUri = null)
    {
        _http = http;
        _metadataUri = metadataUri ?? NeoForgeVersions.MetadataUri;
        _legacyMetadataUri = legacyMetadataUri ?? NeoForgeVersions.LegacyMetadataUri;
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken ct)
    {
        var (_, body) = await GetAsync(_metadataUri, ct).ConfigureAwait(false);
        var modern = NeoForgeVersions.ParseMetadata(body ?? "");

        // Сборки для 1.20.1 лежат в отдельном артефакте. Если его список недоступен, остальные версии всё равно работают.
        try
        {
            var (status, legacyBody) = await GetAsync(_legacyMetadataUri, ct, allowNotFound: true).ConfigureAwait(false);
            if (status == HttpStatusCode.NotFound || legacyBody is null) return modern;
            var legacy = NeoForgeVersions.ParseMetadata(legacyBody).Where(NeoForgeVersions.IsLegacy);
            return legacy.Concat(modern).ToList();
        }
        catch (NeoForgeException) when (!ct.IsCancellationRequested)
        {
            return modern;
        }
    }

    public async Task<string?> GetInstallerSha1Async(string neoForgeVersion, CancellationToken ct)
    {
        var uri = new Uri(NeoForgeVersions.InstallerUri(neoForgeVersion).AbsoluteUri + ".sha1"); // проверяет безопасность версии
        var (status, body) = await GetAsync(uri, ct, allowNotFound: true).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound || body is null) return null;

        // Файл .sha1 содержит 40 hex-символов (иногда с именем файла после пробела).
        var token = body.Trim().Split(' ', '\t', '\n', '\r')[0];
        return token.Length == 40 && token.All(Uri.IsHexDigit) ? token.ToLowerInvariant() : null;
    }

    private async Task<(HttpStatusCode Status, string? Body)> GetAsync(Uri uri, CancellationToken ct, bool allowNotFound = false)
    {
        try
        {
            using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return (response.StatusCode, null);
            response.EnsureSuccessStatusCode();
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new NeoForgeException($"Не удалось связаться с источником NeoForge ({uri.Host}): проверьте подключение к интернету.", ex);
        }
    }
}
