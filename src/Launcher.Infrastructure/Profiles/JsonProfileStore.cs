using System.Text.Json;
using Launcher.Core.Errors;
using Launcher.Core.Profiles;

namespace Launcher.Infrastructure.Profiles;

/// <summary>
/// Хранит профили в JSON-файле. Повреждённый файл НЕ сбрасывается молча (иначе следующее сохранение
/// стёрло бы профили пользователя): вместо этого выдаётся понятная ошибка.
/// </summary>
public sealed class JsonProfileStore : IProfileStore
{
    internal sealed record ProfilesFile(List<GameProfile>? Profiles);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonProfileStore(string path) => _path = path;

    public async Task<IReadOnlyList<GameProfile>> ListAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return (await ReadAsync(ct).ConfigureAwait(false)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        finally { _lock.Release(); }
    }

    public async Task<GameProfile?> FindAsync(string name, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await ReadAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(GameProfile profile, CancellationToken ct)
    {
        profile.Validate();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profiles = await ReadAsync(ct).ConfigureAwait(false);
            profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            profiles.Add(profile);
            await WriteAsync(profiles, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profiles = await ReadAsync(ct).ConfigureAwait(false);
            var removed = profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) await WriteAsync(profiles, ct).ConfigureAwait(false);
            return removed;
        }
        finally { _lock.Release(); }
    }

    private async Task<List<GameProfile>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new List<GameProfile>();
        try
        {
            await using var stream = File.OpenRead(_path);
            var file = await JsonSerializer.DeserializeAsync<ProfilesFile>(stream, Options, ct).ConfigureAwait(false);
            return file?.Profiles ?? new List<GameProfile>();
        }
        catch (JsonException ex)
        {
            throw new ProfileException(
                $"Файл профилей повреждён: {_path}. Исправьте его вручную или переименуйте/удалите, чтобы начать заново.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileException($"Не удалось прочитать файл профилей: {_path} ({ex.Message}).", ex);
        }
    }

    private async Task WriteAsync(List<GameProfile> profiles, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new ProfilesFile(profiles), Options), ct).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileException($"Не удалось сохранить файл профилей: {_path} ({ex.Message}).", ex);
        }
    }
}
