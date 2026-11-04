using System.Net;
using Launcher.Core.Errors;
using Launcher.Core.Forge;

namespace Launcher.Infrastructure.Forge;

/// <summary>Клиент официальных источников Forge: files.minecraftforge.net и maven.minecraftforge.net.</summary>
public sealed class HttpForgeMetaClient : IForgeMetaClient
{
    public static readonly Uri DefaultPromotionsUri =
        new("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json");

    private readonly HttpClient _http;
    private readonly Uri _promotionsUri;
    private readonly Uri _mavenBase;

    public HttpForgeMetaClient(HttpClient http, Uri? promotionsUri = null, Uri? mavenBase = null)
    {
        _http = http;
        _promotionsUri = promotionsUri ?? DefaultPromotionsUri;
        _mavenBase = mavenBase ?? ForgeVersions.MavenBase;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPromotionsAsync(CancellationToken ct)
    {
        var (_, body) = await GetAsync(_promotionsUri, ct).ConfigureAwait(false);
        return ForgeVersions.ParsePromotions(body ?? "");
    }

    public async Task<string?> GetInstallerSha1Async(string gameVersion, string forgeVersion, CancellationToken ct)
    {
        var installer = ForgeVersions.InstallerUri(gameVersion, forgeVersion); // проверяет безопасность версий
        var relative = installer.AbsoluteUri[ForgeVersions.MavenBase.AbsoluteUri.Length..] + ".sha1";
        var (status, body) = await GetAsync(new Uri(_mavenBase, relative), ct, allowNotFound: true).ConfigureAwait(false);
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
            throw new ForgeException($"Не удалось связаться с источником Forge ({uri.Host}): проверьте подключение к интернету.", ex);
        }
    }
}
