using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Launcher.Core.Errors;
using Launcher.Core.Modpacks;

namespace Launcher.Infrastructure.Mods;

/// <summary>Клиент API CurseForge (https://docs.curseforge.com/rest-api/): ключ передаётся заголовком x-api-key и нигде не записывается в журнал.</summary>
public sealed class HttpCurseForgeClient : ICurseForgeClient
{
    public static readonly Uri DefaultBaseUri = new("https://api.curseforge.com/");

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string?>> _apiKey;
    private readonly Uri _baseUri;

    public HttpCurseForgeClient(HttpClient http, Func<CancellationToken, Task<string?>> apiKey, Uri? baseUri = null)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUri = baseUri ?? DefaultBaseUri;
    }

    public async Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> fileIds, CancellationToken ct)
    {
        var key = (await _apiKey(ct).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new ModException("Для сборок CurseForge нужен бесплатный ключ API: получите его на console.curseforge.com и вставьте в Настройках.");
        if (key.Any(c => c < 0x21 || c > 0x7E))
            throw new ModException("Ключ CurseForge API записан неверно (лишние символы). Вставьте его заново в Настройках.");
        if (fileIds.Count == 0) return [];

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "v1/mods/files"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { fileIds }), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
            };
            request.Headers.TryAddWithoutValidation("x-api-key", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ModException("CurseForge отклонил ключ API. Проверьте ключ в Настройках (console.curseforge.com).");
            response.EnsureSuccessStatusCode();
            return CurseForgeParser.ParseFiles(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new ModException("Не удалось связаться с CurseForge (api.curseforge.com): проверьте подключение к интернету.", ex);
        }
    }
}
