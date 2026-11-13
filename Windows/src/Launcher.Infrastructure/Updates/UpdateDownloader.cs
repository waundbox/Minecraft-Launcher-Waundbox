using System.Security.Cryptography;
using Launcher.Core.Logging;
using Launcher.Core.Updates;

namespace Launcher.Infrastructure.Updates;

/// <summary>Скачивает установщик новой версии и проверяет его SHA-256. Непроверенный файл не отдаётся никогда.</summary>
public sealed class UpdateDownloader
{
    private const long MaxInstallerBytes = 600L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILauncherLogger _logger;
    private readonly string _owner;
    private readonly string _repository;

    /// <param name="http">Клиент без общего таймаута.</param>
    public UpdateDownloader(HttpClient http, ILauncherLogger? logger = null,
        string owner = UpdateSource.Owner, string repository = UpdateSource.Repository)
    {
        _http = http;
        _logger = logger ?? NullLauncherLogger.Instance;
        _owner = owner;
        _repository = repository;
    }

    /// <summary>Можно ли установить этот выпуск прямо из лаунчера (есть установщик из нашего репозитория и контрольная сумма).</summary>
    public static bool CanInstall(ReleaseInfo release, string owner = UpdateSource.Owner, string repository = UpdateSource.Repository) =>
        release.InstallerSha256 is { Length: 64 } && UpdateSource.IsTrustedAssetUrl(release.InstallerUrl, owner, repository);

    /// <summary>Скачивает установщик в <paramref name="directory"/> и возвращает путь к проверенному файлу.</summary>
    /// <param name="progress">Доля 0…1 (если размер известен).</param>
    public async Task<string> DownloadAsync(ReleaseInfo release, string directory, IProgress<double>? progress, CancellationToken ct)
    {
        if (release.InstallerSha256 is not { Length: 64 } expected)
            throw new UpdateException("У этого выпуска нет контрольной суммы установщика, поэтому лаунчер не станет его запускать. Скачайте установщик со страницы релиза.");
        if (!UpdateSource.IsTrustedAssetUrl(release.InstallerUrl, _owner, _repository))
            throw new UpdateException("Адрес установщика не относится к официальному репозиторию Waundbox — скачивание отменено.");

        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, $"Waundbox-Setup-{release.Version}.exe");
        var partPath = finalPath + ".part";

        try
        {
            using var response = await _http.GetAsync(release.InstallerUrl!, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"Сервер ответил ошибкой {(int)response.StatusCode} при скачивании обновления.");

            var total = response.Content.Headers.ContentLength ?? release.InstallerSize;
            if (total is > MaxInstallerBytes) throw new UpdateException("Файл обновления слишком большой — скачивание отменено.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > MaxInstallerBytes) throw new UpdateException("Файл обновления слишком большой — скачивание отменено.");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (total is > 0) progress?.Report(Math.Min(1.0, (double)received / total.Value));
                }
            }

            if (release.InstallerSize is { } size && received != size)
                throw new UpdateException("Файл обновления скачался не полностью. Попробуйте ещё раз.");

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn($"Контрольная сумма установщика не совпала (ожидалась {expected}, получена {actual}).");
                throw new UpdateException("Скачанный файл не совпал с контрольной суммой и не будет запущен. Попробуйте ещё раз или скачайте установщик со страницы релиза.");
            }

            File.Move(partPath, finalPath, overwrite: true);
            progress?.Report(1.0);
            _logger.Info($"Установщик {release.Version} скачан и проверен.");
            return finalPath;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException("Не удалось скачать обновление: проверьте подключение к интернету и свободное место на диске.", ex);
        }
        finally
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
