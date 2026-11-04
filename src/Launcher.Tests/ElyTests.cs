using System.Net;
using System.Security.Cryptography;
using System.Text;
using Launcher.Core.Authentication;
using Launcher.Core.Configuration;
using Launcher.Core.Errors;
using Launcher.Infrastructure.Authentication;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class AccountChoiceTests
{
    [Theory]
    [InlineData(null, true, true, AccountKind.Microsoft)]
    [InlineData(null, false, true, AccountKind.ElyBy)]
    [InlineData(null, false, false, AccountKind.Development)]
    [InlineData("elyby", true, true, AccountKind.ElyBy)]
    [InlineData("elyby", true, false, AccountKind.Microsoft)]   // выбранный недоступен — берём первый доступный
    [InlineData("microsoft", false, true, AccountKind.ElyBy)]
    [InlineData("dev", true, true, AccountKind.Development)]
    [InlineData("мусор", false, true, AccountKind.ElyBy)]
    public void Resolve_PicksPreferredIfAvailableOtherwiseFirstAvailable(string? preferred, bool ms, bool ely, AccountKind expected) =>
        Assert.Equal(expected, AccountChoice.Resolve(preferred, ms, ely));

    [Theory]
    [InlineData("#8cf0b8", "#8CF0B8")]
    [InlineData("8CF0B8", "#8CF0B8")]
    [InlineData(" #abc ", "#AABBCC")]
    [InlineData("#12345", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ThemeColors_Normalizes(string? input, string? expected) =>
        Assert.Equal(expected, ThemeColors.TryNormalizeHex(input));
}

public class HttpElyClientTests
{
    private sealed class RoutingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpElyClient Client(RoutingHandler handler) => new(new HttpClient(handler));

    private const string SessionJson =
        """{"accessToken":"AT","clientToken":"CT","availableProfiles":[{"id":"11111111-2222-3333-4444-555555555555","name":"Steve"}],"selectedProfile":{"id":"11111111-2222-3333-4444-555555555555","name":"Steve"}}""";

    [Fact]
    public async Task Authenticate_ParsesSessionAndStripsDashes()
    {
        var handler = new RoutingHandler((_, _) => Json(HttpStatusCode.OK, SessionJson));
        var session = await Client(handler).AuthenticateAsync("steve@example.com", "pw", "CT", default);

        Assert.Equal("AT", session.AccessToken);
        Assert.Equal("CT", session.ClientToken);
        Assert.Equal("Steve", session.Profile.Name);
        Assert.Equal("11111111222233334444555555555555", session.Profile.Uuid);
        Assert.Equal("https://authserver.ely.by/auth/authenticate", handler.Requests.Single().Url);
        Assert.Contains("\"username\":\"steve@example.com\"", handler.Requests.Single().Body);
        Assert.Contains("\"clientToken\":\"CT\"", handler.Requests.Single().Body);
    }

    [Fact]
    public async Task Authenticate_TwoFactorMessage_ThrowsTwoFactorRequired()
    {
        var handler = new RoutingHandler((_, _) => Json(HttpStatusCode.Unauthorized,
            """{"error":"ForbiddenOperationException","errorMessage":"Account protected with two factor auth."}"""));
        await Assert.ThrowsAsync<ElyTwoFactorRequiredException>(() => Client(handler).AuthenticateAsync("a", "b", "CT", default));
    }

    [Fact]
    public async Task Authenticate_WrongPassword_GivesFriendlyMessageWithoutSecrets()
    {
        var handler = new RoutingHandler((_, _) => Json(HttpStatusCode.Unauthorized,
            """{"error":"ForbiddenOperationException","errorMessage":"Invalid credentials. Invalid email or password."}"""));
        var ex = await Assert.ThrowsAsync<AuthException>(() => Client(handler).AuthenticateAsync("a", "SecretPw", "CT", default));
        Assert.DoesNotContain("SecretPw", ex.Message);
        Assert.Contains("Неверный", ex.Message);
        Assert.IsNotType<ElyTwoFactorRequiredException>(ex);
    }

    [Fact]
    public async Task Authenticate_NoSelectedProfile_Throws()
    {
        var handler = new RoutingHandler((_, _) => Json(HttpStatusCode.OK, """{"accessToken":"AT","clientToken":"CT"}"""));
        await Assert.ThrowsAsync<AuthException>(() => Client(handler).AuthenticateAsync("a", "b", "CT", default));
    }

    [Fact]
    public async Task Refresh_Rejected_ThrowsTokenRejected()
    {
        var handler = new RoutingHandler((_, _) => Json(HttpStatusCode.Unauthorized,
            """{"error":"ForbiddenOperationException","errorMessage":"Token expired."}"""));
        await Assert.ThrowsAsync<ElyTokenRejectedException>(() => Client(handler).RefreshAsync("AT", "CT", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task Validate_MapsStatus(HttpStatusCode code, bool expected)
    {
        var handler = new RoutingHandler((_, _) => new HttpResponseMessage(code));
        Assert.Equal(expected, await Client(handler).ValidateAsync("AT", default));
    }

    [Fact]
    public async Task Validate_ServerError_ThrowsAuthException()
    {
        var handler = new RoutingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<AuthException>(() => Client(handler).ValidateAsync("AT", default));
    }

    [Fact]
    public async Task NetworkFailure_BecomesAuthException()
    {
        var handler = new RoutingHandler((_, _) => throw new HttpRequestException("нет сети"));
        await Assert.ThrowsAsync<AuthException>(() => Client(handler).ValidateAsync("AT", default));
    }

    [Fact]
    public async Task Invalidate_UnauthorizedIsNotAnError()
    {
        var handler = new RoutingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Client(handler).InvalidateAsync("AT", "CT", default);
    }
}

public class ElyAccountServiceTests
{
    private sealed class FakeClient : IElyAuthClient
    {
        public string? LastUsername, LastPassword, LastClientToken;
        public bool? ValidateResult = true;
        public Exception? ValidateError, RefreshError, InvalidateError;
        public int Refreshes, Invalidates;

        public Task<ElySession> AuthenticateAsync(string usernameOrEmail, string passwordWithCode, string clientToken, CancellationToken ct)
        {
            LastUsername = usernameOrEmail;
            LastPassword = passwordWithCode;
            LastClientToken = clientToken;
            return Task.FromResult(new ElySession("AT1", clientToken, new ElyProfile("abc123", "Steve")));
        }

        public Task<ElySession> RefreshAsync(string accessToken, string clientToken, CancellationToken ct)
        {
            Refreshes++;
            if (RefreshError is not null) throw RefreshError;
            return Task.FromResult(new ElySession("AT2", clientToken, new ElyProfile("abc123", "Steve")));
        }

        public Task<bool> ValidateAsync(string accessToken, CancellationToken ct)
        {
            if (ValidateError is not null) throw ValidateError;
            return Task.FromResult(ValidateResult!.Value);
        }

        public Task InvalidateAsync(string accessToken, string clientToken, CancellationToken ct)
        {
            Invalidates++;
            if (InvalidateError is not null) throw InvalidateError;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryStore : IElyAccountStore
    {
        public StoredElyAccount? Value;
        public Task<StoredElyAccount?> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(StoredElyAccount account, CancellationToken ct) { Value = account; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct) { Value = null; return Task.CompletedTask; }
    }

    private readonly FakeClient _client = new();
    private readonly MemoryStore _store = new();
    private ElyAccountService Service => new(_client, _store);

    private void SignedIn() => _store.Value = new StoredElyAccount("Steve", "abc123", "AT0", "CT0");

    [Fact]
    public async Task Login_StoresTokensOnly_AndBuildsElyAccount()
    {
        var account = await Service.LoginAsync(" steve ", "pw", null, default);

        Assert.Equal("steve", _client.LastUsername);
        Assert.Equal("pw", _client.LastPassword);
        Assert.Equal("Steve", account.Username);
        Assert.Equal("abc123", account.Uuid);
        Assert.Equal("AT1", account.AccessToken);
        Assert.Equal("ely.by", account.AuthlibServer);
        Assert.False(account.IsDevelopment);
        Assert.Equal(32, _store.Value!.ClientToken.Length);
        Assert.DoesNotContain("pw", System.Text.Json.JsonSerializer.Serialize(_store.Value));
    }

    [Fact]
    public async Task Login_WithTwoFactorCode_AppendsItToPassword()
    {
        await Service.LoginAsync("steve", "pw", " 123456 ", default);
        Assert.Equal("pw:123456", _client.LastPassword);
    }

    [Fact]
    public async Task Login_ReusesClientToken()
    {
        SignedIn();
        await Service.LoginAsync("steve", "pw", null, default);
        Assert.Equal("CT0", _client.LastClientToken);
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("steve", "")]
    public async Task Login_EmptyFields_Throw(string user, string password) =>
        await Assert.ThrowsAsync<AuthException>(() => Service.LoginAsync(user, password, null, default));

    [Fact]
    public async Task GetGameAccount_NotSignedIn_IsNull() => Assert.Null(await Service.GetGameAccountAsync(default));

    [Fact]
    public async Task GetGameAccount_ValidToken_DoesNotRefresh()
    {
        SignedIn();
        var account = await Service.GetGameAccountAsync(default);
        Assert.Equal("AT0", account!.AccessToken);
        Assert.Equal(0, _client.Refreshes);
    }

    [Fact]
    public async Task GetGameAccount_InvalidToken_RefreshesAndSaves()
    {
        SignedIn();
        _client.ValidateResult = false;
        var account = await Service.GetGameAccountAsync(default);
        Assert.Equal("AT2", account!.AccessToken);
        Assert.Equal("AT2", _store.Value!.AccessToken);
        Assert.Equal("CT0", _store.Value.ClientToken);
    }

    [Fact]
    public async Task GetGameAccount_RefreshRejected_ClearsLoginAndThrows()
    {
        SignedIn();
        _client.ValidateResult = false;
        _client.RefreshError = new ElyTokenRejectedException();
        await Assert.ThrowsAsync<ElyTokenRejectedException>(() => Service.GetGameAccountAsync(default));
        Assert.Null(_store.Value);
    }

    [Fact]
    public async Task GetGameAccount_Offline_UsesStoredToken()
    {
        SignedIn();
        _client.ValidateError = new AuthException("нет сети");
        var account = await Service.GetGameAccountAsync(default);
        Assert.Equal("AT0", account!.AccessToken);
        Assert.NotNull(_store.Value);
    }

    [Fact]
    public async Task Logout_DeletesLocalDataEvenIfServerFails()
    {
        SignedIn();
        _client.InvalidateError = new AuthException("нет сети");
        await Service.LogoutAsync(default);
        Assert.Null(_store.Value);
        Assert.Equal(1, _client.Invalidates);
    }

    [Fact]
    public async Task Status_ReflectsStore()
    {
        Assert.False((await Service.GetStatusAsync(default)).SignedIn);
        SignedIn();
        var status = await Service.GetStatusAsync(default);
        Assert.True(status.SignedIn);
        Assert.Equal("Steve", status.Username);
    }
}

public class AuthlibInjectorProviderTests : TempDirectoryTest
{
    private sealed class FuncHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly byte[] Jar = Encoding.UTF8.GetBytes("fake-jar-content");

    private static string LatestJson(string sha256, string url = "https://example.test/authlib-injector-1.2.7.jar") =>
        "{\"build_number\":1,\"version\":\"1.2.7\",\"download_url\":\"" + url + "\",\"checksums\":{\"sha256\":\"" + sha256 + "\"}}";

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static HttpResponseMessage Text(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s) };
    private static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };

    private AuthlibInjectorProvider Create(FuncHandler handler, ManualTime? clock = null) =>
        new(new HttpClient(handler), Path.Combine(Dir, "authlib"), clock: clock);

    [Fact]
    public async Task Downloads_VerifiesChecksum_AndCachesForAnHour()
    {
        var handler = new FuncHandler(u => u.AbsolutePath.EndsWith("latest.json") ? Text(LatestJson(Sha(Jar))) : Bytes(Jar));
        var provider = Create(handler);

        var path = await provider.GetJarPathAsync(default);
        Assert.True(File.Exists(path));
        Assert.Equal(Jar, await File.ReadAllBytesAsync(path));
        Assert.EndsWith("authlib-injector-1.2.7.jar", path);

        var before = handler.Requests.Count;
        Assert.Equal(path, await provider.GetJarPathAsync(default));
        Assert.Equal(before, handler.Requests.Count); // повторно сеть не трогаем
    }

    [Fact]
    public async Task WrongChecksum_Throws_AndLeavesNoFile()
    {
        var handler = new FuncHandler(u => u.AbsolutePath.EndsWith("latest.json") ? Text(LatestJson(new string('0', 64))) : Bytes(Jar));
        var provider = Create(handler);

        await Assert.ThrowsAsync<AuthException>(() => provider.GetJarPathAsync(default));
        var dir = Path.Combine(Dir, "authlib");
        Assert.True(!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0);
    }

    [Fact]
    public async Task NonHttpsDownloadUrl_IsRejected()
    {
        var handler = new FuncHandler(u => Text(LatestJson(Sha(Jar), "http://example.test/a.jar")));
        await Assert.ThrowsAsync<AuthException>(() => Create(handler).GetJarPathAsync(default));
    }

    [Fact]
    public async Task Offline_UsesPreviouslyDownloadedJar()
    {
        var online = true;
        var handler = new FuncHandler(u =>
        {
            if (!online) throw new HttpRequestException("нет сети");
            return u.AbsolutePath.EndsWith("latest.json") ? Text(LatestJson(Sha(Jar))) : Bytes(Jar);
        });
        var clock = new ManualTime();
        var provider = Create(handler, clock);
        var path = await provider.GetJarPathAsync(default);

        online = false;
        clock.Now = clock.Now.AddHours(2);
        Assert.Equal(path, await provider.GetJarPathAsync(default));
    }

    [Fact]
    public async Task Offline_WithoutAnyJar_Throws()
    {
        var handler = new FuncHandler(_ => throw new HttpRequestException("нет сети"));
        await Assert.ThrowsAsync<AuthException>(() => Create(handler).GetJarPathAsync(default));
    }
}
