using System.Net;
using System.Security.Cryptography;
using System.Text;
using Launcher.Core.Configuration;
using Launcher.Core.Logging;
using Launcher.Core.Updates;
using Launcher.Infrastructure.Configuration;
using Launcher.Infrastructure.Updates;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class UpdateTests : TempDirectoryTest
{
    private const string Download = "https://github.com/waundbox/Minecraft-Launcher-Waundbox/releases/download/v2.0.0/Waundbox-Setup-2.0.0.exe";
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("pretend installer");
    private static readonly string InstallerSha = Convert.ToHexStringLower(SHA256.HashData(Installer));

    private static ReleaseInfo Release(string version, bool required = false, string? sha = null, string? url = Download, long? size = null) =>
        new(Version.Parse(version), "v" + version, version, "Что нового", "https://github.com/waundbox/Minecraft-Launcher-Waundbox/releases/tag/v" + version,
            required, "Waundbox-Setup.exe", url, size, sha);

    // ----- версии -----

    [Theory]
    [InlineData("v2.0.0", "2.0.0", false)]
    [InlineData("2.1", "2.1.0", false)]
    [InlineData("V1.1.0", "1.1.0", false)]
    [InlineData("1.2.3-beta", "1.2.3", true)]
    [InlineData("1.2.3+build5", "1.2.3", false)]
    public void Version_parsing(string text, string expected, bool pre)
    {
        Assert.True(AppVersion.TryParse(text, out var version, out var preRelease));
        Assert.Equal(Version.Parse(expected), version);
        Assert.Equal(pre, preRelease);
    }

    [Theory]
    [InlineData("Winows-release")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v1.x.0")]
    [InlineData("1.2.3.4.5")]
    public void Not_a_version(string? text) => Assert.False(AppVersion.TryParse(text, out _, out _));

    // ----- разбор релизов -----

    private static string ReleasesJson(string digest = "") => $$"""
    [
      { "tag_name": "v3.0.0", "name": "draft", "draft": true, "prerelease": false, "body": "", "html_url": "https://github.com/waundbox/Minecraft-Launcher-Waundbox/releases/tag/v3.0.0", "assets": [] },
      { "tag_name": "v2.5.0-beta", "name": "beta", "draft": false, "prerelease": true, "body": "", "html_url": "https://github.com/x/y", "assets": [] },
      { "tag_name": "Winows-release", "name": "1.0.0", "draft": false, "prerelease": false, "body": "первый выпуск", "html_url": "https://github.com/waundbox/Minecraft-Launcher-Waundbox/releases/tag/Winows-release", "assets": [] },
      { "tag_name": "v2.0.0", "name": "Waundbox 2.0.0", "draft": false, "prerelease": false,
        "body": "Обязательно: да\r\n- исправлено то\r\n- добавлено это", "html_url": "https://github.com/waundbox/Minecraft-Launcher-Waundbox/releases/tag/v2.0.0",
        "assets": [
          { "name": "source.zip", "browser_download_url": "https://github.com/waundbox/Minecraft-Launcher-Waundbox/releases/download/v2.0.0/source.zip", "size": 5 },
          { "name": "Waundbox-Setup-2.0.0.exe", "browser_download_url": "{{Download}}", "size": 17 {{digest}} },
          { "name": "Waundbox-Setup-2.0.0.exe.sha256", "browser_download_url": "{{Download}}.sha256", "size": 100 }
        ] }
    ]
    """;

    [Fact]
    public void Releases_skip_drafts_prereleases_and_use_name_when_tag_is_not_a_version()
    {
        var releases = ReleaseParser.ParseReleases(ReleasesJson());
        Assert.Equal(new[] { "1.0.0", "2.0.0" }, releases.Select(r => r.Version.ToString()).OrderBy(v => v).ToArray());
    }

    [Fact]
    public void Release_fields_marker_and_installer()
    {
        var release = ReleaseParser.ParseReleases(ReleasesJson($", \"digest\": \"sha256:{InstallerSha.ToUpperInvariant()}\"")).Single(r => r.Version == new Version(2, 0, 0));

        Assert.True(release.IsRequired);
        Assert.DoesNotContain("Обязательно", release.Notes);
        Assert.Contains("исправлено то", release.Notes);
        Assert.Equal("Waundbox-Setup-2.0.0.exe", release.InstallerName);
        Assert.Equal(Download, release.InstallerUrl);
        Assert.Equal(17, release.InstallerSize);
        Assert.Equal(InstallerSha, release.InstallerSha256);
        Assert.Equal(Download + ".sha256", release.HashFileUrl);
    }

    [Fact]
    public void Release_without_marker_is_optional_and_hash_can_come_from_the_text()
    {
        var json = $$"""
        [{ "tag_name": "v1.5.0", "name": "x", "draft": false, "prerelease": false,
           "body": "Обязательно: нет\nSHA-256: {{InstallerSha}}", "html_url": "https://github.com/waundbox/Minecraft-Launcher-Waundbox/releases/tag/v1.5.0",
           "assets": [{ "name": "Waundbox-Setup.exe", "browser_download_url": "{{Download}}", "size": 17 }] }]
        """;
        var release = ReleaseParser.ParseReleases(json).Single();
        Assert.False(release.IsRequired);
        Assert.Equal(InstallerSha, release.InstallerSha256);
    }

    [Fact]
    public void Foreign_release_page_is_replaced_with_ours()
    {
        var json = """[{ "tag_name": "v1.5.0", "name": "x", "draft": false, "prerelease": false, "body": "", "html_url": "https://evil.example/page", "assets": [] }]""";
        Assert.Equal(UpdateSource.ReleasesPage, ReleaseParser.ParseReleases(json).Single().PageUrl);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Garbage_response_is_an_update_error(string json) =>
        Assert.Throws<UpdateException>(() => ReleaseParser.ParseReleases(json));

    // ----- выбор обновления -----

    [Fact]
    public void Latest_newer_release_is_chosen_and_nothing_when_up_to_date()
    {
        var releases = new[] { Release("1.0.0"), Release("1.2.0"), Release("1.1.0") };

        var result = UpdateChecker.Evaluate(releases, new Version(1, 0, 0));
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(1, 2, 0), result.Latest!.Version);
        Assert.False(result.IsRequired);

        Assert.False(UpdateChecker.Evaluate(releases, new Version(1, 2, 0)).IsUpdateAvailable);
        Assert.False(UpdateChecker.Evaluate(releases, new Version(5, 0, 0)).IsUpdateAvailable);
    }

    [Fact]
    public void Required_release_in_the_middle_makes_the_update_required()
    {
        var releases = new[] { Release("1.1.0", required: true), Release("1.2.0") };
        var result = UpdateChecker.Evaluate(releases, new Version(1, 0, 0, 0));
        Assert.True(result.IsRequired);
        Assert.Equal(new Version(1, 2, 0), result.Latest!.Version);

        // уже обновились до обязательного — дальнейший обычный выпуск обязательным не считается
        Assert.False(UpdateChecker.Evaluate(releases, new Version(1, 1, 0)).IsRequired);
    }

    private sealed class FakeUpdateClient : IUpdateClient
    {
        public IReadOnlyList<ReleaseInfo> Releases { get; init; } = Array.Empty<ReleaseInfo>();
        public string? Hash { get; init; }
        public int HashReads { get; private set; }

        public Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct) => Task.FromResult(Releases);

        public Task<string?> ReadHashFileAsync(string url, CancellationToken ct)
        {
            HashReads++;
            return Task.FromResult(Hash);
        }
    }

    [Fact]
    public async Task Checker_reads_the_hash_file_when_release_has_no_hash()
    {
        var client = new FakeUpdateClient { Releases = new[] { Release("2.0.0") with { HashFileUrl = Download + ".sha256" } }, Hash = InstallerSha };
        var result = await new UpdateChecker(client).CheckAsync(new Version(1, 0, 0), CancellationToken.None);
        Assert.Equal(InstallerSha, result.Latest!.InstallerSha256);
        Assert.Equal(1, client.HashReads);
    }

    [Fact]
    public async Task Checker_does_not_read_the_hash_file_when_hash_is_known_or_no_update()
    {
        var known = new FakeUpdateClient { Releases = new[] { Release("2.0.0", sha: InstallerSha) with { HashFileUrl = Download + ".sha256" } } };
        await new UpdateChecker(known).CheckAsync(new Version(1, 0, 0), CancellationToken.None);
        var none = new FakeUpdateClient { Releases = new[] { Release("1.0.0") with { HashFileUrl = Download + ".sha256" } } };
        await new UpdateChecker(none).CheckAsync(new Version(1, 0, 0), CancellationToken.None);
        Assert.Equal(0, known.HashReads + none.HashReads);
    }

    // ----- GitHub -----

    [Fact]
    public async Task GitHub_client_parses_releases_and_asks_the_right_address()
    {
        string? requested = null;
        var handler = new DelegateHandler(request =>
        {
            requested = request.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleasesJson()) };
        });
        var releases = await new GitHubUpdateClient(new HttpClient(handler)).GetReleasesAsync(CancellationToken.None);

        Assert.Equal(2, releases.Count);
        Assert.StartsWith("https://api.github.com/repos/waundbox/Minecraft-Launcher-Waundbox/releases", requested);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GitHub_client_errors_become_update_errors(HttpStatusCode code)
    {
        var client = new GitHubUpdateClient(new HttpClient(new SequenceHandler(Responses.Status(code))));
        await Assert.ThrowsAsync<UpdateException>(() => client.GetReleasesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GitHub_client_without_network_is_an_update_error()
    {
        var client = new GitHubUpdateClient(new HttpClient(new DelegateHandler(_ => throw new HttpRequestException("offline"))));
        var ex = await Assert.ThrowsAsync<UpdateException>(() => client.GetReleasesAsync(CancellationToken.None));
        Assert.Contains("подключение", ex.Message);
    }

    [Fact]
    public async Task Hash_file_is_read_only_from_our_repository()
    {
        var handler = new SequenceHandler(Responses.Ok(Encoding.UTF8.GetBytes($"{InstallerSha}  Waundbox-Setup-2.0.0.exe\n")));
        var client = new GitHubUpdateClient(new HttpClient(handler));

        Assert.Equal(InstallerSha, await client.ReadHashFileAsync(Download + ".sha256", CancellationToken.None));
        Assert.Null(await client.ReadHashFileAsync("https://evil.example/a.sha256", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    // ----- безопасность адресов -----

    [Theory]
    [InlineData("https://github.com/waundbox/Minecraft-Launcher-Waundbox/releases/download/v1/a.exe", true)]
    [InlineData("https://GitHub.com/Waundbox/minecraft-launcher-waundbox/releases/download/v1/a.exe", true)]
    [InlineData("http://github.com/waundbox/Minecraft-Launcher-Waundbox/releases/download/v1/a.exe", false)]
    [InlineData("https://github.com/evil/Minecraft-Launcher-Waundbox/releases/download/v1/a.exe", false)]
    [InlineData("https://github.com/waundbox/other/releases/download/v1/a.exe", false)]
    [InlineData("https://github.com.evil.example/waundbox/Minecraft-Launcher-Waundbox/releases/download/v1/a.exe", false)]
    [InlineData("https://github.com/waundbox/Minecraft-Launcher-Waundbox/releases/download/../../x", false)]
    [InlineData("https://github.com/waundbox/Minecraft-Launcher-Waundbox/archive/main.zip", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Trusted_asset_addresses(string? url, bool trusted) => Assert.Equal(trusted, UpdateSource.IsTrustedAssetUrl(url));

    // ----- скачивание установщика -----

    private UpdateDownloader Downloader(params Func<HttpResponseMessage>[] responses) =>
        new(new HttpClient(new SequenceHandler(responses)));

    [Fact]
    public async Task Verified_installer_is_saved()
    {
        var path = await Downloader(Responses.Ok(Installer))
            .DownloadAsync(Release("2.0.0", sha: InstallerSha, size: Installer.Length), Dir, null, CancellationToken.None);

        Assert.Equal(Installer, await File.ReadAllBytesAsync(path));
        Assert.Equal("Waundbox-Setup-2.0.0.exe", Path.GetFileName(path));
        Assert.Empty(Directory.GetFiles(Dir, "*.part"));
    }

    [Fact]
    public async Task Installer_with_wrong_hash_is_deleted_and_refused()
    {
        var wrong = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1 }));
        var downloader = Downloader(Responses.Ok(Installer));

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            downloader.DownloadAsync(Release("2.0.0", sha: wrong), Dir, null, CancellationToken.None));

        Assert.Contains("контрольной суммой", ex.Message);
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task Incomplete_installer_is_refused()
    {
        var downloader = Downloader(Responses.Ok(Installer));
        await Assert.ThrowsAsync<UpdateException>(() =>
            downloader.DownloadAsync(Release("2.0.0", sha: InstallerSha, size: Installer.Length + 10), Dir, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task Installer_without_hash_or_from_a_foreign_address_is_never_downloaded()
    {
        var handler = new SequenceHandler(Responses.Ok(Installer));
        var downloader = new UpdateDownloader(new HttpClient(handler));

        await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadAsync(Release("2.0.0", sha: null), Dir, null, CancellationToken.None));
        await Assert.ThrowsAsync<UpdateException>(() =>
            downloader.DownloadAsync(Release("2.0.0", sha: InstallerSha, url: "https://evil.example/Waundbox-Setup.exe"), Dir, null, CancellationToken.None));

        Assert.Equal(0, handler.Calls);
        Assert.False(UpdateDownloader.CanInstall(Release("2.0.0", sha: null)));
        Assert.False(UpdateDownloader.CanInstall(Release("2.0.0", sha: InstallerSha, url: null)));
        Assert.True(UpdateDownloader.CanInstall(Release("2.0.0", sha: InstallerSha)));
    }

    [Fact]
    public async Task Download_server_error_is_an_update_error_and_progress_reaches_one()
    {
        await Assert.ThrowsAsync<UpdateException>(() =>
            Downloader(Responses.Status(HttpStatusCode.NotFound)).DownloadAsync(Release("2.0.0", sha: InstallerSha), Dir, null, CancellationToken.None));

        double last = 0;
        await Downloader(Responses.Ok(Installer)).DownloadAsync(Release("2.0.0", sha: InstallerSha, size: Installer.Length), Dir,
            new Launcher.Core.Downloads.InlineProgress<double>(v => last = v), CancellationToken.None);
        Assert.Equal(1.0, last);
    }

    // ----- настройки -----

    [Fact]
    public async Task Update_settings_survive_save_and_load()
    {
        var store = new JsonSettingsStore(Path.Combine(Dir, "settings.json"), NullLauncherLogger.Instance);
        var defaults = await store.LoadAsync(CancellationToken.None);
        Assert.True(defaults.AutoCheckUpdates);
        Assert.False(defaults.LiquidGlass);

        await store.SaveAsync(new LauncherSettings(AutoCheckUpdates: false, RequiredUpdateVersion: "2.0.0", LiquidGlass: true), CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.AutoCheckUpdates);
        Assert.Equal("2.0.0", loaded.RequiredUpdateVersion);
        Assert.True(loaded.LiquidGlass);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
