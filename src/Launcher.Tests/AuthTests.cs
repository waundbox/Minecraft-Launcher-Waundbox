using System.Net;
using System.Text;
using Launcher.Cli.Commands;
using Launcher.Core.Authentication;
using Launcher.Core.Configuration;
using Launcher.Core.Errors;
using Launcher.Infrastructure.Authentication;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class AccountServiceTests
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeOAuth : IMicrosoftOAuthClient
    {
        public Queue<DevicePollResult> Polls { get; } = new();
        public int PollCalls { get; private set; }
        public int DeviceCodeSeconds { get; set; } = 900;
        public Func<string, OAuthTokens> OnRefresh { get; set; } = _ => new OAuthTokens("ms-access-2", "refresh-2", DateTimeOffset.UnixEpoch);
        public List<string> RefreshTokensUsed { get; } = new();

        public Task<DeviceCodeInfo> RequestDeviceCodeAsync(string clientId, CancellationToken ct) =>
            Task.FromResult(new DeviceCodeInfo("secret-device-code", "ABCD1234", new Uri("https://www.microsoft.com/link"), DeviceCodeSeconds, 5));

        public Task<DevicePollResult> PollAsync(string clientId, DeviceCodeInfo code, CancellationToken ct)
        {
            PollCalls++;
            return Task.FromResult(Polls.Dequeue());
        }

        public Task<OAuthTokens> RefreshAsync(string clientId, string refreshToken, CancellationToken ct)
        {
            RefreshTokensUsed.Add(refreshToken);
            return Task.FromResult(OnRefresh(refreshToken));
        }
    }

    private sealed class FakeXbox : IXboxClient
    {
        public string? LastMicrosoftToken { get; private set; }

        public Task<XboxToken> AuthenticateAsync(string microsoftAccessToken, CancellationToken ct)
        {
            LastMicrosoftToken = microsoftAccessToken;
            return Task.FromResult(new XboxToken("xbl-token", "uhs-1"));
        }

        public Task<XboxToken> AuthorizeMinecraftAsync(string xboxLiveToken, CancellationToken ct) =>
            Task.FromResult(new XboxToken("xsts-token", "uhs-2"));
    }

    private sealed class FakeMinecraft(FakeClock clock) : IMinecraftServicesClient
    {
        public MinecraftProfile? Profile { get; set; } = new("069a79f4-44e9-4726-a5be-fca90e38aaf5", "Steve");
        public (string, string)? LastLogin { get; private set; }

        public Task<MinecraftSession> LoginWithXboxAsync(string userHash, string xstsToken, CancellationToken ct)
        {
            LastLogin = (userHash, xstsToken);
            return Task.FromResult(new MinecraftSession("mc-token", clock.Now.AddHours(24)));
        }

        public Task<MinecraftProfile?> GetProfileAsync(string minecraftAccessToken, CancellationToken ct) =>
            Task.FromResult(Profile);

        public List<(string Token, int Length, bool Slim)> Uploads { get; } = new();
        public MinecraftSkinInfo? Skin { get; set; }

        public Task<MinecraftSkinInfo?> GetSkinAsync(string minecraftAccessToken, CancellationToken ct) => Task.FromResult(Skin);

        public Task UploadSkinAsync(string minecraftAccessToken, byte[] png, bool slim, CancellationToken ct)
        {
            Uploads.Add((minecraftAccessToken, png.Length, slim));
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadSkinAsync(Uri url, CancellationToken ct) => Task.FromResult(new byte[] { 1, 2, 3 });
    }

    private sealed class MemoryAccountStore : IAccountStore
    {
        public StoredAccount? Account { get; set; }
        public Task<StoredAccount?> LoadAsync(CancellationToken ct) => Task.FromResult(Account);
        public Task SaveAsync(StoredAccount account, CancellationToken ct) { Account = account; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct) { Account = null; return Task.CompletedTask; }
    }

    private sealed record Env(
        AccountService Service, FakeOAuth OAuth, FakeXbox Xbox, FakeMinecraft Minecraft,
        MemoryAccountStore Store, FakeClock Clock, List<TimeSpan> Delays);

    private static Env Create(string? clientId = ClientId)
    {
        var clock = new FakeClock();
        var oauth = new FakeOAuth();
        var xbox = new FakeXbox();
        var mc = new FakeMinecraft(clock);
        var store = new MemoryAccountStore();
        var delays = new List<TimeSpan>();
        var service = new AccountService(oauth, xbox, mc, store, new MemorySettings(new LauncherSettings(MicrosoftClientId: clientId)),
            clock: clock, delay: (t, _) => { delays.Add(t); clock.Now += t; return Task.CompletedTask; },
            embeddedClientId: ""); // вшитый client id в тестах не используется
        return new Env(service, oauth, xbox, mc, store, clock, delays);
    }

    private static DevicePollResult Success() =>
        new(DevicePollStatus.Success, new OAuthTokens("ms-access", "refresh-1", DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task Login_Succeeds_AfterPending_AndStoresRefreshToken()
    {
        var env = Create();
        env.OAuth.Polls.Enqueue(new DevicePollResult(DevicePollStatus.Pending));
        env.OAuth.Polls.Enqueue(Success());
        DeviceCodeInfo? shown = null;

        var account = await env.Service.LoginAsync(c => shown = c, default);

        Assert.Equal("ABCD1234", shown!.UserCode);
        Assert.Equal("Steve", account.Username);
        Assert.Equal("069a79f444e94726a5befca90e38aaf5", account.Uuid); // без дефисов
        Assert.Equal("msa", account.UserType);
        Assert.False(account.IsDevelopment);
        Assert.Equal("mc-token", account.AccessToken);
        Assert.Equal("ms-access", env.Xbox.LastMicrosoftToken);
        Assert.Equal(("uhs-2", "xsts-token"), env.Minecraft.LastLogin!.Value); // uhs берётся из ответа XSTS
        Assert.Equal("refresh-1", env.Store.Account!.RefreshToken);
        Assert.Equal(2, env.OAuth.PollCalls);
    }

    [Fact]
    public async Task Login_SlowDown_IncreasesInterval()
    {
        var env = Create();
        env.OAuth.Polls.Enqueue(new DevicePollResult(DevicePollStatus.SlowDown));
        env.OAuth.Polls.Enqueue(Success());

        await env.Service.LoginAsync(_ => { }, default);

        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) }, env.Delays.ToArray());
    }

    [Theory]
    [InlineData(DevicePollStatus.Declined, "отклонён")]
    [InlineData(DevicePollStatus.Expired, "истекло")]
    public async Task Login_DeclinedOrExpired_ThrowsReadableError(DevicePollStatus status, string expected)
    {
        var env = Create();
        env.OAuth.Polls.Enqueue(new DevicePollResult(status));

        var ex = await Assert.ThrowsAsync<AuthException>(() => env.Service.LoginAsync(_ => { }, default));

        Assert.Contains(expected, ex.Message);
        Assert.Null(env.Store.Account);
    }

    [Fact]
    public async Task Login_StopsWhenCodeLifetimeEnds()
    {
        var env = Create();
        env.OAuth.DeviceCodeSeconds = 12;
        for (var i = 0; i < 5; i++) env.OAuth.Polls.Enqueue(new DevicePollResult(DevicePollStatus.Pending));

        var ex = await Assert.ThrowsAsync<AuthException>(() => env.Service.LoginAsync(_ => { }, default));

        Assert.Contains("истекло", ex.Message);
        Assert.True(env.OAuth.PollCalls <= 2);
    }

    [Fact]
    public async Task Login_WithoutGameLicense_IsRejected_AndNothingIsStored()
    {
        var env = Create();
        env.Minecraft.Profile = null;
        env.OAuth.Polls.Enqueue(Success());

        var ex = await Assert.ThrowsAsync<AuthException>(() => env.Service.LoginAsync(_ => { }, default));

        Assert.Contains("Java Edition", ex.Message);
        Assert.Null(env.Store.Account);
    }

    [Fact]
    public async Task Login_WithoutClientId_ExplainsSetup()
    {
        var env = Create(clientId: null);

        var ex = await Assert.ThrowsAsync<AuthException>(() => env.Service.LoginAsync(_ => { }, default));

        Assert.Contains("client id", ex.Message);
    }

    [Fact]
    public async Task GetAccount_NotSignedIn_ReturnsNull() =>
        Assert.Null(await Create().Service.GetAccountAsync(default));

    [Fact]
    public async Task GetAccount_FreshToken_IsUsedWithoutNetwork()
    {
        var env = Create();
        env.Store.Account = new StoredAccount("Steve", "069a79f444e94726a5befca90e38aaf5", "refresh-1", "mc-old", env.Clock.Now.AddHours(2));

        var account = await env.Service.GetAccountAsync(default);

        Assert.Equal("mc-old", account!.AccessToken);
        Assert.Empty(env.OAuth.RefreshTokensUsed);
    }

    [Fact]
    public async Task GetAccount_ExpiredToken_RefreshesAndSavesNewRefreshToken()
    {
        var env = Create();
        env.Store.Account = new StoredAccount("Steve", "069a79f444e94726a5befca90e38aaf5", "refresh-1", "mc-old", env.Clock.Now.AddMinutes(2));

        var account = await env.Service.GetAccountAsync(default);

        Assert.Equal("mc-token", account!.AccessToken);
        Assert.Equal(new[] { "refresh-1" }, env.OAuth.RefreshTokensUsed.ToArray());
        Assert.Equal("refresh-2", env.Store.Account!.RefreshToken); // ротация токена обновления
    }

    [Fact]
    public async Task GetAccount_RefreshRejected_AsksToSignInAgain()
    {
        var env = Create();
        env.OAuth.OnRefresh = _ => throw new AuthException("Не удалось обновить вход. Выполните вход заново: auth login");
        env.Store.Account = new StoredAccount("Steve", "u", "refresh-1", null, null);

        var ex = await Assert.ThrowsAsync<AuthException>(() => env.Service.GetAccountAsync(default));

        Assert.Contains("auth login", ex.Message);
    }

    [Fact]
    public async Task UploadSkin_UsesSignedInAccountToken()
    {
        var env = Create();
        env.Store.Account = new StoredAccount("Steve", "u", "r", "MCTOKEN", env.Clock.Now.AddHours(5));
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 64, 0, 0, 0, 64 }.CopyTo(png, 0);

        await env.Service.UploadSkinAsync(png, slim: true, default);

        Assert.Equal(("MCTOKEN", 33, true), env.Minecraft.Uploads.Single());
    }

    [Fact]
    public async Task UploadSkin_NotSignedIn_Throws()
    {
        var env = Create();
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 64, 0, 0, 0, 64 }.CopyTo(png, 0);

        await Assert.ThrowsAsync<AuthException>(() => env.Service.UploadSkinAsync(png, false, default));
        Assert.Empty(env.Minecraft.Uploads);
    }

    [Fact]
    public async Task UploadSkin_RejectsWrongImageBeforeNetwork()
    {
        var env = Create();
        env.Store.Account = new StoredAccount("Steve", "u", "r", "MCTOKEN", env.Clock.Now.AddHours(5));

        await Assert.ThrowsAsync<LauncherException>(() => env.Service.UploadSkinAsync(new byte[] { 1, 2, 3 }, false, default));
        Assert.Empty(env.Minecraft.Uploads);
    }

    [Fact]
    public async Task Logout_RemovesStoredAccount()
    {
        var env = Create();
        env.Store.Account = new StoredAccount("Steve", "u", "r", null, null);

        await env.Service.LogoutAsync(default);

        Assert.Null(env.Store.Account);
        Assert.False((await env.Service.GetStatusAsync(default)).SignedIn);
    }

    [Fact]
    public async Task Status_ContainsNoSecrets()
    {
        var env = Create();
        env.Store.Account = new StoredAccount("Steve", "uuid-1", "REFRESH", "MCTOKEN", env.Clock.Now.AddHours(1));

        var status = await env.Service.GetStatusAsync(default);

        Assert.True(status.SignedIn);
        Assert.True(status.ClientIdConfigured);
        Assert.DoesNotContain("REFRESH", status.ToString());
        Assert.DoesNotContain("MCTOKEN", status.ToString());
    }
}

