using System.IO.Compression;
using Launcher.Core.Downloads;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Logging;

namespace Launcher.Core.Modpacks;

/// <summary>
/// Раскладывает сборку Modrinth по папке профиля: скачивает файлы с проверкой SHA-1 и распаковывает папки overrides.
/// Саму игру и загрузчик ставит вызывающий код (Minecraft/Fabric/Forge по данным <see cref="MrpackInfo"/>).
/// </summary>
public sealed class MrpackImporter
{
    private const int Parallelism = 4;
    private const long MaxExtractedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxExtractedFiles = 100_000;

    // Домены, разрешённые спецификацией для сборок с Modrinth: https://docs.modrinth.com/modpacks/format/
    private static readonly string[] AllowedHosts = ["cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"];

    private readonly IFileDownloader _downloader;
    private readonly MinecraftLayout _layout;
    private readonly ILauncherLogger _logger;

    public MrpackImporter(IFileDownloader downloader, MinecraftLayout layout, ILauncherLogger? logger = null)
    {
        _downloader = downloader;
        _layout = layout;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    public async Task<MrpackImportResult> ImportAsync(
        string profileName, string mrpackPath, MrpackInfo info, IProgress<MrpackProgress>? progress, CancellationToken ct)
    {
        var instance = _layout.InstanceDirectory(profileName);
        Directory.CreateDirectory(instance);
        var warnings = new List<string>();

        if (info.Notes is { Count: > 0 })
        {
            warnings.AddRange(info.Notes);
            // Полный список — в файле рядом с профилем: всплывающие сообщения показывают только первые строки.
            await File.WriteAllLinesAsync(Path.Combine(instance, "waundbox-manual-downloads.txt"), info.Notes, ct).ConfigureAwait(false);
            foreach (var note in info.Notes) _logger.Warn(note);
        }
        var downloaded = await DownloadFilesAsync(instance, info, progress, warnings, ct).ConfigureAwait(false);

        progress?.Report(new MrpackProgress("Копирую файлы настроек из сборки…", 0.95));
        var extracted = ExtractOverrides(mrpackPath, instance, info, warnings, ct);

        progress?.Report(new MrpackProgress("Готово", 1));
        return new MrpackImportResult(downloaded, extracted, warnings);
    }

    private async Task<int> DownloadFilesAsync(
        string instance, MrpackInfo info, IProgress<MrpackProgress>? progress, List<string> warnings, CancellationToken ct)
    {
        var total = info.Files.Count;
        var done = 0;
        var skipped = new System.Collections.Concurrent.ConcurrentBag<string>();

        await Parallel.ForEachAsync(info.Files, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (file, token) =>
            {
                var target = SafePath.Resolve(instance, file.Path)
                    ?? throw new ModException($"Небезопасный путь «{file.Path}» в сборке. Импорт остановлен.");
                if (SafePath.IsRunnable(target))
                {
                    skipped.Add(file.Path);
                }
                else
                {
                    await DownloadOneAsync(file, target, info.ExtraAllowedHosts, token).ConfigureAwait(false);
                }

                var n = Interlocked.Increment(ref done);
                progress?.Report(new MrpackProgress($"Скачиваю файлы сборки: {n} из {total}", 0.9 * n / Math.Max(1, total)));
            }).ConfigureAwait(false);

        foreach (var path in skipped) warnings.Add($"Пропущен исполняемый файл «{path}»: в сборках такие файлы не принимаются.");
        return total - skipped.Count;
    }

    private async Task DownloadOneAsync(MrpackFile file, string target, IReadOnlyList<string>? extraHosts, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        Exception? last = null;
        var tried = false;
        foreach (var url in file.Downloads)
        {
            if (!IsAllowed(url, extraHosts)) continue;
            tried = true;
            try
            {
                await _downloader.DownloadAsync(
                    new DownloadRequest(url, target, string.IsNullOrEmpty(file.Sha1) ? null : file.Sha1, file.Size > 0 ? file.Size : null), null, ct).ConfigureAwait(false);
                return;
            }
            catch (LauncherException ex)
            {
                last = ex;
                _logger.Warn($"Не удалось скачать {file.Path} с {url.Host}, пробую другой адрес.", ex);
            }
        }

        if (!tried)
            throw new ModException($"Файл «{file.Path}» предлагается не с разрешённых серверов (Modrinth, GitHub, GitLab). Импорт остановлен.");
        throw new ModException($"Не удалось скачать файл «{file.Path}» из сборки: {last?.Message}", last!);
    }

    private static bool IsAllowed(Uri url, IReadOnlyList<string>? extraHosts) =>
        url.Scheme == Uri.UriSchemeHttps &&
        (AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase) ||
         (extraHosts?.Contains(url.Host, StringComparer.OrdinalIgnoreCase) ?? false));

    /// <summary>Сначала overrides, затем client-overrides (они перекрывают общие файлы), как сказано в спецификации.</summary>
    private int ExtractOverrides(string mrpackPath, string instance, MrpackInfo info, List<string> warnings, CancellationToken ct)
    {
        var count = 0;
        long bytes = 0;
        try
        {
            using var zip = ZipFile.OpenRead(mrpackPath);
            foreach (var prefix in (info.OverridesFolders?.Select(f => f + "/") ?? new[] { "overrides/", "client-overrides/" }))
            {
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/')) continue;

                    var relative = name[prefix.Length..];
                    var target = SafePath.Resolve(instance, relative);
                    if (target is null)
                        throw new ModException($"В сборке есть файл с небезопасным путём «{entry.FullName}». Импорт остановлен.");
                    if (SafePath.IsRunnable(target))
                    {
                        warnings.Add($"Пропущен исполняемый файл «{relative}».");
                        continue;
                    }

                    if (++count > MaxExtractedFiles || (bytes += entry.Length) > MaxExtractedBytes)
                        throw new ModException("Сборка содержит подозрительно много данных. Импорт остановлен.");

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ModException("Архив сборки повреждён.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModException("Не удалось распаковать файлы сборки в папку профиля.", ex);
        }
        return count;
    }
}
