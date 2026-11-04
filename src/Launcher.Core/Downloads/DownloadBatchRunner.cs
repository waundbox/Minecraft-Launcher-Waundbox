namespace Launcher.Core.Downloads;

/// <summary>Общий прогресс пакета загрузок.</summary>
public sealed record BatchProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

/// <summary>Итог пакета загрузок.</summary>
public sealed record BatchResult(int Downloaded, int AlreadyValid, long BytesDownloaded);

/// <summary>Скачивает много файлов параллельно с ограничением числа одновременных загрузок.</summary>
public sealed class DownloadBatchRunner
{
    private readonly IFileDownloader _downloader;
    private readonly int _maxConcurrency;

    public DownloadBatchRunner(IFileDownloader downloader, int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        _downloader = downloader;
        _maxConcurrency = maxConcurrency;
    }

    /// <summary>
    /// Первая же ошибка отменяет остальные загрузки и пробрасывается наружу.
    /// Отмена через <paramref name="ct"/> вызывает OperationCanceledException.
    /// </summary>
    public async Task<BatchResult> RunAsync(
        IReadOnlyList<DownloadRequest> requests, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var filesTotal = requests.Count;
        var bytesTotal = requests.Sum(r => r.Size ?? 0);
        var done = 0;
        var downloaded = 0;
        var alreadyValid = 0;
        long bytesDone = 0;
        long bytesDownloaded = 0;

        void Publish() => progress?.Report(new BatchProgress(
            Volatile.Read(ref done), filesTotal, Interlocked.Read(ref bytesDone), bytesTotal));

        var bytesCallback = new InlineProgress<long>(delta =>
        {
            Interlocked.Add(ref bytesDone, delta);
            Interlocked.Add(ref bytesDownloaded, delta);
            Publish();
        });

        var options = new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrency, CancellationToken = ct };

        await Parallel.ForEachAsync(requests, options, async (request, token) =>
        {
            var outcome = await _downloader.DownloadAsync(request, bytesCallback, token).ConfigureAwait(false);
            if (outcome == DownloadOutcome.AlreadyValid)
            {
                Interlocked.Increment(ref alreadyValid);
                Interlocked.Add(ref bytesDone, request.Size ?? 0);
            }
            else
            {
                Interlocked.Increment(ref downloaded);
            }
            Interlocked.Increment(ref done);
            Publish();
        }).ConfigureAwait(false);

        return new BatchResult(downloaded, alreadyValid, Interlocked.Read(ref bytesDownloaded));
    }
}
