using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Core.Updates;

/// <summary>Разбор ответа GitHub API со списком выпусков (https://docs.github.com/rest/releases).</summary>
public static partial class ReleaseParser
{
    /// <summary>Строка вида «Обязательно: да» (или «required: yes») в описании релиза делает обновление обязательным.</summary>
    [GeneratedRegex(@"^[ \t]*(?:обязательно|required)[ \t]*:[ \t]*(?:да|yes|true)[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RequiredLine();

    [GeneratedRegex(@"sha-?256[ \t]*[:=]?[ \t]*`?([0-9a-fA-F]{64})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShaInText();

    [GeneratedRegex(@"\b([0-9a-fA-F]{64})\b")]
    private static partial Regex ShaOnly();

    /// <summary>Разбирает массив выпусков; черновики, предварительные выпуски и выпуски без понятного номера пропускаются.</summary>
    public static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new UpdateException("GitHub вернул непонятный ответ при проверке обновлений.", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new UpdateException("GitHub вернул непонятный ответ при проверке обновлений.");

            var result = new List<ReleaseInfo>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var release = ParseRelease(item);
                if (release is not null) result.Add(release);
            }
            return result;
        }
    }

    private static ReleaseInfo? ParseRelease(JsonElement item)
    {
        if (Bool(item, "draft") || Bool(item, "prerelease")) return null;

        var tag = Text(item, "tag_name") ?? "";
        var title = Text(item, "name") ?? "";

        // номер берётся из тега; если тег не похож на версию (например, «Winows-release»), — из названия релиза
        if (!AppVersion.TryParse(tag, out var version, out var preTag) && !AppVersion.TryParse(title, out version, out preTag)) return null;
        if (preTag) return null;

        var body = Text(item, "body") ?? "";
        var required = RequiredLine().IsMatch(body);
        var notes = RequiredLine().Replace(body, "").Trim();

        var page = Text(item, "html_url");
        if (!IsHttps(page)) page = "https://github.com/waundbox/Minecraft-Launcher-Waundbox/releases";

        string? installerName = null, installerUrl = null, sha = null, hashFile = null;
        long? size = null;
        var assets = new List<(string Name, string Url, long? Size, string? Digest)>();
        if (item.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
            foreach (var asset in assetArray.EnumerateArray())
            {
                var name = Text(asset, "name");
                var url = Text(asset, "browser_download_url");
                if (name is null || url is null) continue;
                long? length = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var l) ? l : null;
                assets.Add((name, url, length, Text(asset, "digest")));
            }

        var installers = assets.Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = installers.FirstOrDefault(a => a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase));
        if (chosen.Name is null) chosen = installers.FirstOrDefault();
        if (chosen.Name is not null)
        {
            installerName = chosen.Name;
            installerUrl = chosen.Url;
            size = chosen.Size;

            if (chosen.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                && ShaOnly().Match(digest) is { Success: true } d)
                sha = d.Groups[1].Value.ToLowerInvariant();
            if (sha is null && ShaInText().Match(body) is { Success: true } m)
                sha = m.Groups[1].Value.ToLowerInvariant();
            hashFile = assets.FirstOrDefault(a => string.Equals(a.Name, chosen.Name + ".sha256", StringComparison.OrdinalIgnoreCase)).Url;
        }

        return new ReleaseInfo(AppVersion.Normalize(version), tag, title, notes, page!, required,
            installerName, installerUrl, size, sha, hashFile);
    }

    /// <summary>Достаёт контрольную сумму из текста файла «.sha256» («&lt;hex&gt;  имя файла» и т. п.).</summary>
    public static string? ParseHashFile(string text) =>
        ShaOnly().Match(text ?? "") is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
