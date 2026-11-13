using System.IO.Compression;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Libraries;
using Launcher.Core.Versions;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class MinecraftInstallerTests : TempDirectoryTest
{
    private static readonly PlatformInfo Windows = new("windows", "x86_64", "10.0.22631.0");

    private sealed class FixedManifest(VersionManifest manifest) : IVersionManifestProvider
    {
        public Task<ManifestResult> GetManifestAsync(bool forceRefresh, CancellationToken ct) =>
            Task.FromResult(new ManifestResult(manifest, ManifestSource.Network, DateTimeOffset.UtcNow));
    }

    private static byte[] NativesZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "lwjgl.dll", "META-INF/MANIFEST.MF" })
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write("x");
        }
        return ms.ToArray();
    }

    private (MinecraftInstaller Installer, FakeDownloader Downloader, MinecraftLayout Layout) Create()
    {
        var downloader = new FakeDownloader()
            .Serve(VersionTestData.VersionJsonUrl, VersionTestData.VersionJson)
            .Serve(VersionTestData.AssetIndexUrl, VersionTestData.AssetIndexJson)
            .Serve(VersionTestData.PlatformNativesWindowsUrl, NativesZip());
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var manifest = VersionManifestParser.Parse(TestData.ManifestJson);
        return (new MinecraftInstaller(new FixedManifest(manifest), downloader, layout, Windows), downloader, layout);
    }

    [Fact]
    public async Task Install_DownloadsClientLibrariesAndAssets_ForCurrentPlatformOnly()
    {
        var (installer, downloader, layout) = Create();

        var result = await installer.InstallAsync("1.21.1", new InstallOptions(), null, default);

        // клиент + logging + lwjgl natives-windows + lwjgl-platform + его natives + 2 уникальных ресурса
        Assert.Equal(7, result.FilesTotal);
        Assert.Equal(7, result.FilesDownloaded);
        Assert.True(File.Exists(layout.VersionJsonPath("1.21.1")));
        Assert.True(File.Exists(layout.ClientJarPath("1.21.1")));
        Assert.True(File.Exists(layout.LibraryPath("com/mojang/logging/1.2.7/logging-1.2.7.jar")));
        Assert.True(File.Exists(layout.AssetObjectPath(VersionTestData.HashA)));
        Assert.True(File.Exists(layout.AssetObjectPath(VersionTestData.HashC)));
        Assert.True(File.Exists(layout.AssetIndexPath("17")));
        Assert.DoesNotContain(downloader.Requests, r => r.Url.ToString().Contains("natives-linux"));
    }

    [Fact]
    public async Task Install_ExtractsNatives_WithoutExcludedEntries()
    {
        var (installer, _, layout) = Create();

        await installer.InstallAsync("1.21.1", new InstallOptions(), null, default);

        var natives = layout.NativesDirectory("1.21.1");
        Assert.True(File.Exists(Path.Combine(natives, "lwjgl.dll")));
        Assert.False(Directory.Exists(Path.Combine(natives, "META-INF")));
    }

    [Fact]
    public async Task Install_SecondRun_DownloadsNothingNew()
    {
        var (installer, _, _) = Create();
        await installer.InstallAsync("1.21.1", new InstallOptions(), null, default);

        var second = await installer.InstallAsync("1.21.1", new InstallOptions(), null, default);

        Assert.Equal(0, second.FilesDownloaded);
        Assert.Equal(7, second.FilesAlreadyValid);
    }

    [Fact]
    public async Task Install_ReportsStagesAndEndsWithCompleted()
    {
        var (installer, _, _) = Create();
        var stages = new List<InstallStage>();
        var progress = new Launcher.Core.Downloads.InlineProgress<InstallProgress>(p => { lock (stages) stages.Add(p.Stage); });

        await installer.InstallAsync("1.21.1", new InstallOptions(MaxConcurrency: 2), progress, default);

        Assert.Equal(InstallStage.VersionList, stages.First());
        Assert.Equal(InstallStage.Completed, stages.Last());
        Assert.Contains(InstallStage.Files, stages);
        Assert.Contains(InstallStage.Natives, stages);
    }

    [Fact]
    public async Task Install_UnknownVersion_SuggestsSimilarOnes()
    {
        var (installer, _, _) = Create();

        var ex = await Assert.ThrowsAsync<VersionNotFoundException>(
            () => installer.InstallAsync("1.21", new InstallOptions(), null, default));

        Assert.Contains("1.21.1", ex.Message);
    }

    [Fact]
    public async Task Install_RejectsLibraryPathOutsideLibrariesFolder()
    {
        var (_, downloader, layout) = Create();
        var evil = VersionTestData.VersionJson.Replace(
            "com/mojang/logging/1.2.7/logging-1.2.7.jar", "../../../evil.jar");
        downloader.Serve(VersionTestData.VersionJsonUrl, evil);
        var installer = new MinecraftInstaller(
            new FixedManifest(VersionManifestParser.Parse(TestData.ManifestJson)), downloader, layout, Windows);

        await Assert.ThrowsAsync<VersionMetadataException>(
            () => installer.InstallAsync("1.21.1", new InstallOptions(), null, default));
    }
}
