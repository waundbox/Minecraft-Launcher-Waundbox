using System.IO.Compression;
using Launcher.Core.Assets;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Libraries;
using Launcher.Core.Versions;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class LegacyAssetsAndLoggingTests : TempDirectoryTest
{
    private static readonly PlatformInfo Windows = new("windows", "x86_64", "10.0.22631.0");

    private sealed class FixedManifest(VersionManifest manifest) : IVersionManifestProvider
    {
        public Task<ManifestResult> GetManifestAsync(bool forceRefresh, CancellationToken ct) =>
            Task.FromResult(new ManifestResult(manifest, ManifestSource.Network, DateTimeOffset.UtcNow));
    }

    private static string IndexJson(string flags, string name = "sounds/click.ogg") => $$"""
        { {{flags}} "objects": { "{{name}}": { "hash": "{{VersionTestData.HashA}}", "size": 5 } } }
        """;

    private MinecraftLayout NewLayout(bool withSource)
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        if (withSource)
        {
            var source = layout.AssetObjectPath(VersionTestData.HashA);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, new byte[5]);
        }
        return layout;
    }

    [Fact]
    public void ParseIndex_ReadsFlags()
    {
        var virt = AssetIndexParser.ParseIndex(IndexJson("\"virtual\": true,"));
        Assert.True(virt.IsVirtual);
        Assert.False(virt.MapToResources);

        var map = AssetIndexParser.ParseIndex(IndexJson("\"map_to_resources\": true,"));
        Assert.True(map.MapToResources);

        var modern = AssetIndexParser.ParseIndex(IndexJson(""));
        Assert.False(modern.IsVirtual);
        Assert.False(modern.MapToResources);
        Assert.Single(modern.Objects);
    }

    [Fact]
    public async Task Prepare_Virtual_CopiesToVirtualDirectory()
    {
        var layout = NewLayout(withSource: true);
        var index = AssetIndexParser.ParseIndex(IndexJson("\"virtual\": true,"));

        var target = await LegacyAssetsPreparer.PrepareAsync(layout, "legacy", index, Path.Combine(Dir, "inst"), default);

        Assert.Equal(layout.VirtualAssetsDirectory("legacy"), target);
        Assert.True(File.Exists(Path.Combine(target!, "sounds", "click.ogg")));
    }

    [Fact]
    public async Task Prepare_MapToResources_CopiesToGameDirectory()
    {
        var layout = NewLayout(withSource: true);
        var gameDir = Path.Combine(Dir, "inst");
        var index = AssetIndexParser.ParseIndex(IndexJson("\"map_to_resources\": true,"));

        var target = await LegacyAssetsPreparer.PrepareAsync(layout, "pre-1.6", index, gameDir, default);

        Assert.Equal(Path.Combine(gameDir, "resources"), target);
        Assert.True(File.Exists(Path.Combine(target!, "sounds", "click.ogg")));
    }

    [Fact]
    public async Task Prepare_Modern_ReturnsNullAndCopiesNothing()
    {
        var layout = NewLayout(withSource: true);
        var index = AssetIndexParser.ParseIndex(IndexJson(""));

        var target = await LegacyAssetsPreparer.PrepareAsync(layout, "17", index, Path.Combine(Dir, "inst"), default);

        Assert.Null(target);
        Assert.False(Directory.Exists(layout.VirtualAssetsDirectory("17")));
    }

    [Fact]
    public async Task Prepare_MissingSource_ThrowsLaunchException()
    {
        var layout = NewLayout(withSource: false);
        var index = AssetIndexParser.ParseIndex(IndexJson("\"virtual\": true,"));

        await Assert.ThrowsAsync<LaunchException>(() =>
            LegacyAssetsPreparer.PrepareAsync(layout, "legacy", index, Path.Combine(Dir, "inst"), default));
    }

    [Fact]
    public async Task Prepare_NameWithTraversal_IsRejected()
    {
        var layout = NewLayout(withSource: true);
        var index = AssetIndexParser.ParseIndex(IndexJson("\"virtual\": true,", "../../evil.txt"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            LegacyAssetsPreparer.PrepareAsync(layout, "legacy", index, Path.Combine(Dir, "inst"), default));
        Assert.False(File.Exists(Path.Combine(Dir, "evil.txt")));
    }

    [Fact]
    public void VersionDetailsParser_ReadsLoggingConfig()
    {
        var details = VersionDetailsParser.Parse(LaunchTestData.VersionJson);

        Assert.NotNull(details.Logging);
        Assert.Equal("client-1.12.xml", details.Logging!.FileId);
        Assert.Equal("-Dlog4j.configurationFile=${path}", details.Logging.Argument);
        Assert.Equal(888, details.Logging.Size);
    }

    [Fact]
    public void VersionDetailsParser_NoLogging_IsNull()
    {
        Assert.Null(VersionDetailsParser.Parse(VersionTestData.VersionJson).Logging);
    }

    [Fact]
    public async Task Install_DownloadsLogConfig()
    {
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                using (var w = new StreamWriter(z.CreateEntry("lwjgl.dll").Open())) w.Write("x");
            zip = ms.ToArray();
        }
        var downloader = new FakeDownloader()
            .Serve(VersionTestData.VersionJsonUrl, LaunchTestData.VersionJson)
            .Serve(VersionTestData.AssetIndexUrl, VersionTestData.AssetIndexJson)
            .Serve(VersionTestData.PlatformNativesWindowsUrl, zip);
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var manifest = VersionManifestParser.Parse(TestData.ManifestJson);
        var installer = new MinecraftInstaller(new FixedManifest(manifest), downloader, layout, Windows);

        var result = await installer.InstallAsync("1.21.1", new InstallOptions(), null, default);

        Assert.Equal(8, result.FilesTotal);
        Assert.True(File.Exists(layout.LogConfigPath("client-1.12.xml")));
    }
}