public class AuthClientsTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Body, string? Auth)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!, body, request.Headers.Authorization?.ToString()));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // --- Microsoft

    [Fact]
    public async Task OAuth_RequestsDeviceCode_WithConsumersTenantAndXboxScope()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """
            { "device_code": "DC", "user_code": "UC", "verification_uri": "https://www.microsoft.com/link", "expires_in": 900, "interval": 5, "message": "m" }
            """));
        var client = new MicrosoftOAuthClient(new HttpClient(handler));

        var code = await client.RequestDeviceCodeAsync("cid", default);

        Assert.Equal("UC", code.UserCode);
        Assert.Equal(900, code.ExpiresInSeconds);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", request.Uri.ToString());
        Assert.Contains("client_id=cid", request.Body);
        Assert.Contains("scope=XboxLive.signin+offline_access", request.Body);
    }

    [Fact]
    public async Task OAuth_DeviceCodeRejected_MentionsClientIdAndPublicFlows()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.BadRequest, """{ "error": "invalid_client", "error_description": "bad app" }"""));

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => new MicrosoftOAuthClient(new HttpClient(handler)).RequestDeviceCodeAsync("cid", default));

        Assert.Contains("invalid_client", ex.Message);
        Assert.Contains("client id", ex.Message);
    }

    [Theory]
    [InlineData("authorization_pending", DevicePollStatus.Pending)]
    [InlineData("slow_down", DevicePollStatus.SlowDown)]
    [InlineData("authorization_declined", DevicePollStatus.Declined)]
    [InlineData("expired_token", DevicePollStatus.Expired)]
    public async Task OAuth_Poll_MapsErrorsToStatuses(string error, DevicePollStatus expected)
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.BadRequest, $$"""{ "error": "{{error}}" }"""));
        var code = new DeviceCodeInfo("DC", "UC", new Uri("https://www.microsoft.com/link"), 900, 5);

        var result = await new MicrosoftOAuthClient(new HttpClient(handler)).PollAsync("cid", code, default);

        Assert.Equal(expected, result.Status);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", handler.Requests[0].Body);
        Assert.Contains("device_code=DC", handler.Requests[0].Body);
    }

    [Fact]
    public async Task OAuth_Poll_UnknownError_Throws()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.BadRequest, """{ "error": "invalid_client" }"""));
        var code = new DeviceCodeInfo("DC", "UC", new Uri("https://www.microsoft.com/link"), 900, 5);

        await Assert.ThrowsAsync<AuthException>(() => new MicrosoftOAuthClient(new HttpClient(handler)).PollAsync("cid", code, default));
    }

    [Fact]
    public async Task OAuth_Poll_SuccessReturnsTokens()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{ "access_token": "A", "refresh_token": "R", "expires_in": 3600 }"""));
        var code = new DeviceCodeInfo("DC", "UC", new Uri("https://www.microsoft.com/link"), 900, 5);

        var result = await new MicrosoftOAuthClient(new HttpClient(handler)).PollAsync("cid", code, default);

        Assert.Equal(DevicePollStatus.Success, result.Status);
        Assert.Equal("A", result.Tokens!.AccessToken);
        Assert.Equal("R", result.Tokens.RefreshToken);
    }

    [Fact]
    public async Task OAuth_Refresh_InvalidGrant_AsksToSignInAgain()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.BadRequest, """{ "error": "invalid_grant", "error_description": "expired" }"""));

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => new MicrosoftOAuthClient(new HttpClient(handler)).RefreshAsync("cid", "SECRET-REFRESH", default));

        Assert.Contains("auth login", ex.Message);
        Assert.DoesNotContain("SECRET-REFRESH", ex.Message); // токен не должен попасть в сообщение
    }

    [Fact]
    public async Task OAuth_NetworkFailure_IsReadable()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("boom"));

        await Assert.ThrowsAsync<AuthException>(() => new MicrosoftOAuthClient(new HttpClient(handler)).RequestDeviceCodeAsync("cid", default));
    }

    // --- Xbox

    [Fact]
    public async Task Xbox_Authenticate_SendsRpsTicket_AndReadsUserHash()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """
            { "Token": "XBL", "DisplayClaims": { "xui": [ { "uhs": "UHS" } ] } }
            """));

        var token = await new XboxClient(new HttpClient(handler)).AuthenticateAsync("MSTOKEN", default);

        Assert.Equal(new XboxToken("XBL", "UHS"), token);
        var request = handler.Requests[0];
        Assert.Equal("https://user.auth.xboxlive.com/user/authenticate", request.Uri.ToString());
        Assert.Contains("\"RpsTicket\":\"d=MSTOKEN\"", request.Body);
        Assert.Contains("\"RelyingParty\":\"http://auth.xboxlive.com\"", request.Body);
        Assert.Contains("\"TokenType\":\"JWT\"", request.Body);
    }

    [Fact]
    public async Task Xbox_Xsts_UsesMinecraftRelyingParty()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """
            { "Token": "XSTS", "DisplayClaims": { "xui": [ { "uhs": "UHS" } ] } }
            """));

        await new XboxClient(new HttpClient(handler)).AuthorizeMinecraftAsync("XBL", default);

        var request = handler.Requests[0];
        Assert.Equal("https://xsts.auth.xboxlive.com/xsts/authorize", request.Uri.ToString());
        Assert.Contains("\"RelyingParty\":\"rp://api.minecraftservices.com/\"", request.Body);
        Assert.Contains("\"SandboxId\":\"RETAIL\"", request.Body);
        Assert.Contains("\"UserTokens\":[\"XBL\"]", request.Body);
    }

    [Theory]
    [InlineData(2148916233, "нет профиля Xbox")]
    [InlineData(2148916238, "семейн")]
    [InlineData(2148916235, "стране")]
    public async Task Xbox_Xsts_ExplainsKnownErrors(long code, string fragment)
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.Unauthorized, $$"""{ "Identity": "0", "XErr": {{code}}, "Message": "" }"""));

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => new XboxClient(new HttpClient(handler)).AuthorizeMinecraftAsync("XBL", default));

        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public async Task Xbox_MalformedResponse_Throws()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{ "Token": "X", "DisplayClaims": [] }"""));

        await Assert.ThrowsAsync<AuthException>(() => new XboxClient(new HttpClient(handler)).AuthenticateAsync("MS", default));
    }

    // --- Minecraft Services

    [Fact]
    public async Task Minecraft_Login_SendsXbl3IdentityToken()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{ "access_token": "MC", "expires_in": 86400, "token_type": "Bearer" }"""));

        var session = await new MinecraftServicesClient(new HttpClient(handler)).LoginWithXboxAsync("UHS", "XSTS", default);

        Assert.Equal("MC", session.AccessToken);
        var request = handler.Requests[0];
        Assert.Equal("https://api.minecraftservices.com/authentication/login_with_xbox", request.Uri.ToString());
        Assert.Contains("\"identityToken\":\"XBL3.0 x=UHS;XSTS\"", request.Body);
    }

    [Fact]
    public async Task Minecraft_Login403_ExplainsAppApproval()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.Forbidden, """{ "path": "/authentication/login_with_xbox", "error": "Forbidden" }"""));

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => new MinecraftServicesClient(new HttpClient(handler)).LoginWithXboxAsync("U", "X", default));

        Assert.Contains("403", ex.Message);
        Assert.Contains("Azure", ex.Message);
    }

    [Fact]
    public async Task Minecraft_Profile_ReadsIdAndName_AndSendsBearer()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{ "id": "abc", "name": "Steve", "skins": [] }"""));

        var profile = await new MinecraftServicesClient(new HttpClient(handler)).GetProfileAsync("MC", default);

        Assert.Equal(new MinecraftProfile("abc", "Steve"), profile);
        Assert.Equal("Bearer MC", handler.Requests[0].Auth);
        Assert.Equal("https://api.minecraftservices.com/minecraft/profile", handler.Requests[0].Uri.ToString());
    }

    private static byte[] SkinPng()
    {
        // сигнатура PNG + заголовок IHDR с размером 64×64 — этого достаточно для проверки скина
        var data = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 64, 0, 0, 0, 64 }.CopyTo(data, 0);
        return data;
    }

    [Theory]
    [InlineData(true, "slim")]
    [InlineData(false, "classic")]
    public async Task Minecraft_UploadSkin_SendsMultipartWithVariantAndFile(bool slim, string variant)
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{ "id": "abc", "name": "Steve" }"""));

        await new MinecraftServicesClient(new HttpClient(handler)).UploadSkinAsync("MC", SkinPng(), slim, default);

        var request = handler.Requests[0];
        Assert.Equal("https://api.minecraftservices.com/minecraft/profile/skins", request.Uri.ToString());
        Assert.Equal("Bearer MC", request.Auth);
        Assert.Contains("name=variant", request.Body);
        Assert.Contains(variant, request.Body);
        Assert.Contains("name=file", request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "заново")]
    [InlineData(HttpStatusCode.TooManyRequests, "подождать")]
    [InlineData(HttpStatusCode.BadRequest, "PNG")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Minecraft_UploadSkin_Errors_AreExplained(HttpStatusCode status, string expected)
    {
        var handler = new FakeHandler((_, _) => Json(status, "{}"));
        var ex = await Assert.ThrowsAsync<AuthException>(
            () => new MinecraftServicesClient(new HttpClient(handler)).UploadSkinAsync("MC", SkinPng(), false, default));
        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("MC", ex.Message.Replace("Mojang", "").Replace("Minecraft", "").Replace("Microsoft", ""));
    }

    [Fact]
    public async Task Minecraft_GetSkin_ReadsActiveSkinAndVariant()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """
            { "id": "abc", "name": "Steve", "skins": [
                { "id": "1", "state": "INACTIVE", "url": "https://textures.minecraft.net/texture/old", "variant": "CLASSIC" },
                { "id": "2", "state": "ACTIVE", "url": "https://textures.minecraft.net/texture/new", "variant": "SLIM" } ] }
            """));

        var skin = await new MinecraftServicesClient(new HttpClient(handler)).GetSkinAsync("MC", default);

        Assert.Equal("https://textures.minecraft.net/texture/new", skin!.Url.ToString());
        Assert.True(skin.Slim);
    }

    [Fact]
    public async Task Minecraft_DownloadSkin_RefusesForeignHosts()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, "x"));
        var client = new MinecraftServicesClient(new HttpClient(handler));

        await Assert.ThrowsAsync<AuthException>(() => client.DownloadSkinAsync(new Uri("https://evil.example.com/a.png"), default));
        await Assert.ThrowsAsync<AuthException>(() => client.DownloadSkinAsync(new Uri("http://evil.example.com/texture/a"), default));
        await Assert.ThrowsAsync<AuthException>(() => client.DownloadSkinAsync(new Uri("ftp://textures.minecraft.net/texture/a"), default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Minecraft_DownloadSkin_UpgradesHttpTextureUrlToHttps()
    {
        // Mojang возвращает адрес скина с http://; раньше из-за этого скин аккаунта не загружался
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, "png"));
        var client = new MinecraftServicesClient(new HttpClient(handler));

        await client.DownloadSkinAsync(new Uri("http://textures.minecraft.net/texture/abc"), default);

        Assert.Equal("https://textures.minecraft.net/texture/abc", handler.Requests.Single().Uri.ToString());
    }

    [Fact]
    public async Task Minecraft_Profile404_MeansNoGame()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.NotFound, """{ "error": "NOT_FOUND" }"""));

        Assert.Null(await new MinecraftServicesClient(new HttpClient(handler)).GetProfileAsync("MC", default));
    }
}

public class DpapiAccountStoreTests : TempDirectoryTest
{
    [Fact]
    public async Task RoundTrip_EncryptsFileOnDisk()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI есть только в Windows; на других системах тест не проверяет ничего

        var path = Path.Combine(Dir, "account.dat");
        var store = new DpapiAccountStore(path);
        var account = new StoredAccount("Steve", "uuid", "SECRET-REFRESH-TOKEN", "SECRET-MC-TOKEN", DateTimeOffset.UnixEpoch);

        await store.SaveAsync(account, default);

        Assert.DoesNotContain("SECRET-REFRESH-TOKEN", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.Equal(account, await store.LoadAsync(default));

        await store.DeleteAsync(default);
        Assert.Null(await store.LoadAsync(default));
    }

    [Fact]
    public async Task CorruptFile_MeansNotSignedIn()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Dir, "account.dat");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Null(await new DpapiAccountStore(path).LoadAsync(default));
    }
}

public class AuthCliOptionsTests
{
    [Fact]
    public void Setup_AcceptsGuid_AndLowercasesIt()
    {
        var o = AuthCliOptions.TryParse(["setup", "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"], out var error);

        Assert.Null(error);
        Assert.Equal(AuthAction.Setup, o!.Action);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", o.ClientId);
    }

    [Theory]
    [InlineData("login", AuthAction.Login)]
    [InlineData("status", AuthAction.Status)]
    [InlineData("logout", AuthAction.Logout)]
    public void SimpleActions_Parse(string word, AuthAction expected) =>
        Assert.Equal(expected, AuthCliOptions.TryParse([word], out _)!.Action);

    [Theory]
    [InlineData("setup")]
    [InlineData("setup not-a-guid")]
    [InlineData("setup a b")]
    [InlineData("login extra")]
    [InlineData("whatever")]
    public void BadInput_IsRejected(string line)
    {
        Assert.Null(AuthCliOptions.TryParse(line.Split(' '), out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
