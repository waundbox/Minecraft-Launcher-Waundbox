using Launcher.Core.Errors;
using Launcher.Core.Fabric;
using Launcher.Core.Installation;
using Launcher.Core.Libraries;
using Launcher.Core.Versions;
using Launcher.Infrastructure.Fabric;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class FabricTests : TempDirectoryTest
{
    private static readonly PlatformInfo Windows = new("windows", "x86_64", "10.0.22631.0");

    private sealed class FakeMeta(string loadersJson, string profileJson) : IFabricMetaClient
    {
        public Task<IReadOnlyList<FabricLoaderVersion>> GetLoaderVersionsAsync(string gameVersion, CancellationToken ct)
        {
            IReadOnlyList<FabricLoaderVersion> result = gameVersion == "1.21.1"
                ? FabricMetaParser.ParseLoaders(loadersJson)
                : new List<FabricLoaderVersion>();
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

    private (FabricInstaller Installer, FakeVanilla Vanilla, FakeDownloader Downloader, MinecraftLayout Layout) Create(string? profileJson = null)
    {
        var vanilla = new FakeVanilla();
        var downloader = new FakeDownloader();
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var installer = new FabricInstaller(
            new FakeMeta(FabricTestData.LoadersJson, profileJson ?? FabricTestData.ProfileJson), vanilla, downloader, layout, Windows);
        return (installer, vanilla, downloader, layout);
    }

    // --- координаты Maven

    [Theory]
    [InlineData("org.ow2.asm:asm:9.10.1", "org/ow2/asm/asm/9.10.1/asm-9.10.1.jar")]
    [InlineData("net.fabricmc:fabric-loader:0.19.5", "net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.3:natives-windows", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar")]
    [InlineData("a.b:c:1.0@zip", "a/b/c/1.0/c-1.0.zip")]
    public void MavenCoordinates_BuildPath(string coordinates, string expected) =>
        Assert.Equal(expected, MavenCoordinates.ToRelativePath(coordinates));

    [Theory]
    [InlineData("broken")]
    [InlineData("a:b")]
    [InlineData("a:../b:1.0")]
    [InlineData("a:b:1.0@../x")]
    public void MavenCoordinates_RejectsBadInput(string coordinates) =>
        Assert.Throws<VersionMetadataException>(() => MavenCoordinates.ToRelativePath(coordinates));

    [Fact]
    public void MavenCoordinates_KeyIgnoresVersion()
    {
        Assert.Equal("org.ow2.asm:asm", MavenCoordinates.KeyWithoutVersion("org.ow2.asm:asm:9.10.1"));
        Assert.Equal("org.lwjgl:lwjgl:natives-windows", MavenCoordinates.KeyWithoutVersion("org.lwjgl:lwjgl:3.3.3:natives-windows"));
    }

    // --- разбор профиля и слияние

    [Fact]
    public void ParseOverlay_ReadsMavenStyleLibraries()
    {
        var overlay = VersionDetailsParser.ParseOverlay(FabricTestData.ProfileJson);

        Assert.Equal(FabricTestData.ChildId, overlay.Id);
        Assert.Equal("1.21.1", overlay.InheritsFrom);
        Assert.Equal(3, overlay.Libraries.Count);
        var asm = overlay.Libraries[0].Artifact!;
        Assert.Equal(FabricTestData.AsmUrl, asm.Url!.ToString());
        Assert.Equal("org/ow2/asm/asm/9.10.1/asm-9.10.1.jar", asm.Path);
        Assert.Equal(VersionTestData.HashA, asm.Sha1);
        Assert.Equal(FabricTestData.LoaderUrl, overlay.Libraries[2].Artifact!.Url!.ToString());
        Assert.Null(overlay.Libraries[2].Artifact!.Sha1);
    }

    [Fact]
    public void ParseOverlay_LibraryWithoutDownloadsAndUrl_IsRejected()
    {
        var json = """{ "id": "x", "mainClass": "m", "libraries": [ { "name": "a:b:1" } ] }""";

        Assert.Throws<VersionMetadataException>(() => VersionDetailsParser.ParseOverlay(json));
    }

    [Fact]
    public void Merge_ChildLibrariesWinAndArgumentsAreConcatenated()
    {
        var parent = VersionDetailsParser.Parse(LaunchTestData.VersionJson);
        var overlay = VersionDetailsParser.ParseOverlay(FabricTestData.ProfileJson);

        var merged = VersionMerger.Merge(parent, overlay);

        Assert.Equal(FabricTestData.ChildId, merged.Id);
        Assert.Equal("1.21.1", merged.ClientJarId);
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", merged.MainClass);
        Assert.Equal(parent.AssetIndex, merged.AssetIndex);
        Assert.Equal(21, merged.JavaMajorVersion);
        Assert.Equal("org.ow2.asm:asm:9.10.1", merged.Libraries[0].Name);
        Assert.Contains(merged.Libraries, l => l.Name == "com.mojang:logging:9.9.9");
        Assert.DoesNotContain(merged.Libraries, l => l.Name == "com.mojang:logging:1.2.7");
        Assert.Equal(2, merged.AllRawJson.Count);
        Assert.Equal(parent.Logging, merged.Logging);
    }

    // --- хранилище установленных версий

    [Fact]
    public async Task Repository_LoadsInheritanceChain()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        Directory.CreateDirectory(layout.VersionDirectory("1.21.1"));
        File.WriteAllText(layout.VersionJsonPath("1.21.1"), VersionTestData.VersionJson);
        Directory.CreateDirectory(layout.VersionDirectory(FabricTestData.ChildId));
        File.WriteAllText(layout.VersionJsonPath(FabricTestData.ChildId), FabricTestData.ProfileJson);

        var details = await new InstalledVersionRepository(layout).LoadAsync(FabricTestData.ChildId, default);

        Assert.Equal(FabricTestData.ChildId, details.Id);
        Assert.Equal("1.21.1", details.ClientJarId);
    }

    [Fact]
    public async Task Repository_DetectsInheritanceCycle()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        foreach (var (id, parent) in new[] { ("a", "b"), ("b", "a") })
        {
            Directory.CreateDirectory(layout.VersionDirectory(id));
            File.WriteAllText(layout.VersionJsonPath(id), $$"""{ "id": "{{id}}", "inheritsFrom": "{{parent}}", "mainClass": "m" }""");
        }

        await Assert.ThrowsAsync<VersionMetadataException>(() => new InstalledVersionRepository(layout).LoadAsync("a", default));
    }

    // --- список загрузчиков

    [Fact]
    public void ParseLoaders_ReadsVersionsAndStability()
    {
        var loaders = FabricMetaParser.ParseLoaders(FabricTestData.LoadersJson);

        Assert.Equal(new[] { "0.19.5", "0.19.6-beta.1", "0.19.4" }, loaders.Select(l => l.Version).ToArray());
        Assert.Equal(new[] { true, false, true }, loaders.Select(l => l.Stable).ToArray());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""[ { "loader": {} } ]""")]
    public void ParseLoaders_RejectsBadFormat(string json) =>
        Assert.Throws<FabricException>(() => FabricMetaParser.ParseLoaders(json));

    // --- установка

    [Fact]
    public async Task Install_UsesLatestStableLoader_InstallsBaseAndDownloadsLibraries()
    {
        var (installer, vanilla, downloader, layout) = Create();

        var result = await installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default);

        Assert.Equal("1.21.1", Assert.Single(vanilla.Installed));
        Assert.Equal(FabricTestData.ChildId, result.VersionId);
        Assert.Equal("0.19.5", result.LoaderVersion); // 0.19.6-beta.1 нестабильна и пропущена
        Assert.Equal(3, result.FilesTotal);
        Assert.True(File.Exists(layout.VersionJsonPath(FabricTestData.ChildId)));
        Assert.True(File.Exists(layout.LibraryPath("net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar")));
        Assert.Contains(downloader.Requests, r => r.Url.ToString() == FabricTestData.AsmUrl && r.Sha1 == VersionTestData.HashA);
    }

    [Fact]
    public async Task Install_ExplicitLoader_IsRespected_AndUnknownOneIsRejected()
    {
        var (installer, _, _, _) = Create();

        var ex = await Assert.ThrowsAsync<FabricException>(
            () => installer.InstallAsync("1.21.1", "9.9.9", new InstallOptions(), null, default));

        Assert.Contains("0.19.5", ex.Message);
    }

    [Fact]
    public async Task Install_UnsupportedGameVersion_FailsBeforeTouchingDisk()
    {
        var (installer, vanilla, _, layout) = Create();

        await Assert.ThrowsAsync<FabricException>(() => installer.InstallAsync("0.0.1", null, new InstallOptions(), null, default));

        Assert.Empty(vanilla.Installed);
        Assert.False(Directory.Exists(layout.VersionsDirectory));
    }

    [Fact]
    public async Task Install_ProfileForAnotherGameVersion_IsRejected_AndNothingIsWritten()
    {
        var wrong = FabricTestData.ProfileJson.Replace("\"inheritsFrom\": \"1.21.1\"", "\"inheritsFrom\": \"1.20\"");
        var (installer, _, _, layout) = Create(wrong);

        await Assert.ThrowsAsync<FabricException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));

        Assert.False(File.Exists(layout.VersionJsonPath(FabricTestData.ChildId)));
    }

    [Fact]
    public async Task Install_ProfileWithSameIdAsGame_IsRejected()
    {
        var wrong = FabricTestData.ProfileJson.Replace(FabricTestData.ChildId, "1.21.1");
        var (installer, _, _, _) = Create(wrong);

        await Assert.ThrowsAsync<FabricException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));
    }

    [Fact]
    public async Task Install_ProfileWithTraversalId_IsRejected()
    {
        var wrong = FabricTestData.ProfileJson.Replace(FabricTestData.ChildId, "../../evil");
        var (installer, _, _, _) = Create(wrong);

        await Assert.ThrowsAsync<VersionMetadataException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));
    }

    // --- HTTP-клиент

    [Fact]
    public async Task HttpClient_BuildsOfficialUrls_AndEscapesVersions()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]") });
        var client = new HttpFabricMetaClient(new HttpClient(handler));

        await client.GetLoaderVersionsAsync("1.21.1", default);
        await client.GetProfileJsonAsync("1.21.1", "0.19.5", default);

        Assert.Equal("https://meta.fabricmc.net/v2/versions/loader/1.21.1", handler.Requests[0].ToString());
        Assert.Equal("https://meta.fabricmc.net/v2/versions/loader/1.21.1/0.19.5/profile/json", handler.Requests[1].ToString());
    }

    [Fact]
    public async Task HttpClient_NotFoundMeansNoLoaders_ForList()
    {
        var client = new HttpFabricMetaClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest))));

        Assert.Empty(await client.GetLoaderVersionsAsync("nope", default));
        await Assert.ThrowsAsync<FabricException>(() => client.GetProfileJsonAsync("nope", "1", default));
    }

    [Fact]
    public async Task HttpClient_ServerError_IsReadable()
    {
        var client = new HttpFabricMetaClient(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("boom"))));

        var ex = await Assert.ThrowsAsync<FabricException>(() => client.GetLoaderVersionsAsync("1.21.1", default));

        Assert.Contains("Fabric", ex.Message);
    }
}

public class VersionLabelTests
{
    [Theory]
    [InlineData("1.21.1", LoaderKind.Vanilla, "1.21.1", null)]
    [InlineData("24w40a", LoaderKind.Vanilla, "24w40a", null)]
    [InlineData("fabric-loader-0.19.5-1.21.1", LoaderKind.Fabric, "1.21.1", "0.19.5")]
    [InlineData("fabric-loader-0.19.6-beta.1-1.21.1", LoaderKind.Fabric, "1.21.1", "0.19.6-beta.1")]
    [InlineData("fabric-loader-0.19.5-1.21.2-pre1", LoaderKind.Fabric, "1.21.2-pre1", "0.19.5")]
    [InlineData("1.21.1-forge-52.1.0", LoaderKind.Forge, "1.21.1", "52.1.0")]
    public void Parse_RecognizesLoaders(string id, LoaderKind kind, string game, string? loader)
    {
        var label = VersionLabel.Parse(id);

        Assert.Equal(kind, label.Loader);
        Assert.Equal(game, label.GameVersion);
        Assert.Equal(loader, label.LoaderVersion);
    }

    [Fact]
    public void Display_IsShort()
    {
        Assert.Equal("Fabric 1.21.1", VersionLabel.Parse("fabric-loader-0.19.5-1.21.1").Display);
        Assert.Equal("1.21.1", VersionLabel.Parse("1.21.1").Display);
    }
}
