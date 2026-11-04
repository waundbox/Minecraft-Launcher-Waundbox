using System.Net;
using System.Text;
using System.Text.Json;
using Launcher.Core.Errors;

namespace Launcher.Infrastructure.Authentication;

/// <summary>Общие помощники HTTP для клиентов авторизации. Тела запросов и ответов с токенами никогда не попадают в сообщения об ошибках.</summary>
internal static class AuthHttp
{
    public static async Task<(HttpStatusCode Status, JsonDocument? Json)> PostFormAsync(
        HttpClient http, Uri uri, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        return await SendAsync(http, () => new HttpRequestMessage(HttpMethod.Post, uri) { Content = content }, ct).ConfigureAwait(false);
    }

    public static async Task<(HttpStatusCode Status, JsonDocument? Json)> PostJsonAsync(
        HttpClient http, Uri uri, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        return await SendAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json");
            return request;
        }, ct).ConfigureAwait(false);
    }

    public static async Task<(HttpStatusCode Status, JsonDocument? Json)> GetBearerAsync(
        HttpClient http, Uri uri, string bearerToken, CancellationToken ct) =>
        await SendAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            return request;
        }, ct).ConfigureAwait(false);

    private static async Task<(HttpStatusCode, JsonDocument?)> SendAsync(
        HttpClient http, Func<HttpRequestMessage> create, CancellationToken ct)
    {
        try
        {
            using var request = create();
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonDocument? doc = null;
            try { doc = string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text); }
            catch (JsonException) { /* не JSON — вызывающий код обработает по коду состояния */ }
            return (response.StatusCode, doc);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new AuthException("Не удалось связаться с серверами Microsoft/Xbox/Minecraft: проверьте подключение к интернету.", ex);
        }
    }

    public static string? String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static int? Int(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n : null;

    /// <summary>Описание ошибки для пользователя: только код и описание от сервера, без тел запросов.</summary>
    public static string Describe(JsonDocument? doc)
    {
        if (doc is null) return "сервер вернул неожиданный ответ";
        var code = String(doc.RootElement, "error");
        var description = String(doc.RootElement, "error_description");
        if (description is { Length: > 300 }) description = description[..300];
        return string.Join(": ", new[] { code, description?.Split('\n')[0].Trim() }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } text
            ? text : "сервер вернул неожиданный ответ";
    }
}
