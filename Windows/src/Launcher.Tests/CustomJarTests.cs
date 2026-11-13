using System.IO.Compression;
using Launcher.Core.Custom;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Versions;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class CustomJarToolsTests : TempDirectoryTest
{
    private const string BaseJson = """
    {
      "id": "1.21.1", "type": "release", "mainClass": "net.minecraft.client.main.Main",
      "assetIndex": { "id": "17", "url": "https://example.com/17.json", "sha1": "aa", "size": 1 },
      "downloads": { "client": { "url": "https://example.com/client.jar", "sha1": "bb", "size": 123 } },
      "libraries": [],
      "arguments": { "jvm": ["-cp", "${classpath}"], "game": ["--version", "${version_name}"] }
    }
    """;

    private string MakeJar(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(Dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(content);
        }
        return path;
    }

    [Fact]
    public void VersionId_RoundTripsThroughLabel()
    {
        var id = CustomJarTools.MakeVersionId("my-client", "1.21.1");

        Assert.Equal("my-client+mc1.21.1", id);
        var label = VersionLabel.Parse(id);
        Assert.Equal(LoaderKind.Custom, label.Loader);
        Assert.Equal("1.21.1", label.GameVersion);
        Assert.Equal("my-client", label.LoaderVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("..\\evil")]
    [InlineData("-start")]
    public void BadNames_AreRejected(string name) =>
        Assert.Throws<CustomJarException>(() => CustomJarTools.MakeVersionId(name, "1.21.1"));

    [Fact]
    public void Inspect_RecognizesClientAndReadsVersionJson()
    {
        var jar = MakeJar("client.jar",
            ("net/minecraft/client/main/Main.class", "x"),
            ("version.json", "{\"id\":\"1.21.1\",\"name\":\"1.21.1\"}"));

        var result = CustomJarTools.Inspect(jar, "net.minecraft.client.main.Main");

        Assert.True(result.LooksLikeClient);
        Assert.True(result.HasMainClass);
        Assert.Equal("1.21.1", result.DetectedVersion);
    }

    [Fact]
    public void Inspect_JarWithoutClasses_Throws()
    {
        var jar = MakeJar("empty.jar", ("readme.txt", "hi"));
        Assert.Throws<CustomJarException>(() => CustomJarTools.Inspect(jar));
    }

    [Fact]
    public void Inspect_NotAZip_Throws()
    {
        var path = Path.Combine(Dir, "fake.jar");
        File.WriteAllText(path, "this is not a zip");
        Assert.Throws<CustomJarException>(() => CustomJarTools.Inspect(path));
    }

    [Fact]
    public void Inspect_MissingMainClass_IsReportedButNotFatal()
    {
        var jar = MakeJar("other.jar", ("com/example/Other.class", "x"));
        var result = CustomJarTools.Inspect(jar, "net.minecraft.client.main.Main");
        Assert.False(result.HasMainClass);
        Assert.False(result.LooksLikeClient);
    }

    [Fact]
    public void OverlayJson_ParsesAndUsesOwnJar()
    {
        var json = CustomJarTools.BuildOverlayJson("my+mc1.21.1", "1.21.1", "net.minecraft.client.main.Main");
        var overlay = VersionDetailsParser.ParseOverlay(json);

        Assert.True(overlay.UsesOwnJar);
        Assert.Equal("1.21.1", overlay.InheritsFrom);
        Assert.Equal("net.minecraft.client.main.Main", overlay.MainClass);
        Assert.Empty(Launcher.Core.GameLaunch.LaunchArgumentsParser.Parse(json).Jvm); // аргументы читаются без ошибок
    }

    [Fact]
    public void Merge_WithOwnJar_UsesOverlayJarAndSkipsSizeCheck()
    {
        var parent = VersionDetailsParser.Parse(BaseJson);
        var overlay = VersionDetailsParser.ParseOverlay(
            CustomJarTools.BuildOverlayJson("my+mc1.21.1", "1.21.1", parent.MainClass));

        var merged = VersionMerger.Merge(parent, overlay);

        Assert.Equal("my+mc1.21.1", merged.ClientJarId);
        Assert.Null(merged.ClientDownload.Size);
        Assert.Null(merged.ClientDownload.Sha1);
    }

    [Fact]
    public void Merge_WithoutOwnJar_KeepsParentClient()
    {
        var parent = VersionDetailsParser.Parse(BaseJson);
        var overlay = VersionDetailsParser.ParseOverlay("""
        { "id": "fabric-loader-0.16.0-1.21.1", "inheritsFrom": "1.21.1", "mainClass": "x.Y", "libraries": [] }
        """);

        var merged = VersionMerger.Merge(parent, overlay);

        Assert.Equal("1.21.1", merged.ClientJarId);
        Assert.Equal(123, merged.ClientDownload.Size);
    }

    [Fact]
    public void NormalizeUserJson_WithInheritance_MarksOwnJarAndFillsMainClass()
    {
        var json = CustomJarTools.NormalizeUserJson(
            "{\"id\":\"old\",\"inheritsFrom\":\"1.21.1\"}", "mine+mc1.21.1", "base.Main", out var inherits, out _);

        Assert.Equal("1.21.1", inherits);
        var overlay = VersionDetailsParser.ParseOverlay(json);
        Assert.Equal("mine+mc1.21.1", overlay.Id);
        Assert.Equal("base.Main", overlay.MainClass);
        Assert.True(overlay.UsesOwnJar);
        Launcher.Core.GameLaunch.LaunchArgumentsParser.Parse(json); // пустые аргументы добавлены
    }

    [Fact]
    public void NormalizeUserJson_Standalone_ReplacesClientDownload()
    {
        var json = CustomJarTools.NormalizeUserJson(BaseJson, "mine+mc1.21.1", null, out var inherits, out var assets);

        Assert.Null(inherits);
        Assert.Equal("17", assets);
        var details = VersionDetailsParser.Parse(json);
        Assert.Equal("mine+mc1.21.1", details.Id);
        Assert.Null(details.ClientDownload.Size);
        Assert.Null(details.ClientDownload.Sha1);
    }

    [Fact]
    public void NormalizeUserJson_StandaloneWithoutAssetIndex_Throws() =>
        Assert.Throws<CustomJarException>(() =>
            CustomJarTools.NormalizeUserJson("{\"id\":\"x\",\"mainClass\":\"a.B\"}", "x+mc1", null, out _, out _));

    [Fact]
    public void NormalizeUserJson_NotJson_Throws() =>
        Assert.Throws<CustomJarException>(() =>
            CustomJarTools.NormalizeUserJson("{ nope", "x+mc1", null, out _, out _));

    // ---------- установка целиком (с поддельным установщиком базовой версии) ----------

    private sealed class FakeVanilla : IMinecraftInstaller, ILocalVersionInstaller
    {
        private readonly MinecraftLayout _layout;
        public List<string> Installed { get; } = new();
        public FakeVanilla(MinecraftLayout layout) => _layout = layout;

        public Task<InstallResult> InstallAsync(string versionId, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct)
        {
            Installed.Add(versionId);
            var path = _layout.VersionJsonPath(versionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BaseJson.Replace("1.21.1", versionId));
            return Task.FromResult(new InstallResult(versionId, path, _layout.ClientJarPath(versionId), "", 0, 0, 0, 0));
        }

        public Task<InstallResult> InstallLocalAsync(string versionId, string versionJson, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct)
        {
            Installed.Add("local:" + versionId);
            var path = _layout.VersionJsonPath(versionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, versionJson);
            return Task.FromResult(new InstallResult(versionId, path, _layout.ClientJarPath(versionId), "", 0, 0, 0, 0));
        }
    }

    [Fact]
    public async Task Install_JarPlusBaseVersion_WritesOverlayAndJar()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var fake = new FakeVanilla(layout);
        var jar = MakeJar("client.jar", ("net/minecraft/client/main/Main.class", "x"));

        var result = await new CustomJarInstaller(fake, fake, layout)
            .InstallAsync(new CustomJarRequest(jar, "mine", "1.21.1", null), new InstallOptions(), null, default);

        Assert.Equal("mine+mc1.21.1", result.VersionId);
        Assert.Contains("1.21.1", fake.Installed);
        Assert.True(File.Exists(layout.ClientJarPath(result.VersionId)));

        var details = await new InstalledVersionRepository(layout).LoadAsync(result.VersionId, default);
        Assert.Equal(result.VersionId, details.ClientJarId);
        Assert.Equal("net.minecraft.client.main.Main", details.MainClass);
    }

    [Fact]
    public async Task Install_JarWithoutBaseVersion_AsksForIt()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var fake = new FakeVanilla(layout);
        var jar = MakeJar("client.jar", ("net/minecraft/client/main/Main.class", "x"));

        var ex = await Assert.ThrowsAsync<CustomJarException>(() => new CustomJarInstaller(fake, fake, layout)
            .InstallAsync(new CustomJarRequest(jar, "mine", null, null), new InstallOptions(), null, default));

        Assert.Contains("версию Minecraft", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(layout.VersionsDirectory, "mine+mc")));
    }

    [Fact]
    public async Task Install_UserJsonWithInheritance_UsesBaseFromJson()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var fake = new FakeVanilla(layout);
        var jar = MakeJar("client.jar", ("net/minecraft/client/main/Main.class", "x"));
        var jsonPath = Path.Combine(Dir, "client.json");
        File.WriteAllText(jsonPath, "{\"id\":\"vendor-client\",\"inheritsFrom\":\"1.21.1\",\"mainClass\":\"net.minecraft.client.main.Main\"}");

        var result = await new CustomJarInstaller(fake, fake, layout)
            .InstallAsync(new CustomJarRequest(jar, "vendor", null, jsonPath), new InstallOptions(), null, default);

        Assert.Equal("vendor+mc1.21.1", result.VersionId);
        Assert.True(result.UsedOwnJson);
        var details = await new InstalledVersionRepository(layout).LoadAsync(result.VersionId, default);
        Assert.Equal(result.VersionId, details.ClientJarId);
    }

    [Fact]
    public async Task Install_UserJsonStandalone_GoesThroughLocalInstaller()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var fake = new FakeVanilla(layout);
        var jar = MakeJar("client.jar", ("net/minecraft/client/main/Main.class", "x"));
        var jsonPath = Path.Combine(Dir, "client.json");
        File.WriteAllText(jsonPath, BaseJson);

        var result = await new CustomJarInstaller(fake, fake, layout)
            .InstallAsync(new CustomJarRequest(jar, "full", "1.21.1", jsonPath), new InstallOptions(), null, default);

        Assert.Contains("local:full+mc1.21.1", fake.Installed);
        Assert.True(File.Exists(layout.ClientJarPath(result.VersionId)));
    }

    [Fact]
    public async Task Install_SameVersionTwice_Fails()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var fake = new FakeVanilla(layout);
        var jar = MakeJar("client.jar", ("net/minecraft/client/main/Main.class", "x"));
        var installer = new CustomJarInstaller(fake, fake, layout);
        var request = new CustomJarRequest(jar, "dup", "1.21.1", null);

        await installer.InstallAsync(request, new InstallOptions(), null, default);

        await Assert.ThrowsAsync<CustomJarException>(() => installer.InstallAsync(request, new InstallOptions(), null, default));
    }
}
