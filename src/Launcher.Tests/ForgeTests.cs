using System.IO.Compression;
using Launcher.Core.Configuration;
using Launcher.Core.Errors;
using Launcher.Core.Forge;
using Launcher.Core.Installation;
using Launcher.Core.JavaRuntime;
using Launcher.Core.Libraries;
using Launcher.Core.Versions;
using Launcher.Infrastructure.Forge;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class ForgeTests : TempDirectoryTest
{
    private static readonly PlatformInfo Windows = new("windows", "x86_64", "10.0.22631.0");

    private sealed class FakeForgeMeta(string? sha1 = "0123456789012345678901234567890123456789") : IForgeMetaClient
    {
        public Task<IReadOnlyDictionary<string, string>> GetPromotionsAsync(CancellationToken ct) =>
            Task.FromResult(ForgeVersions.ParsePromotions(ForgeTestData.PromotionsJson));

        public Task<string?> GetInstallerSha1Async(string gameVersion, string forgeVersion, CancellationToken ct) =>
            Task.FromResult(sha1);
    }

    /// <summary>Устанавливает «ванильную» версию: кладёт её JSON на диск.</summary>
    private sealed class FakeVanilla(MinecraftLayout layout) : IMinecraftInstaller
    {
        public int Calls { get; private set; }

        public Task<InstallResult> InstallAsync(string versionId, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct)
        {
            Calls++;
            Directory.CreateDirectory(layout.VersionDirectory(versionId));
            File.WriteAllText(layout.VersionJsonPath(versionId), VersionTestData.VersionJson);
            return Task.FromResult(new InstallResult(versionId, "", "", "", 0, 0, 0, 0));
        }
    }

    /// <summary>Подменяет запуск установщика Forge: записывает, что запускали, и при успехе создаёт профиль версии.</summary>
    private sealed class FakeInstallerRunner(MinecraftLayout layout, int exitCode, bool writeVersion, bool timedOut = false) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            if (arguments.Contains("-version")) return Task.FromResult(JavaSamples.Ok("21.0.4")); // проверка Java
            Calls.Add([fileName, .. arguments]);
            if (writeVersion)
            {
                Directory.CreateDirectory(layout.VersionDirectory(ForgeTestData.ForgeId));
                File.WriteAllText(layout.VersionJsonPath(ForgeTestData.ForgeId), ForgeTestData.VersionJson);
            }
            return Task.FromResult(new ProcessResult(exitCode, "line1\nline2", exitCode == 0 ? "" : "boom", timedOut));
        }
    }

    private static byte[] InstallerJar(string? versionJson)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("install_profile.json").Open())) w.Write("{}");
            if (versionJson is not null)
                using (var w = new StreamWriter(zip.CreateEntry("version.json").Open())) w.Write(versionJson);
        }
        return ms.ToArray();
    }

    private (ForgeInstaller Installer, FakeInstallerRunner Runner, FakeVanilla Vanilla, FakeDownloader Downloader, MinecraftLayout Layout) Create(
        byte[]? jar = null, int exitCode = 0, bool writeVersion = true, bool timedOut = false, string? sha1 = "0123456789012345678901234567890123456789")
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var vanilla = new FakeVanilla(layout);
        var downloader = new FakeDownloader().Serve(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-installer.jar",
            jar ?? InstallerJar(ForgeTestData.VersionJson));
        var runner = new FakeInstallerRunner(layout, exitCode, writeVersion, timedOut);
        var java = new JavaResolver(
            new FakeJavaDetector(JavaSamples.Make(21, path: "/jdk21/bin/java")),
            new JavaProbe(runner));
        var installer = new ForgeInstaller(
            new FakeForgeMeta(sha1), vanilla, downloader, runner, java, new MemorySettings(),
            new InstalledVersionRepository(layout), layout, Path.Combine(Dir, "installers"));
        return (installer, runner, vanilla, downloader, layout);
    }

    // --- список сборок

    [Fact]
    public void Choose_PrefersExplicitThenRecommendedThenLatest()
    {
        var promos = ForgeVersions.ParsePromotions(ForgeTestData.PromotionsJson);

        Assert.Equal("52.1.0", ForgeVersions.Choose(promos, "1.21.1", null));
        Assert.Equal("47.4.28", ForgeVersions.Choose(promos, "1.20.1", null)); // рекомендованной нет — последняя
        Assert.Equal("52.1.19", ForgeVersions.Choose(promos, "1.21.1", "52.1.19"));
    }

    [Fact]
    public void Choose_UnknownGameVersion_Throws()
    {
        var promos = ForgeVersions.ParsePromotions(ForgeTestData.PromotionsJson);

        Assert.Throws<ForgeException>(() => ForgeVersions.Choose(promos, "1.2.3", null));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("")]
    public void UnsafeVersions_AreRejected(string value)
    {
        var promos = ForgeVersions.ParsePromotions(ForgeTestData.PromotionsJson);

        Assert.Throws<ForgeException>(() => ForgeVersions.Choose(promos, "1.21.1", value));
        Assert.Throws<ForgeException>(() => ForgeVersions.InstallerUri(value, "52.1.0"));
    }

    [Fact]
    public void InstallerUri_IsOfficialMavenPath() =>
        Assert.Equal(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-installer.jar",
            ForgeVersions.InstallerUri("1.21.1", "52.1.0").ToString());

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    public void ParsePromotions_RejectsBadFormat(string json) =>
        Assert.Throws<ForgeException>(() => ForgeVersions.ParsePromotions(json));

    // --- профиль Forge (адрес основного JAR пуст)

    [Fact]
    public void Parser_AcceptsLibraryWithoutUrl_AndMergeKeepsIt()
    {
        var overlay = VersionDetailsParser.ParseOverlay(ForgeTestData.VersionJson);

        var forgeJar = overlay.Libraries[0].Artifact!;
        Assert.Null(forgeJar.Url);
        Assert.Equal("net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-universal.jar", forgeJar.Path);
        Assert.NotNull(overlay.Libraries[1].Artifact!.Url);
    }

    [Fact]
    public void Parser_ClientWithoutUrl_IsRejected()
    {
        var json = VersionTestData.VersionJson.Replace("https://piston-data.mojang.com/v1/objects/c/client.jar", "");

        Assert.Throws<VersionMetadataException>(() => VersionDetailsParser.Parse(json));
    }

    // --- установка

    [Fact]
    public async Task Install_RunsOfficialInstallerWithInstallClient_AndReturnsVersionId()
    {
        var (installer, runner, vanilla, downloader, layout) = Create();

        var result = await installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default);

        Assert.Equal(ForgeTestData.ForgeId, result.VersionId);
        Assert.Equal("52.1.0", result.ForgeVersion);
        Assert.Equal(1, vanilla.Calls);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("/jdk21/bin/java", call[0]);
        Assert.Equal("-jar", call[1]);
        Assert.EndsWith("forge-1.21.1-52.1.0-installer.jar", call[2]);
        Assert.Equal("--installClient", call[3]);
        Assert.Equal(layout.Root, call[4]);
        Assert.True(File.Exists(Path.Combine(layout.Root, "launcher_profiles.json")));
        Assert.Contains(downloader.Requests, r => r.Sha1 == "0123456789012345678901234567890123456789");
    }

    [Fact]
    public async Task Install_WithoutPublishedChecksum_StillInstalls()
    {
        var (installer, _, _, downloader, _) = Create(sha1: null);

        await installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default);

        Assert.All(downloader.Requests, r => Assert.Null(r.Sha1));
    }

    [Fact]
    public async Task Install_OldFormatInstaller_IsRejectedWithExplanation_AndNothingIsRun()
    {
        var (installer, runner, _, _, _) = Create(jar: InstallerJar(null));

        var ex = await Assert.ThrowsAsync<ForgeException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));

        Assert.Contains("1.13", ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Install_InstallerForAnotherGameVersion_IsRejected()
    {
        var wrong = ForgeTestData.VersionJson.Replace("\"inheritsFrom\": \"1.21.1\"", "\"inheritsFrom\": \"1.20.1\"");
        var (installer, runner, _, _, _) = Create(jar: InstallerJar(wrong));

        await Assert.ThrowsAsync<ForgeException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Install_CorruptInstaller_GivesReadableError()
    {
        var (installer, _, _, _, _) = Create(jar: "not a zip"u8.ToArray());

        var ex = await Assert.ThrowsAsync<ForgeException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));

        Assert.Contains("повреждён", ex.Message);
    }

    [Fact]
    public async Task Install_InstallerFails_ShowsOutputTail()
    {
        var (installer, _, _, _, _) = Create(exitCode: 1, writeVersion: false);

        var ex = await Assert.ThrowsAsync<ForgeException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));

        Assert.Contains("код 1", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Install_InstallerSucceedsButNoVersion_IsReported()
    {
        var (installer, _, _, _, _) = Create(writeVersion: false);

        var ex = await Assert.ThrowsAsync<ForgeException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));

        Assert.Contains(ForgeTestData.ForgeId, ex.Message);
    }

    [Fact]
    public async Task Install_Timeout_IsReported()
    {
        var (installer, _, _, _, _) = Create(exitCode: -1, writeVersion: false, timedOut: true);

        var ex = await Assert.ThrowsAsync<ForgeException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));

        Assert.Contains("не завершился", ex.Message);
    }

    // --- HTTP-клиент

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Http_ReadsSha1_AndUsesOfficialUrl()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("C445499201106CD56123029315A8E53228EA677B  forge.jar\n") });

        var sha1 = await new HttpForgeMetaClient(new HttpClient(handler)).GetInstallerSha1Async("1.21.1", "52.1.0", default);

        Assert.Equal("c445499201106cd56123029315a8e53228ea677b", sha1);
        Assert.Equal(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-installer.jar.sha1",
            handler.Requests[0].ToString());
    }

    [Fact]
    public async Task Http_MissingSha1File_MeansNull()
    {
        var client = new HttpForgeMetaClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))));

        Assert.Null(await client.GetInstallerSha1Async("1.21.1", "52.1.0", default));
    }

    [Fact]
    public async Task Http_GarbageInSha1File_MeansNull()
    {
        var client = new HttpForgeMetaClient(new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html>oops</html>") })));

        Assert.Null(await client.GetInstallerSha1Async("1.21.1", "52.1.0", default));
    }

    [Fact]
    public async Task Http_NetworkError_IsReadable()
    {
        var client = new HttpForgeMetaClient(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("boom"))));

        await Assert.ThrowsAsync<ForgeException>(() => client.GetPromotionsAsync(default));
    }
}
