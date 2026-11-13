using System.Security.Cryptography;

namespace Launcher.Core.Downloads;

/// <summary>Проверка файлов по размеру и SHA-1.</summary>
public static class FileIntegrity
{
    /// <summary>
    /// true, если файл существует и совпадает со ВСЕМИ указанными ожиданиями.
    /// Если не указано ни размера, ни суммы — проверять нечем, поэтому файл НЕ считается исправным.
    /// </summary>
    public static async Task<bool> IsKnownGoodAsync(string path, string? sha1, long? size, CancellationToken ct)
    {
        if (sha1 is null && size is null) return false;
        return await MatchesAsync(path, sha1, size, ct).ConfigureAwait(false);
    }

    /// <summary>true, если файл существует и совпадает с теми ожиданиями, которые указаны (если не указаны — true).</summary>
    public static async Task<bool> MatchesAsync(string path, string? sha1, long? size, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            if (size is not null && info.Length != size) return false;
            if (sha1 is null) return true;
            var actual = await ComputeSha1Async(path, ct).ConfigureAwait(false);
            return string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // файл занят или недоступен — проверить не удалось
        }
    }

    /// <summary>SHA-1 файла в нижнем регистре (hex).</summary>
    public static async Task<string> ComputeSha1Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
