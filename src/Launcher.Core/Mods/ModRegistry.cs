using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Core.Errors;

namespace Launcher.Core.Mods;

/// <summary>
/// Список файлов, которые поставил лаунчер, в служебном файле папки профиля. Файлы, положенные вручную, лаунчер не трогает:
/// он знает только то, что записал сам.
/// </summary>
public sealed class ModRegistry
{
    public const string FileName = ".waundbox-content.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record FileModel(List<InstalledMod>? Items);

    private readonly string _path;

    public ModRegistry(string instanceDirectory) => _path = Path.Combine(instanceDirectory, FileName);

    public List<InstalledMod> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Options);
            return model?.Items?.Where(i => i is not null && !string.IsNullOrEmpty(i.ProjectId) && !string.IsNullOrEmpty(i.FileName)).ToList() ?? [];
        }
        catch (JsonException)
        {
            // Повреждённый служебный файл не должен ломать установку: лаунчер просто «забывает» прежний список.
            return [];
        }
        catch (IOException ex)
        {
            throw new ModException("Не удалось прочитать список установленных модов профиля.", ex);
        }
    }

    public void Save(IReadOnlyList<InstalledMod> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new FileModel(items.ToList()), Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModException("Не удалось сохранить список установленных модов профиля.", ex);
        }
    }
}
