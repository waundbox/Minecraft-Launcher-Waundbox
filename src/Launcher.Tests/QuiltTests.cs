using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Libraries;
using Launcher.Core.Mods;
using Launcher.Core.Quilt;
using Launcher.Core.Versions;
using Launcher.Infrastructure.Quilt;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class QuiltTests : TempDirectoryTest
{
    private static readonly PlatformInfo Windows = new("windows", "x86_64", "10.0.22631.0");

    private const string ChildId = "quilt-loader-0.26.0-1.20.1";

    // Формат ответа meta.quiltmc.org/v3: у загрузчика нет поля «stable», предварительные сборки — с суффиксом.
    private const string LoadersJson = """
        [
          { "loader": { "maven": "org.quiltmc:quilt-loader:0.20.0-beta.9", "version": "0.20.0-beta.9", "build": 9 } },
          { "loader": { "maven": "org.quiltmc:quilt-loader:0.26.0", "version": "0.26.0", "build": 1 } },
          { "loader": { "version": "0.27.0-beta.1" } },
          { "loader": { "version": "0.9.0" } }
        ]
        """;

    private const string ProfileJson = """
        {
          "id": "quilt-loader-0.26.0-1.20.1",
          "inheritsFrom": "1.20.1",
          "type": "release",
          "mainClass": "org.quiltmc.loader.impl.launch.knot.KnotClient",
          "arguments": { "game": [], "jvm": [] },
          "libraries": [
            { "name": "org.ow2.asm:asm:9.5", "url": "https://maven.fabricmc.net/" },
            { "name": "org.quiltmc:quilt-loader:0.26.0", "url": "https://maven.quiltmc.org/repository/release/" }
          ]
        }
        """;

    private sealed class FakeMeta(string profileJson) : IQuiltMetaClient
    {
        public Task<IReadOnlyList<QuiltLoaderVersion>> GetLoaderVersionsAsync(string gameVersion, CancellationToken ct)
        {
            IReadOnlyList<QuiltLoaderVersion> result = gameVersion == "1.20.1"
                ? QuiltMetaParser.ParseLoaders(LoadersJson)
                : new List<QuiltLoaderVersion>();
            return Task.FromResult(result);
        }

        public Task<string> GetProfileJsonAsync(string gameVersion, string loaderVersion, CancellationToken ct) =>
            Task.FromResult(profileJson);
    }

    private sealed class FakeVanilla : IMinecraftInstaller
    {
        public List<string> Installed { get; } = new();

        public Task<InstallResult> InstallAsync(string versionId, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct)
        {
            Installed.Add(versionId);
            return Task.FromResult(new InstallResult(versionId, "", "", "", 0, 0, 0, 0));
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private (QuiltInstaller Installer, FakeVanilla Vanilla, FakeDownloader Downloader, MinecraftLayout Layout) Create(string? profileJson = null)
    {
        var vanilla = new FakeVanilla();
        var downloader = new FakeDownloader();
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        return (new QuiltInstaller(new FakeMeta(profileJson ?? ProfileJson), vanilla, downloader, layout, Windows), vanilla, downloader, layout);
    }

    [Fact]
    public void ParseLoaders_MarksPrereleasesByVersionSuffix()
    {
        var loaders = QuiltMetaParser.ParseLoaders(LoadersJson);
        Assert.Equal(4, loaders.Count);
        Assert.False(loaders[0].Stable);
        Assert.True(loaders[1].Stable);
        Assert.False(loaders[2].Stable);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""[ { "loader": {} } ]""")]
    public void ParseLoaders_BadInput_Throws(string json) =>
        Assert.Throws<QuiltException>(() => QuiltMetaParser.ParseLoaders(json));

    [Fact]
    public void Newest_ComparesNumbersNotText()
    {
        Assert.Equal("0.26.0", QuiltInstaller.Newest(QuiltMetaParser.ParseLoaders(LoadersJson).Where(l => l.Stable).ToList()).Version);
        Assert.True(QuiltInstaller.Compare("0.20.0-beta.10", "0.20.0-beta.9") > 0);
        Assert.True(QuiltInstaller.Compare("0.20.0", "0.20.0-beta.9") > 0);
    }

    [Fact]
    public async Task Install_WritesProfileAfterLibraries()
    {
        var (installer, vanilla, downloader, layout) = Create();

        var result = await installer.InstallAsync("1.20.1", null, new InstallOptions(), null, default);

        Assert.Equal(ChildId, result.VersionId);
        Assert.Equal("0.26.0", result.LoaderVersion); // самая новая стабильная
        Assert.Equal("1.20.1", Assert.Single(vanilla.Installed));
        Assert.True(File.Exists(layout.VersionJsonPath(ChildId)));
        Assert.Contains(downloader.Requests, r => r.Url.ToString() == "https://maven.fabricmc.net/org/ow2/asm/asm/9.5/asm-9.5.jar");
        Assert.Contains(downloader.Requests, r => r.Url.ToString() == "https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-loader/0.26.0/quilt-loader-0.26.0.jar");
    }

    [Fact]
    public async Task Install_UnknownGame_Throws()
    {
        var (installer, _, _, _) = Create();
        await Assert.ThrowsAsync<QuiltException>(() => installer.InstallAsync("0.0.1", null, new InstallOptions(), null, default));
    }

    [Fact]
    public async Task Install_UnknownLoader_Throws()
    {
        var (installer, _, _, _) = Create();
        await Assert.ThrowsAsync<QuiltException>(() => installer.InstallAsync("1.20.1", "9.9.9", new InstallOptions(), null, default));
    }

    [Fact]
    public async Task Install_WrongInheritsFrom_WritesNothing()
    {
        var (installer, _, _, layout) = Create(ProfileJson.Replace("\"inheritsFrom\": \"1.20.1\"", "\"inheritsFrom\": \"1.20\""));
        await Assert.ThrowsAsync<QuiltException>(() => installer.InstallAsync("1.20.1", null, new InstallOptions(), null, default));
        Assert.False(File.Exists(layout.VersionJsonPath(ChildId)));
    }

    [Fact]
    public async Task HttpClient_UsesV3Endpoints()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(LoadersJson) });
        var client = new HttpQuiltMetaClient(new HttpClient(handler));

        var loaders = await client.GetLoaderVersionsAsync("1.20.1", default);
        await client.GetProfileJsonAsync("1.20.1", "0.26.0", default);

        Assert.Equal(4, loaders.Count);
        Assert.Equal("https://meta.quiltmc.org/v3/versions/loader/1.20.1", handler.Requests[0].ToString());
        Assert.Equal("https://meta.quiltmc.org/v3/versions/loader/1.20.1/0.26.0/profile/json", handler.Requests[1].ToString());
    }

    [Fact]
    public async Task HttpClient_NotFound_MeansNoVersions()
    {
        var client = new HttpQuiltMetaClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))));
        Assert.Empty(await client.GetLoaderVersionsAsync("nope", default));
    }

    [Fact]
    public async Task HttpClient_NetworkFailure_BecomesQuiltException()
    {
        var client = new HttpQuiltMetaClient(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("boom"))));
        await Assert.ThrowsAsync<QuiltException>(() => client.GetLoaderVersionsAsync("1.20.1", default));
    }

    [Theory]
    [InlineData("quilt-loader-0.26.0-1.20.1", "1.20.1", "0.26.0")]
    [InlineData("quilt-loader-0.20.0-beta.9-1.20.1", "1.20.1", "0.20.0-beta.9")]
    public void VersionLabel_RecognizesQuilt(string id, string game, string loader)
    {
        var label = VersionLabel.Parse(id);
        Assert.Equal(LoaderKind.Quilt, label.Loader);
        Assert.Equal(game, label.GameVersion);
        Assert.Equal(loader, label.LoaderVersion);
        Assert.Equal("Quilt " + game, label.Display);
    }

    [Fact]
    public void Quilt_UsesFabricModsOnModrinth() => Assert.Equal("fabric", LoaderKind.Quilt.ModrinthLoader());
}
