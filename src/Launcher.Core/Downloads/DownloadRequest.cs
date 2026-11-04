namespace Launcher.Core.Downloads;

/// <summary>Что и куда скачать. Контрольная сумма и размер берутся из метаданных, если они там есть.</summary>
/// <param name="Url">Адрес источника (из метаданных, не угадывается).</param>
/// <param name="DestinationPath">Полный путь итогового файла.</param>
/// <param name="Sha1">Ожидаемый SHA-1 (hex) или null.</param>
/// <param name="Size">Ожидаемый размер в байтах или null.</param>
public sealed record DownloadRequest(Uri Url, string DestinationPath, string? Sha1 = null, long? Size = null);

/// <summary>Что произошло с файлом.</summary>
public enum DownloadOutcome
{
    /// <summary>Файл скачан и проверен.</summary>
    Downloaded,
    /// <summary>Файл уже был на диске и прошёл проверку — скачивание не потребовалось.</summary>
    AlreadyValid
}

/// <summary>Скачивает один файл с проверкой целостности.</summary>
public interface IFileDownloader
{
    /// <param name="bytesProgress">Получает приращения скачанных байт (при повторе попытки приращение может быть отрицательным).</param>
    Task<DownloadOutcome> DownloadAsync(DownloadRequest request, IProgress<long>? bytesProgress, CancellationToken cancellationToken);
}
