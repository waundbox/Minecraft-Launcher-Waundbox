using System.Text.Json;
using Launcher.Core.Authentication;
using Launcher.Core.Errors;

namespace Launcher.Infrastructure.Authentication;

/// <summary>Xbox Live (user.auth.xboxlive.com) и XSTS (xsts.auth.xboxlive.com) для сервисов Minecraft.</summary>
public sealed class XboxClient : IXboxClient
{
    public static readonly Uri UserAuthenticateUri = new("https://user.auth.xboxlive.com/user/authenticate");
    public static readonly Uri XstsAuthorizeUri = new("https://xsts.auth.xboxlive.com/xsts/authorize");
    public const string MinecraftRelyingParty = "rp://api.minecraftservices.com/";

    private readonly HttpClient _http;
    private readonly Uri _authenticateUri;
    private readonly Uri _xstsUri;

    public XboxClient(HttpClient http, Uri? authenticateUri = null, Uri? xstsUri = null)
    {
        _http = http;
        _authenticateUri = authenticateUri ?? UserAuthenticateUri;
        _xstsUri = xstsUri ?? XstsAuthorizeUri;
    }

    public async Task<XboxToken> AuthenticateAsync(string microsoftAccessToken, CancellationToken ct)
    {
        var body = new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoftAccessToken },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };
        var (status, doc) = await AuthHttp.PostJsonAsync(_http, _authenticateUri, body, ct).ConfigureAwait(false);
        using (doc)
            return Read(status, doc) ?? throw new AuthException($"Xbox Live отклонила вход (HTTP {(int)status}).");
    }

    public async Task<XboxToken> AuthorizeMinecraftAsync(string xboxLiveToken, CancellationToken ct)
    {
        var body = new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xboxLiveToken } },
            RelyingParty = MinecraftRelyingParty,
            TokenType = "JWT"
        };
        var (status, doc) = await AuthHttp.PostJsonAsync(_http, _xstsUri, body, ct).ConfigureAwait(false);
        using (doc)
        {
            if (Read(status, doc) is { } token) return token;
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("XErr", out var xerr) && xerr.TryGetInt64(out var code))
                throw new AuthException(ExplainXsts(code));
            throw new AuthException($"Xbox (XSTS) отклонила вход (HTTP {(int)status}).");
        }
    }

    /// <summary>Понятные пояснения для кодов XErr, описанных в документации протокола.</summary>
    public static string ExplainXsts(long code) => code switch
    {
        2148916233 => "У этой учётной записи Microsoft нет профиля Xbox. Создайте его на xbox.com и повторите вход.",
        2148916235 => "Xbox Live недоступна в стране этой учётной записи.",
        2148916236 or 2148916237 => "Для этой учётной записи нужна проверка возраста на странице Xbox.",
        2148916238 => "Это детская учётная запись: взрослый должен добавить её в семейную группу Microsoft.",
        2148916227 => "Учётная запись заблокирована в Xbox.",
        _ => $"Xbox отказала в доступе (код {code})."
    };

    private static XboxToken? Read(System.Net.HttpStatusCode status, JsonDocument? doc)
    {
        if ((int)status >= 400 || doc is null) return null;
        var root = doc.RootElement;
        var token = AuthHttp.String(root, "Token");
        if (token is null || root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("DisplayClaims", out var claims) || claims.ValueKind != JsonValueKind.Object ||
            !claims.TryGetProperty("xui", out var xui) || xui.ValueKind != JsonValueKind.Array || xui.GetArrayLength() == 0)
            return null;
        var uhs = AuthHttp.String(xui[0], "uhs");
        return uhs is null ? null : new XboxToken(token, uhs);
    }
}
