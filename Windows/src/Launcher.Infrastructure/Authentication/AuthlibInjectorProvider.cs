using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Core.Authentication;
using Launcher.Core.Errors;
using Launcher.Core.Logging;

namespace Launcher.Infrastructure.Authentication;

/// <summary>
/// Скачивает authlib-injector (https://github.com/yushijinhun/authlib-injector) по адресу из latest.json
/// и проверяет SHA-256 из того же ответа. Без совпадения суммы файл не используется.
/// Раз в час проверяет новую версию; без интернета берёт уже скачанный файл.
/// </summary>
public sealed class AuthlibInjectorProvider : IAuthlibInjectorProvider
{
    public static readonly Uri LatestUrl = new("https://authlib-injector.yushi.moe/artifact/latest.json");
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly ILauncherLogger _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedPath;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    public AuthlibInjectorProvider(HttpClient http, string directory, ILauncherLogger? logger = null, TimeProvider? clock = null)
    {
        _http = http;
        _directory = directory;
        _logger = logger ?? NullLauncherLogger.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string> GetJarPathAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedPath is not null && File.Exists(_cachedPath) && _clock.GetUtcNow() - _lastCheck < CheckInterval)
                return _cachedPath;

            try
            {
                _cachedPath = await DownloadLatestAsync(ct).ConfigureAwait(false);
                _lastCheck = _clock.GetUtcNow();
                return _cachedPath;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.Warn("Не удалось проверить обновление authlib-injector.", ex);
                var existing = FindNewestLocal();
                if (existing is null)
                    throw new AuthException("Для входа через Ely.by нужен authlib-injector, но скачать его не удалось. Проверьте интернет.", ex);
                _cachedPath = existing;
                _lastCheck = _clock.GetUtcNow();
                return existing;
            }
        }
        finally { _lock.Release(); }
    }

    private async Task<string> DownloadLatestAsync(CancellationToken ct)
    {
        string version, url, sha256;
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(LatestUrl, ct).ConfigureAwait(false));
            var root = doc.RootElement;
            version = root.GetProperty("version").GetString() ?? "";
            url = root.GetProperty("download_url").GetString() ?? "";
            sha256 = root.GetProperty("checksums").GetProperty("sha256").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AuthException("Сервис authlib-injector вернул неожиданный ответ.", ex);
        }

        if (version.Length == 0 || sha256.Length != 64 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new AuthException("Сервис authlib-injector вернул некорректные данные.");
        if (version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Contains(".."))
            throw new AuthException("Сервис authlib-injector вернул некорректную версию.");

        var target = Path.Combine(_directory, $"authlib-injector-{version}.jar");
        if (File.Exists(target) && await Sha256Async(target, ct).ConfigureAwait(false) == sha256.ToLowerInvariant())
            return target;

        Directory.CreateDirectory(_directory);
        var temp = target + ".tmp";
        try
        {
            await using (var net = await _http.GetStreamAsync(uri, ct).ConfigureAwait(false))
            await using (var file = File.Create(temp))
                await net.CopyToAsync(file, ct).ConfigureAwait(false);

            if (await Sha256Async(temp, ct).ConfigureAwait(false) != sha256.ToLowerInvariant())
                throw new AuthException("Контрольная сумма authlib-injector не совпала. Файл отброшен.");
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        _logger.Info($"authlib-injector {version} скачан.");
        return target;
    }

    private string? FindNewestLocal()
    {
        if (!Directory.Exists(_directory)) return null;
        return Directory.GetFiles(_directory, "authlib-injector-*.jar")
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}
