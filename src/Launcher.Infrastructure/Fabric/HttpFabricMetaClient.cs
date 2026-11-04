using Launcher.Core.Errors;
using Launcher.Core.Fabric;

namespace Launcher.Infrastructure.Fabric;

/// <summary>Клиент официального сервиса https://meta.fabricmc.net (API v2).</summary>
public sealed class HttpFabricMetaClient : IFabricMetaClient
{
    public static readonly Uri DefaultBaseUri = new("https://meta.fabricmc.net/");

    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public HttpFabricMetaClient(HttpClient http, Uri? baseUri = null)
    {
        _http = http;
        _baseUri = baseUri ?? DefaultBaseUri;
    }

    public async Task<IReadOnlyList<FabricLoaderVersion>> GetLoaderVersionsAsync(string gameVersion, CancellationToken ct)
    {
        var json = await GetAsync($"v2/versions/loader/{Uri.EscapeDataString(gameVersion)}", ct).ConfigureAwait(false);
        return FabricMetaParser.ParseLoaders(json);
    }

    public Task<string> GetProfileJsonAsync(string gameVersion, string loaderVersion, CancellationToken ct) =>
        GetAsync($"v2/versions/loader/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json", ct);

    private async Task<string> GetAsync(string relative, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(_baseUri, relative), ct).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
            {
                // Для неизвестной версии сервис отвечает ошибкой клиента: для списка это «нет версий», для профиля — ошибка.
                if (relative.EndsWith("/profile/json", StringComparison.Ordinal))
                    throw new FabricException("Fabric не имеет профиля для этой пары версий Minecraft и загрузчика.");
                return "[]";
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new FabricException("Не удалось связаться с сервисом Fabric (meta.fabricmc.net): проверьте подключение к интернету.", ex);
        }
    }
}
