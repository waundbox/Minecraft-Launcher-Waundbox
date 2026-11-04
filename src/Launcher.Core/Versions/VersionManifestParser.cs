using System.Text.Json;
using Launcher.Core.Errors;

namespace Launcher.Core.Versions;

/// <summary>Разбор JSON манифеста версий (формат version_manifest_v2.json).</summary>
public static class VersionManifestParser
{
    /// <exception cref="ManifestFormatException">JSON повреждён или не содержит обязательных полей.</exception>
    public static VersionManifest Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ManifestFormatException("Манифест версий: корневой элемент не является объектом.");

            string? latestRelease = null, latestSnapshot = null;
            if (root.TryGetProperty("latest", out var latest) && latest.ValueKind == JsonValueKind.Object)
            {
                latestRelease = GetOptionalString(latest, "release");
                latestSnapshot = GetOptionalString(latest, "snapshot");
            }

            if (!root.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
                throw new ManifestFormatException("Манифест версий: отсутствует массив \"versions\".");

            var list = new List<VersionEntry>(versions.GetArrayLength());
            foreach (var item in versions.EnumerateArray())
                list.Add(ParseEntry(item));

            return new VersionManifest(latestRelease, latestSnapshot, list);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new ManifestFormatException("Манифест версий повреждён и не может быть разобран.", ex);
        }
    }

    private static VersionEntry ParseEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new ManifestFormatException("Манифест версий: элемент списка версий не является объектом.");

        var id = GetRequiredString(item, "id");
        var rawType = GetRequiredString(item, "type");
        var urlText = GetRequiredString(item, "url");

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            throw new ManifestFormatException($"Манифест версий: некорректный адрес у версии {id}.");

        return new VersionEntry(
            id,
            VersionTypeParser.Parse(rawType),
            rawType,
            url,
            GetRequiredDate(item, "time", id),
            GetRequiredDate(item, "releaseTime", id),
            GetOptionalString(item, "sha1"));
    }

    private static string GetRequiredString(JsonElement obj, string name)
    {
        var value = GetOptionalString(obj, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new ManifestFormatException($"Манифест версий: у записи нет обязательного поля \"{name}\".");
        return value;
    }

    private static string? GetOptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static DateTimeOffset GetRequiredDate(JsonElement obj, string name, string versionId)
    {
        if (obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTimeOffset(out var date))
            return date;
        throw new ManifestFormatException($"Манифест версий: у версии {versionId} некорректное поле \"{name}\".");
    }
}
