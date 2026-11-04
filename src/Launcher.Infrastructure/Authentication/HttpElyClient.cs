using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Launcher.Core.Authentication;
using Launcher.Core.Errors;

namespace Launcher.Infrastructure.Authentication;

/// <summary>Клиент сервера авторизации Ely.by (https://docs.ely.by/en/minecraft-auth.html). Пароли и токены в сообщения об ошибках не попадают.</summary>
public sealed class HttpElyClient : IElyAuthClient
{
    public static readonly Uri DefaultBaseAddress = new("https://authserver.ely.by/");
    internal const string TwoFactorMessage = "Account protected with two factor auth.";

    private readonly HttpClient _http;

    private readonly Uri _baseAddress;

    /// <param name="baseAddress">Адрес сервера; по умолчанию Ely.by. Общий HttpClient не изменяется.</param>
    public HttpElyClient(HttpClient http, Uri? baseAddress = null)
    {
        _http = http;
        _baseAddress = baseAddress ?? DefaultBaseAddress;
    }

    public async Task<ElySession> AuthenticateAsync(string usernameOrEmail, string passwordWithCode, string clientToken, CancellationToken ct)
    {
        using var response = await PostAsync("auth/authenticate", new
        {
            username = usernameOrEmail,
            password = passwordWithCode,
            clientToken,
            requestUser = false
        }, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw await MapErrorAsync(response, refresh: false, ct).ConfigureAwait(false);
        return await ReadSessionAsync(response, clientToken, ct).ConfigureAwait(false);
    }

    public async Task<ElySession> RefreshAsync(string accessToken, string clientToken, CancellationToken ct)
    {
        using var response = await PostAsync("auth/refresh", new { accessToken, clientToken }, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await MapErrorAsync(response, refresh: true, ct).ConfigureAwait(false);
        return await ReadSessionAsync(response, clientToken, ct).ConfigureAwait(false);
    }

    public async Task<bool> ValidateAsync(string accessToken, CancellationToken ct)
    {
        using var response = await PostAsync("auth/validate", new { accessToken }, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return true;
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false;
        throw Unavailable(response.StatusCode);
    }

    public async Task InvalidateAsync(string accessToken, string clientToken, CancellationToken ct)
    {
        using var response = await PostAsync("auth/invalidate", new { accessToken, clientToken }, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return;
        throw Unavailable(response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync<T>(string path, T body, CancellationToken ct)
    {
        try
        {
            return await _http.PostAsJsonAsync(new Uri(_baseAddress, path), body, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AuthException("Не удалось связаться с сервером Ely.by. Проверьте интернет.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AuthException("Сервер Ely.by не ответил вовремя.", ex);
        }
    }

    private static async Task<ElySession> ReadSessionAsync(HttpResponseMessage response, string fallbackClientToken, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var access = root.GetProperty("accessToken").GetString();
            var client = root.TryGetProperty("clientToken", out var c) && c.GetString() is { Length: > 0 } ct2 ? ct2 : fallbackClientToken;
            var profileEl = root.TryGetProperty("selectedProfile", out var sp) && sp.ValueKind == JsonValueKind.Object ? sp : default;
            if (profileEl.ValueKind != JsonValueKind.Object) throw new AuthException("На аккаунте Ely.by нет игрового профиля.");
            var id = profileEl.GetProperty("id").GetString();
            var name = profileEl.GetProperty("name").GetString();
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                throw new AuthException("Ely.by вернул неполный ответ.");
            return new ElySession(access, client, new ElyProfile(id.Replace("-", ""), name));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AuthException("Ely.by вернул неожиданный ответ.", ex);
        }
    }

    private static async Task<AuthException> MapErrorAsync(HttpResponseMessage response, bool refresh, CancellationToken ct)
    {
        string? message = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("errorMessage", out var m)) message = m.GetString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            if (refresh) return new ElyTokenRejectedException();
            if (message is not null && message.Contains("two factor", StringComparison.OrdinalIgnoreCase))
                return new ElyTwoFactorRequiredException();
            if (message is not null && message.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase))
                return new AuthException("Неверный логин, пароль или код двухфакторной защиты.");
            return new AuthException(string.IsNullOrWhiteSpace(message) ? "Ely.by отклонил вход." : $"Ely.by: {message}");
        }
        return Unavailable(response.StatusCode);
    }

    private static AuthException Unavailable(HttpStatusCode code) =>
        new($"Сервер Ely.by сейчас недоступен (код {(int)code}). Попробуйте позже.");
}
