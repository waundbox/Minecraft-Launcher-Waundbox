using System.IO.Compression;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.JavaRuntime;
using Launcher.Core.Libraries;
using Launcher.Core.Mods;
using Launcher.Core.NeoForge;
using Launcher.Core.Versions;
using Launcher.Infrastructure.NeoForge;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class NeoForgeVersionTests
{
    private const string Metadata = """
        <?xml version="1.0" encoding="UTF-8"?>
        <metadata><groupId>net.neoforged</groupId><artifactId>neoforge</artifactId>
          <versioning><versions>
            <version>1.20.1-47.1.105</version><version>1.20.1-47.1.106</version><version>20.2.86</version><version>20.4.237</version>
            <version>21.0.167</version>
            <version>21.1.171</version><version>21.1.172</version><version>21.1.173-beta</version>
            <version>21.5.0-beta</version>
            <version>26.1.0.5-beta</version><version>26.1.1.3</version>
          </versions></versioning></metadata>
        """;

    private static readonly IReadOnlyList<string> All = NeoForgeVersions.ParseMetadata(Metadata);

    [Fact]
    public void ParseMetadata_ReadsVersionsInOrder()
    {
        Assert.Equal(11, All.Count);
        Assert.Equal("1.20.1-47.1.105", All[0]); // версии для 1.20.1 идут первыми, как в metadata
    }

    [Theory]
    [InlineData("")]
    [InlineData("не xml")]
    [InlineData("<metadata><versioning><versions></versions></versioning></metadata>")]
    public void ParseMetadata_BadInput_Throws(string xml) =>
        Assert.Throws<NeoForgeException>(() => NeoForgeVersions.ParseMetadata(xml));

    [Theory]
    [InlineData("1.21.1", "21.1.")]
    [InlineData("1.21", "21.0.")]
    [InlineData("1.20.2", "20.2.")]
    [InlineData("1.20.4", "20.4.")]
    [InlineData("26.1", "26.1.0.")]
    [InlineData("26.1.1", "26.1.1.")]
    [InlineData("1.20.1", "1.20.1-")]
    [InlineData("1.19.4", null)]
    [InlineData("24w10a", null)]
    public void Prefix_FollowsDocumentedScheme(string game, string? expected) =>
        Assert.Equal(expected, NeoForgeVersions.Prefix(game));

    [Theory]
    [InlineData("21.1.172", "1.21.1")]
    [InlineData("21.0.167", "1.21")]
    [InlineData("20.4.237", "1.20.4")]
    [InlineData("21.5.0-beta", "1.21.5")]
    [InlineData("26.1.0.5-beta", "26.1")]
    [InlineData("26.1.1.3", "26.1.1")]
    [InlineData("1.20.1-47.1.106", "1.20.1")]
    [InlineData("abc", null)]
    public void GameVersionOf_InvertsThePrefix(string neo, string? expected) =>
        Assert.Equal(expected, NeoForgeVersions.GameVersionOf(neo));

    [Fact]
    public void Choose_PrefersLatestStable_ThenAnyLatest_ThenExplicit()
    {
        Assert.Equal("21.1.172", NeoForgeVersions.Choose(All, "1.21.1", null));       // beta 21.1.173 пропускается
        Assert.Equal("21.5.0-beta", NeoForgeVersions.Choose(All, "1.21.5", null));    // стабильных нет — берётся beta
        Assert.Equal("21.1.171", NeoForgeVersions.Choose(All, "1.21.1", "21.1.171"));
        Assert.Equal("26.1.1.3", NeoForgeVersions.Choose(All, "26.1.1", null));
    }

    [Fact]
    public void Choose_Errors()
    {
        Assert.Throws<NeoForgeException>(() => NeoForgeVersions.Choose(All, "1.19.4", null));       // раньше 1.20.1 NeoForge нет
        Assert.Throws<NeoForgeException>(() => NeoForgeVersions.Choose(All, "1.20.6", null));       // нет сборок
        Assert.Throws<NeoForgeException>(() => NeoForgeVersions.Choose(All, "1.21.1", "20.4.237")); // версия от другой игры
        Assert.Throws<NeoForgeException>(() => NeoForgeVersions.Choose(All, "1.21.1", "../evil"));
    }

    [Fact]
    public void SupportedGames_ListsGamesWithBuilds()
    {
        var games = NeoForgeVersions.SupportedGames(All);
        Assert.Contains("1.21.1", games);
        Assert.Contains("1.21", games);
        Assert.Contains("26.1", games);
        Assert.Contains("1.20.1", games);
        Assert.DoesNotContain("1.19.4", games);
    }

    [Fact]
    public void Legacy_1_20_1_UsesForgeArtifact()
    {
        Assert.Equal("1.20.1-47.1.106", NeoForgeVersions.Choose(All, "1.20.1", null));
        Assert.True(NeoForgeVersions.IsLegacy("1.20.1-47.1.106"));
        Assert.False(NeoForgeVersions.IsLegacy("21.1.172"));
        Assert.Equal("https://maven.neoforged.net/releases/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar",
            NeoForgeVersions.InstallerUri("1.20.1-47.1.106").ToString());
        Assert.Equal("https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.172/neoforge-21.1.172-installer.jar",
            NeoForgeVersions.InstallerUri("21.1.172").ToString());
    }

    [Fact]
    public void InstallerUri_IsOnOfficialMaven() =>
        Assert.Equal(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.172/neoforge-21.1.172-installer.jar",
            NeoForgeVersions.InstallerUri("21.1.172").AbsoluteUri);

    [Theory]
    [InlineData("neoforge-21.1.172", LoaderKind.NeoForge, "1.21.1", "21.1.172")]
    [InlineData("1.21.1-forge-52.1.0", LoaderKind.Forge, "1.21.1", "52.1.0")]
    [InlineData("1.21.1", LoaderKind.Vanilla, "1.21.1", null)]
    public void VersionLabel_RecognizesNeoForge(string id, LoaderKind kind, string game, string? loader)
    {
        var label = VersionLabel.Parse(id);
        Assert.Equal(kind, label.Loader);
        Assert.Equal(game, label.GameVersion);
        Assert.Equal(loader, label.LoaderVersion);
    }

    [Fact]
    public void ModrinthLoader_ForNeoForge() => Assert.Equal("neoforge", LoaderKind.NeoForge.ModrinthLoader());
}

