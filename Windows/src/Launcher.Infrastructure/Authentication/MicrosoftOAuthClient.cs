using Launcher.Core.Authentication;
using Launcher.Core.Errors;

namespace Launcher.Infrastructure.Authentication;

/// <summary>
/// OAuth 2.0 device code flow платформы Microsoft (тенант "consumers" — личные аккаунты Microsoft).
/// Client secret не нужен; пользователь вводит пароль только на странице Microsoft.
/// </summary>
public sealed class MicrosoftOAuthClient : IMicrosoftOAuthClient
{
    public static readonly Uri DefaultBaseUri = new("https://login.microsoftonline.com/consumers/oauth2/v2.0/");
    public const string Scope = "XboxLive.signin offline_access";
    public const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly TimeProvider _clock;

    public MicrosoftOAuthClient(HttpClient http, Uri? baseUri = null, TimeProvider? clock = null)
    {
        _http = http;
        _baseUri = baseUri ?? DefaultBaseUri;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync(string clientId, CancellationToken ct)
    {
        var (status, doc) = await AuthHttp.PostFormAsync(_http, new Uri(_baseUri, "devicecode"),
            [new("client_id", clientId), new("scope", Scope)], ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc?.RootElement ?? default;
            var deviceCode = AuthHttp.String(root, "device_code");
            var userCode = AuthHttp.String(root, "user_code");
            var uri = AuthHttp.String(root, "verification_uri");
            if ((int)status >= 400 || deviceCode is null || userCode is null || uri is null ||
                !Uri.TryCreate(uri, UriKind.Absolute, out var verification) || verification.Scheme != Uri.UriSchemeHttps)
                throw new AuthException(
                    $"Microsoft не выдала код для входа ({AuthHttp.Describe(doc)}). Проверьте client id: он должен принадлежать приложению Azure " +
                    "с включённым параметром «Allow public client flows».");

            return new DeviceCodeInfo(deviceCode, userCode, verification,
                AuthHttp.Int(root, "expires_in") ?? 900, AuthHttp.Int(root, "interval") ?? 5);
        }
    }

    public async Task<DevicePollResult> PollAsync(string clientId, DeviceCodeInfo code, CancellationToken ct)
    {
        var (_, doc) = await AuthHttp.PostFormAsync(_http, new Uri(_baseUri, "token"),
            [new("grant_type", DeviceGrantType), new("client_id", clientId), new("device_code", code.DeviceCode)], ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc?.RootElement ?? default;
            if (ReadTokens(root) is { } tokens) return new DevicePollResult(DevicePollStatus.Success, tokens);

            return AuthHttp.String(root, "error") switch
            {
                "authorization_pending" => new DevicePollResult(DevicePollStatus.Pending),
                "slow_down" => new DevicePollResult(DevicePollStatus.SlowDown),
                "authorization_declined" => new DevicePollResult(DevicePollStatus.Declined),
                "expired_token" or "code_expired" => new DevicePollResult(DevicePollStatus.Expired),
                _ => throw new AuthException($"Microsoft отклонила вход ({AuthHttp.Describe(doc)}).")
            };
        }
    }

    public async Task<OAuthTokens> RefreshAsync(string clientId, string refreshToken, CancellationToken ct)
    {
        var (_, doc) = await AuthHttp.PostFormAsync(_http, new Uri(_baseUri, "token"),
            [new("grant_type", "refresh_token"), new("client_id", clientId), new("refresh_token", refreshToken), new("scope", Scope)], ct)
            .ConfigureAwait(false);
        using (doc)
        {
            return ReadTokens(doc?.RootElement ?? default)
                ?? throw new AuthException(
                    $"Не удалось обновить вход ({AuthHttp.Describe(doc)}). Выполните вход заново: auth login");
        }
    }

    private OAuthTokens? ReadTokens(System.Text.Json.JsonElement root)
    {
        var access = AuthHttp.String(root, "access_token");
        var refresh = AuthHttp.String(root, "refresh_token");
        if (access is null || refresh is null) return null;
        return new OAuthTokens(access, refresh, _clock.GetUtcNow().AddSeconds(AuthHttp.Int(root, "expires_in") ?? 3600));
    }
}
