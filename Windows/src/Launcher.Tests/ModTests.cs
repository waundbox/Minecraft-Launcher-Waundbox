using System.IO.Compression;
using System.Net;
using System.Text;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Modpacks;
using Launcher.Core.Mods;
using Launcher.Core.Versions;
using Launcher.Infrastructure.Mods;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class ModTests : TempDirectoryTest
{
    private const string VersionsJson = """
    [
      { "id": "v-beta", "project_id": "P1", "name": "Beta", "version_number": "2.0.0-beta", "version_type": "beta",
        "date_published": "2025-03-01T10:00:00Z", "status": "listed", "game_versions": ["1.20.1"], "loaders": ["fabric"],
        "files": [ { "url": "https://cdn.modrinth.com/data/P1/beta.jar", "filename": "beta.jar", "primary": true, "size": 10, "hashes": { "sha1": "aa", "sha512": "bb" } } ],
        "dependencies": [] },
      { "id": "v-rel", "project_id": "P1", "name": "Release", "version_number": "1.9.0", "version_type": "release",
        "date_published": "2025-01-01T10:00:00Z", "status": "listed", "game_versions": ["1.20.1"], "loaders": ["fabric"],
        "files": [
          { "url": "https://cdn.modrinth.com/data/P1/sources.jar", "filename": "p1-sources.jar", "primary": false, "size": 5, "hashes": { "sha1": "s1" } },
          { "url": "https://cdn.modrinth.com/data/P1/p1.jar", "filename": "p1.jar", "primary": true, "size": 20, "hashes": { "sha1": "c1", "sha512": "c2" } } ],
        "dependencies": [
          { "project_id": "DEP", "version_id": null, "dependency_type": "required" },
          { "project_id": "OPT", "version_id": null, "dependency_type": "optional" },
          { "project_id": "BAD", "version_id": null, "dependency_type": "incompatible" } ] },
      { "id": "v-draft", "project_id": "P1", "name": "Draft", "version_number": "3.0.0", "version_type": "release",
        "date_published": "2025-04-01T10:00:00Z", "status": "draft", "game_versions": ["1.20.1"], "loaders": ["fabric"],
        "files": [ { "url": "https://cdn.modrinth.com/data/P1/draft.jar", "filename": "draft.jar", "primary": true, "size": 1, "hashes": { "sha1": "dd" } } ],
        "dependencies": [] }
    ]
    """;

    [Fact]
    public void Parser_Versions_PicksPrimaryFile_SkipsDrafts_ReadsDependencies()
    {
        var versions = ModrinthParser.ParseVersions(VersionsJson);

        Assert.Equal(2, versions.Count);
        var release = versions.Single(v => v.Id == "v-rel");
        Assert.Equal("p1.jar", release.File.FileName);
        Assert.Equal("c1", release.File.Sha1);
        Assert.Equal(3, release.Dependencies.Count);
        Assert.Equal(ModDependencyKind.Required, release.Dependencies[0].Kind);
        Assert.Equal(ModReleaseChannel.Beta, versions.Single(v => v.Id == "v-beta").Channel);
    }

    [Fact]
    public void Selector_PrefersReleaseOverNewerBeta()
    {
        var best = ModVersionSelector.PickBest(ModrinthParser.ParseVersions(VersionsJson));
        Assert.Equal("v-rel", best!.Id);
    }

    [Fact]
    public void Parser_Search_ReadsHitsAndTotal()
    {
        const string json = """
        { "hits": [ { "project_id": "AABB", "slug": "sodium", "title": "Sodium", "description": "fast", "author": "jelly",
                      "icon_url": "https://cdn.modrinth.com/i.png", "downloads": 12345, "project_type": "mod" } ],
          "offset": 0, "limit": 20, "total_hits": 77 }
        """;
        var result = ModrinthParser.ParseSearch(json);

        Assert.Equal(77, result.TotalHits);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("AABB", hit.ProjectId);
        Assert.Equal(ModContentType.Mod, hit.Type);
        Assert.Equal(12345, hit.Downloads);
    }

    [Fact]
    public void Parser_BrokenJson_ThrowsModException() =>
        Assert.Throws<ModException>(() => ModrinthParser.ParseSearch("not json"));

    // ---- HTTP-клиент: адреса запросов

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Http_Search_BuildsFacetsForModLoader()
    {
        var handler = new CapturingHandler("""{ "hits": [], "total_hits": 0 }""");
        var client = new HttpModrinthClient(new HttpClient(handler));

        await client.SearchAsync(new ModSearchQuery("sodium", ModContentType.Mod, "1.20.1", "fabric", ModSort.Downloads), default);

        var url = Uri.UnescapeDataString(handler.Requests.Single().ToString());
        Assert.StartsWith("https://api.modrinth.com/v2/search?query=sodium", url);
        Assert.Contains("[[\"project_type:mod\"],[\"versions:1.20.1\"],[\"categories:fabric\"]]", url);
        Assert.Contains("index=downloads", url);
    }

    [Fact]
    public async Task Http_Search_ShaderHasNoLoaderFacet()
    {
        var handler = new CapturingHandler("""{ "hits": [], "total_hits": 0 }""");
        var client = new HttpModrinthClient(new HttpClient(handler));

        await client.SearchAsync(new ModSearchQuery("", ModContentType.Shader, "1.21.1", "fabric"), default);

        var url = Uri.UnescapeDataString(handler.Requests.Single().ToString());
        Assert.Contains("project_type:shader", url);
        Assert.DoesNotContain("categories:", url);
    }

    [Fact]
    public async Task Http_Versions_PassesGameVersionAndLoaderAsJsonArrays()
    {
        var handler = new CapturingHandler("[]");
        var client = new HttpModrinthClient(new HttpClient(handler));

        await client.GetVersionsAsync("P1", "1.20.1", "fabric", default);

        var url = Uri.UnescapeDataString(handler.Requests.Single().ToString());
        Assert.Contains("project/P1/version?game_versions=[\"1.20.1\"]&loaders=[\"fabric\"]", url);
        Assert.Contains("include_changelog=false", url);
    }

    // ---- установка

    private sealed class FakeModrinth : IModrinthClient
    {
        public Dictionary<string, List<ModVersion>> Versions { get; } = new();
        public List<ModProject> Projects { get; } = new();

        public Task<ModSearchResult> SearchAsync(ModSearchQuery query, CancellationToken ct) =>
            Task.FromResult(new ModSearchResult(Projects, Projects.Count));

        public Task<IReadOnlyList<ModVersion>> GetVersionsAsync(string projectId, string gameVersion, string? loader, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModVersion>>(Versions.TryGetValue(projectId, out var v) ? v : []);

        public Task<ModVersion?> GetVersionAsync(string versionId, CancellationToken ct) =>
            Task.FromResult(Versions.Values.SelectMany(x => x).FirstOrDefault(v => v.Id == versionId));

        public Task<IReadOnlyList<ModProject>> GetProjectsAsync(IReadOnlyCollection<string> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModProject>>(Projects.Where(p => ids.Contains(p.ProjectId)).ToList());
    }

    private static ModVersion Version(
        string id, string project, string file, DateTimeOffset? published = null, string url = "https://cdn.modrinth.com/data/x/f.jar",
        string? sha1 = "abc", params ModDependency[] deps) =>
        new(id, project, file, "1.0", ModReleaseChannel.Release, published ?? DateTimeOffset.Parse("2025-01-01T00:00:00Z"),
            ["1.20.1"], ["fabric"], new ModFile(new Uri(url), file, sha1, null, 10), deps);

    private static readonly VersionLabel FabricLabel = VersionLabel.Parse("fabric-loader-0.15.7-1.20.1");

    private (ModManager Manager, FakeModrinth Client, FakeDownloader Downloader, MinecraftLayout Layout) Create()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var client = new FakeModrinth();
        var downloader = new FakeDownloader();
        return (new ModManager(client, downloader, layout), client, downloader, layout);
    }

    private static ModProject Project(string id, string title) => new(id, null, title, "", null, null, 0, ModContentType.Mod);

    [Fact]
    public async Task Install_PutsFileInModsFolder_AndInstallsRequiredDependency()
    {
        var (manager, client, _, layout) = Create();
        client.Projects.Add(Project("P1", "Main mod"));
        client.Projects.Add(Project("DEP", "Fabric API"));
        client.Versions["P1"] = [Version("v1", "P1", "main.jar", deps:
            [new ModDependency("DEP", null, ModDependencyKind.Required), new ModDependency("OPT", null, ModDependencyKind.Optional)])];
        client.Versions["DEP"] = [Version("d1", "DEP", "fabric-api.jar")];

        var result = await manager.InstallAsync("Main", FabricLabel, Project("P1", "Main mod"), ModContentType.Mod, null, default);

        var mods = Path.Combine(layout.InstanceDirectory("Main"), "mods");
        Assert.True(File.Exists(Path.Combine(mods, "main.jar")));
        Assert.True(File.Exists(Path.Combine(mods, "fabric-api.jar")));
        Assert.Equal(2, result.Installed.Count);
        Assert.False(result.Installed.Single(i => i.ProjectId == "P1").AutoInstalled);
        var dep = result.Installed.Single(i => i.ProjectId == "DEP");
        Assert.True(dep.AutoInstalled);
        Assert.Equal("Fabric API", dep.Title);
        Assert.Equal(2, manager.GetInstalled("Main").Count);
    }

    [Fact]
    public async Task Install_SkipsDependencyThatIsAlreadyInstalled()
    {
        var (manager, client, downloader, _) = Create();
        client.Versions["DEP"] = [Version("d1", "DEP", "fabric-api.jar")];
        client.Versions["P1"] = [Version("v1", "P1", "a.jar", deps: [new ModDependency("DEP", null, ModDependencyKind.Required)])];
        client.Versions["P2"] = [Version("v2", "P2", "b.jar", deps: [new ModDependency("DEP", null, ModDependencyKind.Required)])];

        await manager.InstallAsync("Main", FabricLabel, Project("P1", "A"), ModContentType.Mod, null, default);
        downloader.Requests.Clear();
        await manager.InstallAsync("Main", FabricLabel, Project("P2", "B"), ModContentType.Mod, null, default);

        Assert.Single(downloader.Requests); // только b.jar: зависимость уже на месте
    }

    [Fact]
    public async Task Install_NoCompatibleVersion_SaysSo()
    {
        var (manager, _, _, _) = Create();

        var ex = await Assert.ThrowsAsync<ModException>(() =>
            manager.InstallAsync("Main", FabricLabel, Project("NONE", "Ghost"), ModContentType.Mod, null, default));

        Assert.Contains("Ghost", ex.Message);
        Assert.Contains("1.20.1", ex.Message);
    }

    [Fact]
    public async Task Install_ModIntoVanillaProfile_IsRefused()
    {
        var (manager, client, _, _) = Create();
        client.Versions["P1"] = [Version("v1", "P1", "a.jar")];

        await Assert.ThrowsAsync<ModException>(() =>
            manager.InstallAsync("Main", VersionLabel.Parse("1.20.1"), Project("P1", "A"), ModContentType.Mod, null, default));
    }

    [Fact]
    public async Task Install_ResourcePackIntoVanillaProfile_GoesToResourcepacks()
    {
        var (manager, client, _, layout) = Create();
        client.Versions["RP"] = [Version("r1", "RP", "pack.zip", url: "https://cdn.modrinth.com/data/RP/pack.zip")];

        await manager.InstallAsync("Main", VersionLabel.Parse("1.20.1"),
            new ModProject("RP", null, "Pack", "", null, null, 0, ModContentType.ResourcePack), ModContentType.ResourcePack, null, default);

        Assert.True(File.Exists(Path.Combine(layout.InstanceDirectory("Main"), "resourcepacks", "pack.zip")));
    }

    [Theory]
    [InlineData("https://evil.example.com/a.jar", "abc", "a.jar")]
    [InlineData("http://cdn.modrinth.com/a.jar", "abc", "a.jar")]
    [InlineData("https://cdn.modrinth.com/a.jar", null, "a.jar")]
    [InlineData("https://cdn.modrinth.com/a.jar", "abc", "..\\evil.jar")]
    [InlineData("https://notmodrinth.com/a.jar", "abc", "a.jar")]
    public async Task Install_RejectsUntrustedFiles(string url, string? sha1, string fileName)
    {
        var (manager, client, downloader, _) = Create();
        client.Versions["P1"] = [Version("v1", "P1", fileName, url: url, sha1: sha1)];

        await Assert.ThrowsAsync<ModException>(() =>
            manager.InstallAsync("Main", FabricLabel, Project("P1", "A"), ModContentType.Mod, null, default));
        Assert.Empty(downloader.Requests);
    }

    [Fact]
    public async Task Install_DoesNotOverwriteForeignFileWithSameName()
    {
        var (manager, client, _, layout) = Create();
        var mods = Path.Combine(layout.InstanceDirectory("Main"), "mods");
        Directory.CreateDirectory(mods);
        await File.WriteAllTextAsync(Path.Combine(mods, "a.jar"), "my own file");
        client.Versions["P1"] = [Version("v1", "P1", "a.jar")];

        await Assert.ThrowsAsync<ModException>(() =>
            manager.InstallAsync("Main", FabricLabel, Project("P1", "A"), ModContentType.Mod, null, default));
        Assert.Equal("my own file", await File.ReadAllTextAsync(Path.Combine(mods, "a.jar")));
    }

    [Fact]
    public async Task Remove_DeletesFileAndRecord()
    {
        var (manager, client, _, layout) = Create();
        client.Versions["P1"] = [Version("v1", "P1", "a.jar")];
        var result = await manager.InstallAsync("Main", FabricLabel, Project("P1", "A"), ModContentType.Mod, null, default);

        manager.Remove("Main", result.Installed[0]);

        Assert.False(File.Exists(Path.Combine(layout.InstanceDirectory("Main"), "mods", "a.jar")));
        Assert.Empty(manager.GetInstalled("Main"));
    }

    [Fact]
    public async Task GetInstalled_ForgetsFilesDeletedByHand()
    {
        var (manager, client, _, layout) = Create();
        client.Versions["P1"] = [Version("v1", "P1", "a.jar")];
        await manager.InstallAsync("Main", FabricLabel, Project("P1", "A"), ModContentType.Mod, null, default);

        File.Delete(Path.Combine(layout.InstanceDirectory("Main"), "mods", "a.jar"));

        Assert.Empty(manager.GetInstalled("Main"));
    }

    [Fact]
    public async Task Updates_FoundAndApplied_OldFileReplaced()
    {
        var (manager, client, _, layout) = Create();
        var old = Version("v1", "P1", "a-1.jar", DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        client.Versions["P1"] = [old];
        await manager.InstallAsync("Main", FabricLabel, Project("P1", "A"), ModContentType.Mod, null, default);

        Assert.Empty(await manager.CheckUpdatesAsync("Main", FabricLabel, default));

        client.Versions["P1"] = [old, Version("v2", "P1", "a-2.jar", DateTimeOffset.Parse("2025-06-01T00:00:00Z"))];
        var updates = await manager.CheckUpdatesAsync("Main", FabricLabel, default);
        var update = Assert.Single(updates);
        Assert.Equal("v2", update.Available.Id);

        await manager.UpdateAsync("Main", FabricLabel, update, null, default);

        var mods = Path.Combine(layout.InstanceDirectory("Main"), "mods");
        Assert.True(File.Exists(Path.Combine(mods, "a-2.jar")));
        Assert.False(File.Exists(Path.Combine(mods, "a-1.jar")));
        Assert.Equal("v2", Assert.Single(manager.GetInstalled("Main")).VersionId);
    }

    [Fact]
    public void Recommended_FiltersByLoaderAndType()
    {
        var fabricMods = RecommendedContent.For(ModContentType.Mod, LoaderKind.Fabric);
        Assert.Contains(fabricMods, i => i.Slug == "sodium");
        Assert.DoesNotContain(fabricMods, i => i.Slug == "embeddium");
        Assert.All(RecommendedContent.For(ModContentType.Shader, LoaderKind.Vanilla), i => Assert.Equal(ModContentType.Shader, i.Type));
    }

    // ---- сборки .mrpack

    private string MakePack(string indexJson, params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".mrpack");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        Add("modrinth.index.json", indexJson);
        foreach (var (name, content) in entries) Add(name, content);
        return path;
    }

    private const string GoodIndex = """
    {
      "formatVersion": 1, "game": "minecraft", "versionId": "1.0.0", "name": "Cool Pack", "summary": "fun",
      "files": [
        { "path": "mods/sodium.jar", "hashes": { "sha1": "aa", "sha512": "bb" }, "env": { "client": "required", "server": "required" },
          "downloads": [ "https://cdn.modrinth.com/data/x/sodium.jar" ], "fileSize": 100 },
        { "path": "mods/server-only.jar", "hashes": { "sha1": "cc", "sha512": "dd" }, "env": { "client": "unsupported", "server": "required" },
          "downloads": [ "https://cdn.modrinth.com/data/x/server.jar" ], "fileSize": 100 }
      ],
      "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.7" }
    }
    """;

    [Fact]
    public void Mrpack_Read_GivesGameLoaderAndClientFiles()
    {
        var info = MrpackReader.Read(MakePack(GoodIndex));

        Assert.Equal("Cool Pack", info.Name);
        Assert.Equal("1.20.1", info.GameVersion);
        Assert.Equal(LoaderKind.Fabric, info.Loader);
        Assert.Equal("0.15.7", info.LoaderVersion);
        Assert.Equal("mods/sodium.jar", Assert.Single(info.Files).Path);
        Assert.Equal(1, info.SkippedServerOnlyFiles);
    }

    [Theory]
    [InlineData("""{ "formatVersion": 2, "game": "minecraft", "name": "x", "files": [], "dependencies": { "minecraft": "1.20.1" } }""")]
    [InlineData("""{ "formatVersion": 1, "game": "other", "name": "x", "files": [], "dependencies": { "minecraft": "1.20.1" } }""")]
    [InlineData("""{ "formatVersion": 1, "game": "minecraft", "name": "x", "files": [ { "path": "../evil.jar", "hashes": { "sha1": "a" }, "downloads": ["https://cdn.modrinth.com/a"] } ], "dependencies": { "minecraft": "1.20.1" } }""")]
    [InlineData("""{ "formatVersion": 1, "game": "minecraft", "name": "x", "files": [ { "path": "mods/a.jar", "hashes": {}, "downloads": ["https://cdn.modrinth.com/a"] } ], "dependencies": { "minecraft": "1.20.1" } }""")]
    public void Mrpack_Read_RejectsUnsupportedOrUnsafe(string index) =>
        Assert.Throws<ModException>(() => MrpackReader.Read(MakePack(index)));

    [Fact]
    public void Mrpack_Read_QuiltDependency_IsRecognized()
    {
        var info = MrpackReader.Read(MakePack(
            """{ "formatVersion": 1, "game": "minecraft", "name": "x", "files": [], "dependencies": { "minecraft": "1.20.1", "quilt-loader": "0.26.0" } }"""));
        Assert.Equal(LoaderKind.Quilt, info.Loader);
        Assert.Equal("0.26.0", info.LoaderVersion);
    }

    [Fact]
    public void Mrpack_Read_NeoForgeDependency_IsRecognized()
    {
        var info = MrpackReader.Read(MakePack(
            """{ "formatVersion": 1, "game": "minecraft", "name": "x", "files": [], "dependencies": { "minecraft": "1.21.1", "neoforge": "21.1.172" } }"""));
        Assert.Equal(LoaderKind.NeoForge, info.Loader);
        Assert.Equal("21.1.172", info.LoaderVersion);
    }

    [Fact]
    public void Mrpack_Read_NotAModpack_Throws()
    {
        var path = Path.Combine(Dir, "x.mrpack");
        File.WriteAllText(path, "this is not a zip");
        Assert.Throws<ModException>(() => MrpackReader.Read(path));
    }

    [Fact]
    public async Task Mrpack_Import_DownloadsFiles_ExtractsOverrides_ClientOverridesWin()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var downloader = new FakeDownloader();
        var importer = new MrpackImporter(downloader, layout);
        var pack = MakePack(GoodIndex,
            ("overrides/config/a.toml", "common"),
            ("overrides/config/b.toml", "common-b"),
            ("client-overrides/config/a.toml", "client"),
            ("overrides/tool.exe", "nope"));

        var result = await importer.ImportAsync("Cool", pack, MrpackReader.Read(pack), null, default);

        var instance = layout.InstanceDirectory("Cool");
        Assert.True(File.Exists(Path.Combine(instance, "mods", "sodium.jar")));
        Assert.False(File.Exists(Path.Combine(instance, "mods", "server-only.jar")));
        Assert.Equal("client", await File.ReadAllTextAsync(Path.Combine(instance, "config", "a.toml")));
        Assert.Equal("common-b", await File.ReadAllTextAsync(Path.Combine(instance, "config", "b.toml")));
        Assert.False(File.Exists(Path.Combine(instance, "tool.exe")));
        Assert.Equal(1, result.FilesDownloaded);
        Assert.Contains(result.Warnings, w => w.Contains("tool.exe"));
    }

    [Fact]
    public async Task Mrpack_Import_RejectsEvilOverridePath()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var importer = new MrpackImporter(new FakeDownloader(), layout);
        var pack = MakePack(GoodIndex, ("overrides/../../escape.txt", "x"));

        await Assert.ThrowsAsync<ModException>(() => importer.ImportAsync("Cool", pack, MrpackReader.Read(pack), null, default));
        Assert.False(File.Exists(Path.Combine(Dir, "escape.txt")));
    }

    [Fact]
    public async Task Mrpack_Import_RejectsDownloadFromUnlistedHost()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        var downloader = new FakeDownloader();
        var importer = new MrpackImporter(downloader, layout);
        var pack = MakePack(GoodIndex.Replace("cdn.modrinth.com/data/x/sodium.jar", "evil.example.com/sodium.jar"));

        await Assert.ThrowsAsync<ModException>(() => importer.ImportAsync("Cool", pack, MrpackReader.Read(pack), null, default));
        Assert.Empty(downloader.Requests);
    }

    [Theory]
    [InlineData("mods/a.jar", true)]
    [InlineData("config\\sub\\a.toml", true)]
    [InlineData("../a.jar", false)]
    [InlineData("mods/../../a.jar", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("\\windows\\x", false)]
    [InlineData("C:/x.jar", false)]
    [InlineData("", false)]
    public void SafePath_Resolve(string relative, bool ok)
    {
        var result = SafePath.Resolve(Path.Combine(Dir, "inst"), relative);
        Assert.Equal(ok, result is not null);
        if (result is not null) Assert.StartsWith(Path.GetFullPath(Path.Combine(Dir, "inst")), result);
    }
}