public class NeoForgeInstallerTests : TempDirectoryTest
{
    private const string NeoId = "neoforge-21.1.172";

    private static readonly string OverlayJson = $$"""
        { "id": "{{NeoId}}", "inheritsFrom": "1.21.1", "type": "release",
          "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
          "arguments": { "game": ["--launchTarget", "forgeclient"], "jvm": [] },
          "libraries": [] }
        """;

    private sealed class FakeMeta : INeoForgeMetaClient
    {
        public Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["21.1.171", "21.1.172"]);

        public Task<string?> GetInstallerSha1Async(string neoForgeVersion, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FakeVanilla(MinecraftLayout layout) : IMinecraftInstaller
    {
        public Task<InstallResult> InstallAsync(string versionId, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct)
        {
            Directory.CreateDirectory(layout.VersionDirectory(versionId));
            File.WriteAllText(layout.VersionJsonPath(versionId), VersionTestData.VersionJson);
            return Task.FromResult(new InstallResult(versionId, "", "", "", 0, 0, 0, 0));
        }
    }

    private sealed class FakeRunner(MinecraftLayout layout, int exitCode) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            if (arguments.Contains("-version")) return Task.FromResult(JavaSamples.Ok("21.0.4"));
            Calls.Add([fileName, .. arguments]);
            if (exitCode == 0)
            {
                Directory.CreateDirectory(layout.VersionDirectory(NeoId));
                File.WriteAllText(layout.VersionJsonPath(NeoId), OverlayJson);
            }
            return Task.FromResult(new ProcessResult(exitCode, "out", exitCode == 0 ? "" : "boom", false));
        }
    }

    private static byte[] Jar(string? versionJson)
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

    private (NeoForgeInstaller Installer, FakeRunner Runner) Create(byte[]? jar = null, int exitCode = 0)
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var downloader = new FakeDownloader().Serve(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.172/neoforge-21.1.172-installer.jar",
            jar ?? Jar(OverlayJson));
        var runner = new FakeRunner(layout, exitCode);
        var java = new JavaResolver(new FakeJavaDetector(JavaSamples.Make(21, path: "/jdk21/bin/java")), new JavaProbe(runner));
        var installer = new NeoForgeInstaller(
            new FakeMeta(), new FakeVanilla(layout), downloader, runner, java, new MemorySettings(),
            new InstalledVersionRepository(layout), layout, Path.Combine(Dir, "installers"));
        return (installer, runner);
    }

    [Fact]
    public async Task Install_RunsInstallClient_AndReturnsVersionId()
    {
        var (installer, runner) = Create();

        var result = await installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default);

        Assert.Equal(NeoId, result.VersionId);
        Assert.Equal("21.1.172", result.NeoForgeVersion);
        var call = Assert.Single(runner.Calls);
        Assert.Contains("--install-client", call);
        Assert.Contains("-jar", call);
    }

    [Fact]
    public async Task Install_InstallerFails_ShowsOutput()
    {
        var (installer, _) = Create(exitCode: 3);
        var ex = await Assert.ThrowsAsync<NeoForgeException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Install_InstallerWithoutVersionJson_Throws()
    {
        var (installer, _) = Create(jar: Jar(null));
        await Assert.ThrowsAsync<NeoForgeException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));
    }

    [Fact]
    public async Task Install_NotAJar_Throws()
    {
        var (installer, _) = Create(jar: [1, 2, 3]);
        await Assert.ThrowsAsync<NeoForgeException>(() => installer.InstallAsync("1.21.1", null, new InstallOptions(), null, default));
    }

    [Fact]
    public async Task Install_UnsupportedGame_Throws()
    {
        var (installer, _) = Create();
        await Assert.ThrowsAsync<NeoForgeException>(() => installer.InstallAsync("1.19.4", null, new InstallOptions(), null, default));
    }
}
