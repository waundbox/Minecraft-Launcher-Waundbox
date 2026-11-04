using Launcher.Core.Errors;
using Launcher.Core.Quilt;

namespace Launcher.Infrastructure.Quilt;

/// <summary>Клиент официального сервиса https://meta.quiltmc.org (API v3).</summary>
public sealed class HttpQuiltMetaClient : IQuiltMetaClient
{
    public static readonly Uri DefaultBaseUri = new("https://meta.quiltmc.org/");

    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public HttpQuiltMetaClient(HttpClient http, Uri? baseUri = null)
    {
        _http = http;
        _baseUri = baseUri ?? DefaultBaseUri;
    }

    public async Task<IReadOnlyList<QuiltLoaderVersion>> GetLoaderVersionsAsync(string gameVersion, CancellationToken ct)
    {
        var json = await GetAsync($"v3/versions/loader/{Uri.EscapeDataString(gameVersion)}", ct).ConfigureAwait(false);
        return QuiltMetaParser.ParseLoaders(json);
    }

    public Task<string> GetProfileJsonAsync(string gameVersion, string loaderVersion, CancellationToken ct) =>
        GetAsync($"v3/versions/loader/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json", ct);

    private async Task<string> GetAsync(string relative, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(_baseUri, relative), ct).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
            {
                // Для неизвестной версии сервис отвечает ошибкой клиента: для списка это «нет версий», для профиля — ошибка.
                if (relative.EndsWith("/profile/json", StringComparison.Ordinal))
                    throw new QuiltException("Quilt не имеет профиля для этой пары версий Minecraft и загрузчика.");
                return "[]";
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new QuiltException("Не удалось связаться с сервисом Quilt (meta.quiltmc.org): проверьте подключение к интернету.", ex);
        }
    }
}
