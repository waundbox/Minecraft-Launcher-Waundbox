using Launcher.Core.Downloads;
using Launcher.Core.Errors;
using Launcher.Core.Logging;

namespace Launcher.Infrastructure.Downloads;

/// <summary>
/// Скачивание файла по HTTP/HTTPS: проверка уже имеющегося файла, запись во временный «.part»,
/// проверка размера и SHA-1, безопасная замена итогового файла, повторы при временных ошибках.
/// </summary>
public sealed class HttpFileDownloader : IFileDownloader
{
    private const int BufferSize = 81920;

    private readonly HttpClient _http;
    private readonly ILauncherLogger _logger;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryBaseDelay;
    private readonly TimeSpan _stallTimeout;

    /// <param name="http">Клиент без общего таймаута (за зависание отвечает <paramref name="stallTimeout"/>).</param>
    /// <param name="maxAttempts">Сколько всего попыток на файл.</param>
    /// <param name="retryBaseDelay">Пауза перед второй попыткой; дальше удваивается.</param>
    /// <param name="stallTimeout">Сколько ждать данных, прежде чем считать соединение зависшим.</param>
    public HttpFileDownloader(
        HttpClient http,
        ILauncherLogger? logger = null,
        int maxAttempts = 3,
        TimeSpan? retryBaseDelay = null,
        TimeSpan? stallTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _http = http;
        _logger = logger ?? NullLauncherLogger.Instance;
        _maxAttempts = maxAttempts;
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<DownloadOutcome> DownloadAsync(
        DownloadRequest request, IProgress<long>? bytesProgress, CancellationToken ct)
    {
        if (await FileIntegrity.IsKnownGoodAsync(request.DestinationPath, request.Sha1, request.Size, ct).ConfigureAwait(false))
            return DownloadOutcome.AlreadyValid;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath))!);
        EnsureFreeSpace(request);

        for (var attempt = 1; ; attempt++)
        {
            long reported = 0;
            void Report(long delta)
            {
                reported += delta;
                bytesProgress?.Report(delta);
            }

            try
            {
                await DownloadOnceAsync(request, Report, ct).ConfigureAwait(false);
                return DownloadOutcome.Downloaded;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // пользователь отменил загрузку
            }
            catch (Exception ex)
            {
                bytesProgress?.Report(-reported); // эта попытка не засчитывается

                if (IsDiskFull(ex))
                    throw new InsufficientDiskSpaceException(
                        $"Недостаточно места на диске при загрузке {Path.GetFileName(request.DestinationPath)}.", ex);

                if (!IsTransient(ex) || attempt >= _maxAttempts)
                    throw ToLauncherException(ex, request, attempt);

                _logger.Warn($"Попытка {attempt}/{_maxAttempts} не удалась для {request.Url}, повтор.", ex);
                await Task.Delay(_retryBaseDelay * Math.Pow(2, attempt - 1), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadOnceAsync(DownloadRequest request, Action<long> report, CancellationToken ct)
    {
        var temp = request.DestinationPath + ".part";
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(_stallTimeout);

            using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var network = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await network.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    report(read);
                    stall.CancelAfter(_stallTimeout); // данные идут — сбрасываем таймер зависания
                }
            }

            if (!await FileIntegrity.MatchesAsync(temp, request.Sha1, request.Size, ct).ConfigureAwait(false))
                throw new IntegrityException(
                    $"Файл {Path.GetFileName(request.DestinationPath)} скачан, но не совпал с контрольной суммой или размером.");

            File.Move(temp, request.DestinationPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void EnsureFreeSpace(DownloadRequest request)
    {
        if (request.Size is not long needed) return;

        long free;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(request.DestinationPath));
            if (string.IsNullOrEmpty(root)) return;
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // не удалось узнать свободное место — полагаемся на ошибку записи
        }

        if (free < needed)
            throw new InsufficientDiskSpaceException(
                $"Недостаточно места на диске: для {Path.GetFileName(request.DestinationPath)} нужно {needed} байт, свободно {free}.");
    }

    // 0x70 = ERROR_DISK_FULL, 0x27 = ERROR_HANDLE_DISK_FULL (Windows)
    private static bool IsDiskFull(Exception ex) => ex is IOException io && ((io.HResult & 0xFFFF) is 0x70 or 0x27);

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException http => http.StatusCode is null
            || (int)http.StatusCode >= 500
            || http.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests,
        OperationCanceledException => true, // таймаут зависания (отмену пользователем обработали раньше)
        IntegrityException => true,         // файл мог оборваться на полпути
        IOException => true,
        _ => false
    };

    private static Exception ToLauncherException(Exception ex, DownloadRequest request, int attempts) => ex switch
    {
        LauncherException le => le,
        HttpRequestException { StatusCode: not null } http =>
            new DownloadException($"Сервер ответил {(int)http.StatusCode} ({http.StatusCode}) для {request.Url}.", ex),
        _ => new DownloadException(
            $"Не удалось скачать {request.Url} (попыток: {attempts}): {ex.Message}", ex)
    };
}
