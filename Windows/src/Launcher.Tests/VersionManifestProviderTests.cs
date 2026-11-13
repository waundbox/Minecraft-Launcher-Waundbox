using System.Net;
using Launcher.Core.Errors;
using Launcher.Core.Versions;
using Launcher.Infrastructure.Versions;

namespace Launcher.Tests;

public class VersionManifestProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "waundbox-tests-" + Guid.NewGuid().ToString("N"));
    private string CacheFile => Path.Combine(_dir, "cache", "manifest.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private VersionManifestProvider Create(StubHandler handler, TimeSpan? ttl = null) =>
        new(new HttpClient(handler), CacheFile, ttl ?? TimeSpan.FromHours(1));

    [Fact]
    public async Task NoCache_LoadsFromNetwork_AndWritesCache()
    {
        var handler = new StubHandler(() => Ok(TestData.ManifestJson));

        var result = await Create(handler).GetManifestAsync(false, CancellationToken.None);

        Assert.Equal(ManifestSource.Network, result.Source);
        Assert.Equal(3, result.Manifest.Versions.Count);
        Assert.True(File.Exists(CacheFile));
        Assert.False(File.Exists(CacheFile + ".tmp"));
    }

    [Fact]
    public async Task FreshCache_IsUsedWithoutNetwork()
    {
        await Create(new StubHandler(() => Ok(TestData.ManifestJson))).GetManifestAsync(false, CancellationToken.None);
        var second = new StubHandler(() => throw new InvalidOperationException("Сеть не должна вызываться"));

        var result = await Create(second).GetManifestAsync(false, CancellationToken.None);

        Assert.Equal(ManifestSource.FreshCache, result.Source);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task ForceRefresh_BypassesFreshCache()
    {
        await Create(new StubHandler(() => Ok(TestData.ManifestJson))).GetManifestAsync(false, CancellationToken.None);
        var second = new StubHandler(() => Ok(TestData.ManifestJson));

        var result = await Create(second).GetManifestAsync(true, CancellationToken.None);

        Assert.Equal(ManifestSource.Network, result.Source);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task ServerDown_WithOldCache_ReturnsStaleCache()
    {
        await Create(new StubHandler(() => Ok(TestData.ManifestJson))).GetManifestAsync(false, CancellationToken.None);
        File.SetLastWriteTimeUtc(CacheFile, DateTime.UtcNow.AddDays(-3));
        var down = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await Create(down).GetManifestAsync(false, CancellationToken.None);

        Assert.Equal(ManifestSource.StaleCache, result.Source);
        Assert.Equal(3, result.Manifest.Versions.Count);
    }

    [Fact]
    public async Task ServerDown_NoCache_ThrowsReadableError()
    {
        var down = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<ManifestUnavailableException>(
            () => Create(down).GetManifestAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task GarbageFromServer_DoesNotOverwriteGoodCache()
    {
        await Create(new StubHandler(() => Ok(TestData.ManifestJson))).GetManifestAsync(false, CancellationToken.None);
        var before = File.ReadAllText(CacheFile);
        var garbage = new StubHandler(() => Ok("<html>captive portal</html>"));

        var result = await Create(garbage).GetManifestAsync(true, CancellationToken.None);

        Assert.Equal(ManifestSource.StaleCache, result.Source);
        Assert.Equal(before, File.ReadAllText(CacheFile));
    }

    [Fact]
    public async Task CorruptCache_IsIgnored_AndReplacedFromNetwork()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
        File.WriteAllText(CacheFile, "{ broken");

        var result = await Create(new StubHandler(() => Ok(TestData.ManifestJson)))
            .GetManifestAsync(false, CancellationToken.None);

        Assert.Equal(ManifestSource.Network, result.Source);
        Assert.Equal(3, VersionManifestParser.Parse(File.ReadAllText(CacheFile)).Versions.Count);
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHandler(() => Ok(TestData.ManifestJson));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(handler).GetManifestAsync(true, cts.Token));
    }
}
