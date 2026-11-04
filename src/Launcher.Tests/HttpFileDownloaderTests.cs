using System.Net;
using Launcher.Core.Downloads;
using Launcher.Core.Errors;
using Launcher.Infrastructure.Downloads;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class HttpFileDownloaderTests : TempDirectoryTest
{
    private static readonly byte[] Content = "minecraft-bytes"u8.ToArray();
    private static readonly Uri Url = new("https://example.test/file.jar");

    private string Dest => Path.Combine(Dir, "out", "file.jar");

    private static HttpFileDownloader Create(SequenceHandler handler, int attempts = 3) =>
        new(new HttpClient(handler), maxAttempts: attempts, retryBaseDelay: TimeSpan.Zero);

    private DownloadRequest Request(byte[]? expected = null) =>
        new(Url, Dest, Responses.Sha1Of(expected ?? Content), (expected ?? Content).Length);

    [Fact]
    public async Task Downloads_Verifies_AndLeavesNoTempFile()
    {
        var handler = new SequenceHandler(Responses.Ok(Content));

        var outcome = await Create(handler).DownloadAsync(Request(), null, default);

        Assert.Equal(DownloadOutcome.Downloaded, outcome);
        Assert.Equal(Content, File.ReadAllBytes(Dest));
        Assert.False(File.Exists(Dest + ".part"));
    }

    [Fact]
    public async Task ExistingValidFile_IsNotDownloadedAgain()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Dest)!);
        File.WriteAllBytes(Dest, Content);
        var handler = new SequenceHandler(Responses.Status(HttpStatusCode.InternalServerError));

        var outcome = await Create(handler).DownloadAsync(Request(), null, default);

        Assert.Equal(DownloadOutcome.AlreadyValid, outcome);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ExistingCorruptFile_IsReplaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Dest)!);
        File.WriteAllText(Dest, "garbage of the same..");
        var handler = new SequenceHandler(Responses.Ok(Content));

        var outcome = await Create(handler).DownloadAsync(Request(), null, default);

        Assert.Equal(DownloadOutcome.Downloaded, outcome);
        Assert.Equal(Content, File.ReadAllBytes(Dest));
    }

    [Fact]
    public async Task WrongChecksum_Throws_AndKeepsNoFile()
    {
        var handler = new SequenceHandler(Responses.Ok("tampered!!"u8.ToArray()));

        await Assert.ThrowsAsync<IntegrityException>(() => Create(handler, attempts: 2).DownloadAsync(Request(), null, default));

        Assert.Equal(2, handler.Calls);
        Assert.False(File.Exists(Dest));
        Assert.False(File.Exists(Dest + ".part"));
    }

    [Fact]
    public async Task TransientServerError_IsRetried()
    {
        var handler = new SequenceHandler(Responses.Status(HttpStatusCode.ServiceUnavailable), Responses.Ok(Content));
        long total = 0;

        await Create(handler).DownloadAsync(Request(), new InlineProgress<long>(d => total += d), default);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(Content.Length, total);
    }

    [Fact]
    public async Task NotFound_IsNotRetried()
    {
        var handler = new SequenceHandler(Responses.Status(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<DownloadException>(() => Create(handler).DownloadAsync(Request(), null, default));

        Assert.Equal(1, handler.Calls);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task ReportsBytesProgress()
    {
        long total = 0;
        await Create(new SequenceHandler(Responses.Ok(Content))).DownloadAsync(Request(), new InlineProgress<long>(d => total += d), default);
        Assert.Equal(Content.Length, total);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(new SequenceHandler(Responses.Ok(Content))).DownloadAsync(Request(), null, cts.Token));

        Assert.False(File.Exists(Dest));
    }
}
