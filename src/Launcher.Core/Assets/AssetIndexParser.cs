using System.Text.Json;
using Launcher.Core.Errors;

namespace Launcher.Core.Assets;

/// <summary>Один игровой ресурс: имя в игре, SHA-1 (он же имя файла в хранилище) и размер.</summary>
public sealed record AssetObject(string Name, string Hash, long Size);

/// <summary>Индекс ресурсов целиком.</summary>
/// <param name="IsVirtual">Старый формат: ресурсы нужно разложить по именам в assets/virtual/{id}.</param>
/// <param name="MapToResources">Самый старый формат: ресурсы нужно разложить в папку resources игры.</param>
public sealed record AssetIndex(IReadOnlyList<AssetObject> Objects, bool IsVirtual, bool MapToResources);

/// <summary>Разбор индекса ресурсов (assets/indexes/*.json).</summary>
public static class AssetIndexParser
{
    /// <exception cref="VersionMetadataException">Индекс повреждён.</exception>
    public static IReadOnlyList<AssetObject> Parse(string json) => ParseIndex(json).Objects;

    /// <exception cref="VersionMetadataException">Индекс повреждён.</exception>
    public static AssetIndex ParseIndex(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("objects", out var objects) ||
                objects.ValueKind != JsonValueKind.Object)
                throw new VersionMetadataException("Индекс ресурсов: отсутствует объект \"objects\".");

            var list = new List<AssetObject>();
            foreach (var prop in objects.EnumerateObject())
            {
                var v = prop.Value;
                if (v.ValueKind != JsonValueKind.Object ||
                    !v.TryGetProperty("hash", out var hashEl) || hashEl.ValueKind != JsonValueKind.String ||
                    !v.TryGetProperty("size", out var sizeEl) || !sizeEl.TryGetInt64(out var size))
                    throw new VersionMetadataException($"Индекс ресурсов: некорректная запись \"{prop.Name}\".");

                var hash = hashEl.GetString()!;
                if (hash.Length != 40 || !hash.All(Uri.IsHexDigit))
                    throw new VersionMetadataException($"Индекс ресурсов: некорректный хеш у \"{prop.Name}\".");

                list.Add(new AssetObject(prop.Name, hash.ToLowerInvariant(), size));
            }
            return new AssetIndex(list, ReadFlag(doc.RootElement, "virtual"), ReadFlag(doc.RootElement, "map_to_resources"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new VersionMetadataException("Индекс ресурсов повреждён и не может быть разобран.", ex);
        }
    }

    private static bool ReadFlag(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
