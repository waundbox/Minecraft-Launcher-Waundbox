using Launcher.Core.Downloads;
using Launcher.Core.Errors;

namespace Launcher.Tests;

public class DownloadBatchRunnerTests
{
    private sealed class SlowDownloader(DownloadOutcome outcome = DownloadOutcome.Downloaded, string? failOn = null) : IFileDownloader
    {
        private int _current;
        private int _max;
        public int MaxObserved => _max;

        public async Task<DownloadOutcome> DownloadAsync(DownloadRequest request, IProgress<long>? progress, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _current);
            int snapshot;
            do { snapshot = _max; } while (now > snapshot && Interlocked.CompareExchange(ref _max, now, snapshot) != snapshot);

            try
            {
                await Task.Delay(15, ct);
                if (failOn is not null && request.Url.AbsolutePath.EndsWith(failOn))
                    throw new DownloadException("boom");
                if (outcome == DownloadOutcome.Downloaded) progress?.Report(request.Size ?? 0);
                return outcome;
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    private static List<DownloadRequest> Requests(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new DownloadRequest(new Uri($"https://example.test/f{i}"), $"f{i}", null, 10))
            .ToList();

    [Fact]
    public async Task RespectsConcurrencyLimit()
    {
        var downloader = new SlowDownloader();

        var result = await new DownloadBatchRunner(downloader, 3).RunAsync(Requests(20), null, default);

        Assert.Equal(20, result.Downloaded);
        Assert.InRange(downloader.MaxObserved, 1, 3);
    }

    [Fact]
    public async Task ReportsAggregatedProgress()
    {
        var reports = new List<BatchProgress>();
        var progress = new InlineProgress<BatchProgress>(p => { lock (reports) reports.Add(p); });

        var result = await new DownloadBatchRunner(new SlowDownloader(), 4).RunAsync(Requests(20), progress, default);

        Assert.Equal(200, result.BytesDownloaded);
        Assert.Equal(20, reports.Max(r => r.FilesDone));
        Assert.Equal(200, reports.Max(r => r.BytesDone));
        Assert.All(reports, r => Assert.Equal(20, r.FilesTotal));
    }

    [Fact]
    public async Task AlreadyValidFiles_AreCountedSeparately_AndCountTowardsProgress()
    {
        var reports = new List<BatchProgress>();
        var progress = new InlineProgress<BatchProgress>(p => { lock (reports) reports.Add(p); });

        var result = await new DownloadBatchRunner(new SlowDownloader(DownloadOutcome.AlreadyValid), 4)
            .RunAsync(Requests(5), progress, default);

        Assert.Equal(0, result.Downloaded);
        Assert.Equal(5, result.AlreadyValid);
        Assert.Equal(50, reports.Max(r => r.BytesDone));
    }

    [Fact]
    public async Task FirstFailure_IsPropagated() =>
        await Assert.ThrowsAsync<DownloadException>(() =>
            new DownloadBatchRunner(new SlowDownloader(failOn: "f7"), 4).RunAsync(Requests(20), null, default));

    [Fact]
    public async Task Cancellation_StopsTheBatch()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DownloadBatchRunner(new SlowDownloader(), 4).RunAsync(Requests(20), null, cts.Token));
    }

    [Fact]
    public void ZeroConcurrency_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DownloadBatchRunner(new SlowDownloader(), 0));
}
