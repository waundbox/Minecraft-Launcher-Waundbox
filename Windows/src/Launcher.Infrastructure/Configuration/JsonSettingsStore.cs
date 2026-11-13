using System.Text.Json;
using Launcher.Core.Configuration;
using Launcher.Core.Logging;

namespace Launcher.Infrastructure.Configuration;

/// <summary>Хранит настройки в JSON-файле. Повреждённый файл не ломает лаунчер: берутся настройки по умолчанию.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILauncherLogger _logger;

    public JsonSettingsStore(string path, ILauncherLogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    public async Task<LauncherSettings> LoadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_path)) return new LauncherSettings();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<LauncherSettings>(stream, Options, ct).ConfigureAwait(false)
                   ?? new LauncherSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Warn("Файл настроек повреждён или недоступен, используются настройки по умолчанию.", ex);
            return new LauncherSettings();
        }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, Options), ct).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
    }
}
