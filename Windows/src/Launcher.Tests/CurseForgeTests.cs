using System.IO.Compression;
using System.Net;
using System.Text;
using Launcher.Core.Errors;
using Launcher.Core.Modpacks;
using Launcher.Core.Versions;
using Launcher.Infrastructure.Mods;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class CurseForgeTests : TempDirectoryTest
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private const string ManifestJson = """
        { "manifestType": "minecraftModpack", "manifestVersion": 1, "name": "The Winter Rescue", "version": "0.6.0-rc8",
          "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "forge-47.4.0", "primary": true } ] },
          "files": [ { "projectID": 10, "fileID": 100, "required": true }, { "projectID": 20, "fileID": 200, "required": true },
                     { "projectID": 30, "fileID": 300, "required": true }, { "projectID": 40, "fileID": 400, "required": false } ],
          "overrides": "overrides" }
        """;

    private string MakeZip(string manifest, params (string Name, string Text)[] extra)
    {
        var path = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        Add("manifest.json", manifest);
        foreach (var (n, t) in extra) Add(n, t);
        return path;
    }

    private sealed class FakeClient(params CurseForgeFile[] files) : ICurseForgeClient
    {
        public List<IReadOnlyList<long>> Calls { get; } = new();

        public Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> fileIds, CancellationToken ct)
        {
            Calls.Add(fileIds);
            return Task.FromResult<IReadOnlyList<CurseForgeFile>>(files.Where(f => fileIds.Contains(f.Id)).ToList());
        }
    }

    private static CurseForgeFile File_(long id, string name, string? url, string? sha = Sha) =>
        new(id, id / 10, name, url is null ? null : new Uri(url), sha, 5);

    [Fact]
    public void Read_ParsesLoaderGameAndFiles()
    {
        var m = CurseForgePackReader.Read(MakeZip(ManifestJson));
        Assert.Equal("The Winter Rescue", m.Name);
        Assert.Equal("1.20.1", m.GameVersion);
        Assert.Equal(LoaderKind.Forge, m.Loader);
        Assert.Equal("47.4.0", m.LoaderVersion);
        Assert.Equal(4, m.Files.Count);
        Assert.False(m.Files[3].Required);
        Assert.Equal("overrides", m.OverridesFolder);
    }

    [Theory]
    [InlineData("fabric-0.15.7", LoaderKind.Fabric, "0.15.7")]
    [InlineData("neoforge-21.1.172", LoaderKind.NeoForge, "21.1.172")]
    [InlineData("quilt-0.26.0", LoaderKind.Quilt, "0.26.0")]
    public void Read_RecognizesOtherLoaders(string id, LoaderKind kind, string version)
    {
        var json = ManifestJson.Replace("forge-47.4.0", id);
        var m = CurseForgePackReader.Read(MakeZip(json));
        Assert.Equal(kind, m.Loader);
        Assert.Equal(version, m.LoaderVersion);
    }

    [Theory]
    [InlineData("""{ "manifestType": "minecraftModpack", "name": "x", "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "liteloader-1.0", "primary": true } ] }, "files": [] }""")]
    [InlineData("""{ "manifestType": "other", "name": "x", "minecraft": { "version": "1.20.1" }, "files": [] }""")]
    [InlineData("""{ "manifestType": "minecraftModpack", "name": "x", "minecraft": { "version": "1.20.1" }, "files": [], "overrides": "../evil" }""")]
    [InlineData("""{ "manifestType": "minecraftModpack", "name": "x", "minecraft": { "version": "1.20.1" }, "files": [ { "projectID": "a", "fileID": 1 } ] }""")]
    [InlineData("not json")]
    public void Read_BadInput_Throws(string json) =>
        Assert.Throws<ModException>(() => CurseForgePackReader.Read(MakeZip(json)));

    [Fact]
    public void Detect_DistinguishesFormats()
    {
        Assert.Equal(ModpackFormat.CurseForge, CurseForgePackReader.Detect(MakeZip(ManifestJson)));
        Assert.Equal(ModpackFormat.Modrinth, CurseForgePackReader.Detect(MakeZip(ManifestJson, ("modrinth.index.json", "{}"))));

        var empty = Path.Combine(Dir, "empty.zip");
        using (ZipFile.Open(empty, ZipArchiveMode.Create)) { }
        Assert.Equal(ModpackFormat.Unknown, CurseForgePackReader.Detect(empty));
    }

    [Fact]
    public async Task Resolve_BuildsFiles_AndListsManualOnes()
    {
        var manifest = CurseForgePackReader.Read(MakeZip(ManifestJson));
        var client = new FakeClient(
            File_(100, "alpha.jar", "https://edge.forgecdn.net/files/1/2/alpha.jar"),
            File_(200, "blocked.jar", null),
            File_(300, "pack.zip", "https://mediafilez.forgecdn.net/files/3/4/pack.zip"));

        var info = await CurseForgeResolver.ResolveAsync(manifest, client, default);

        Assert.Equal(2, info.Files.Count);
        Assert.Contains(info.Files, f => f.Path == "mods/alpha.jar" && f.Sha1 == Sha);
        Assert.Contains(info.Files, f => f.Path == "resourcepacks/pack.zip");
        Assert.Equal("overrides", Assert.Single(info.OverridesFolders!));
        Assert.Contains("edge.forgecdn.net", info.ExtraAllowedHosts!);
        Assert.Contains(info.Notes!, n => n.Contains("blocked.jar"));        // запрещено автором — вручную
        Assert.Contains(info.Notes!, n => n.Contains("400"));                // не найден на CurseForge
    }

    [Fact]
    public async Task Resolve_RejectsForeignHostsAndMissingHashes()
    {
        var manifest = CurseForgePackReader.Read(MakeZip(ManifestJson));
        var client = new FakeClient(
            File_(100, "a.jar", "https://evil.example.com/a.jar"),
            File_(200, "b.jar", "https://edge.forgecdn.net/b.jar", sha: null),
            File_(300, "c.jar", "http://edge.forgecdn.net/c.jar"));

        var info = await CurseForgeResolver.ResolveAsync(manifest, client, default);

        Assert.Empty(info.Files);
    }

    [Fact]
    public async Task Resolve_UnsafeFileName_Throws()
    {
        var manifest = CurseForgePackReader.Read(MakeZip(ManifestJson));
        var client = new FakeClient(File_(100, "../../evil.jar", "https://edge.forgecdn.net/x.jar"));
        await Assert.ThrowsAsync<ModException>(() => CurseForgeResolver.ResolveAsync(manifest, client, default));
    }

    [Fact]
    public async Task Resolve_AsksInBatches()
    {
        var files = string.Join(",", Enumerable.Range(1, 120).Select(i => $$"""{ "projectID": {{i}}, "fileID": {{i}} }"""));
        var manifest = CurseForgePackReader.Read(MakeZip(
            """{ "manifestType": "minecraftModpack", "name": "x", "minecraft": { "version": "1.20.1" }, "files": [""" + files + "] }"));
        var client = new FakeClient();

        await CurseForgeResolver.ResolveAsync(manifest, client, default);

        Assert.Equal(new[] { 50, 50, 20 }, client.Calls.Select(c => c.Count).ToArray());
    }

    [Fact]
    public void ParseFiles_PicksSha1ByLength_AndHandlesNullUrl()
    {
        var json = $$"""
            { "data": [
              { "id": 1, "modId": 9, "fileName": "a.jar", "downloadUrl": "https://edge.forgecdn.net/a.jar", "fileLength": 7,
                "hashes": [ { "value": "d41d8cd98f00b204e9800998ecf8427e", "algo": 2 }, { "value": "{{Sha}}", "algo": 1 } ] },
              { "id": 2, "modId": 9, "fileName": "b.jar", "downloadUrl": null, "hashes": [] },
              { "broken": true } ] }
            """;
        var files = CurseForgeParser.ParseFiles(json);
        Assert.Equal(2, files.Count);
        Assert.Equal(Sha, files[0].Sha1);
        Assert.Equal(7, files[0].Length);
        Assert.Null(files[1].DownloadUrl);
        Assert.Null(files[1].Sha1);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public void ParseFiles_BadInput_Throws(string json) =>
        Assert.Throws<ModException>(() => CurseForgeParser.ParseFiles(json));

    [Fact]
    public async Task Import_DownloadsFromCdn_ExtractsCustomOverrides_WritesManualList()
    {
        var pack = MakeZip(ManifestJson, ("overrides/config/a.toml", "cfg"), ("client-overrides/config/b.toml", "ignored"));
        var manifest = CurseForgePackReader.Read(pack);
        var client = new FakeClient(
            File_(100, "alpha.jar", "https://edge.forgecdn.net/files/1/2/alpha.jar"),
            File_(200, "blocked.jar", null));
        var info = await CurseForgeResolver.ResolveAsync(manifest, client, default);

        var layout = new Launcher.Core.Installation.MinecraftLayout(Path.Combine(Dir, "game"));
        var downloader = new FakeDownloader();
        var result = await new MrpackImporter(downloader, layout).ImportAsync("Cf", pack, info, null, default);

        var instance = layout.InstanceDirectory("Cf");
        Assert.True(File.Exists(Path.Combine(instance, "mods", "alpha.jar")));
        Assert.Equal("cfg", await File.ReadAllTextAsync(Path.Combine(instance, "config", "a.toml")));
        Assert.False(File.Exists(Path.Combine(instance, "config", "b.toml"))); // у CurseForge только папка из manifest.json
        Assert.Contains("blocked.jar", await File.ReadAllTextAsync(Path.Combine(instance, "waundbox-manual-downloads.txt")));
        Assert.Equal(1, result.FilesDownloaded);
        Assert.Equal(Sha, downloader.Requests.Single().Sha1);
    }

    [Fact]
    public async Task Import_RefusesCdnHostThatIsNotListed()
    {
        var pack = MakeZip(ManifestJson);
        var info = new MrpackInfo("x", null, "", "1.20.1", LoaderKind.Forge, "47.4.0",
            [new MrpackFile("mods/a.jar", Sha, [new Uri("https://edge.forgecdn.net/a.jar")], 1)], 0);

        var layout = new Launcher.Core.Installation.MinecraftLayout(Path.Combine(Dir, "game"));
        await Assert.ThrowsAsync<ModException>(() => new MrpackImporter(new FakeDownloader(), layout).ImportAsync("X", pack, info, null, default));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    [Fact]
    public async Task HttpClient_SendsKeyHeaderAndFileIds()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "data": [] }""") });
        var client = new HttpCurseForgeClient(new HttpClient(handler), _ => Task.FromResult<string?>("  KEY123  "));

        await client.GetFilesAsync([1, 2], default);

        Assert.Equal("https://api.curseforge.com/v1/mods/files", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("KEY123", handler.Requests[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal("""{"fileIds":[1,2]}""", handler.Bodies[0]);
    }

    [Fact]
    public async Task HttpClient_NoKey_ExplainsHowToGetOne()
    {
        var client = new HttpCurseForgeClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), _ => Task.FromResult<string?>(null));
        var ex = await Assert.ThrowsAsync<ModException>(() => client.GetFilesAsync([1], default));
        Assert.Contains("console.curseforge.com", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task HttpClient_RejectedKey_SaysSo(HttpStatusCode status)
    {
        var client = new HttpCurseForgeClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(status))), _ => Task.FromResult<string?>("K"));
        var ex = await Assert.ThrowsAsync<ModException>(() => client.GetFilesAsync([1], default));
        Assert.Contains("отклонил ключ", ex.Message);
    }

    [Fact]
    public async Task HttpClient_NetworkFailure_BecomesModException()
    {
        var client = new HttpCurseForgeClient(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("boom"))), _ => Task.FromResult<string?>("K"));
        await Assert.ThrowsAsync<ModException>(() => client.GetFilesAsync([1], default));
    }
}
