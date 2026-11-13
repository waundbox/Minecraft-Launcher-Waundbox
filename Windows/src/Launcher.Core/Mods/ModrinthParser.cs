using System.Globalization;
using System.Text.Json;
using Launcher.Core.Errors;

namespace Launcher.Core.Mods;

/// <summary>Разбор ответов Modrinth. Лишние и отсутствующие необязательные поля не считаются ошибкой.</summary>
public static class ModrinthParser
{
    public static ModSearchResult ParseSearch(string json)
    {
        var root = Parse(json);
        var hits = new List<ModProject>();
        if (root.TryGetProperty("hits", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                if (TryParseProject(item, out var project)) hits.Add(project);

        var total = root.TryGetProperty("total_hits", out var t) && t.TryGetInt32(out var n) ? n : hits.Count;
        return new ModSearchResult(hits, total);
    }

    public static IReadOnlyList<ModProject> ParseProjects(string json)
    {
        var root = Parse(json);
        var result = new List<ModProject>();
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray())
                if (TryParseProject(item, out var project)) result.Add(project);
        return result;
    }

    public static IReadOnlyList<ModVersion> ParseVersions(string json)
    {
        var root = Parse(json);
        var result = new List<ModVersion>();
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray())
                if (TryParseVersion(item, out var version)) result.Add(version);
        return result;
    }

    public static ModVersion? ParseVersion(string json) =>
        TryParseVersion(Parse(json), out var version) ? version : null;

    private static JsonElement Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ModException("Modrinth вернул ответ в непонятном формате.", ex);
        }
    }

    private static bool TryParseProject(JsonElement item, out ModProject project)
    {
        project = null!;
        if (item.ValueKind != JsonValueKind.Object) return false;

        // В поиске идентификатор называется project_id, в ответе /projects — id.
        var id = Str(item, "project_id") ?? Str(item, "id");
        var title = Str(item, "title");
        if (id is null || title is null) return false;

        project = new ModProject(
            id, Str(item, "slug"), title, Str(item, "description") ?? "", Str(item, "author"), Str(item, "icon_url"),
            item.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var downloads) ? downloads : 0,
            ModContentTypes.FromProjectType(Str(item, "project_type")));
        return true;
    }

    private static bool TryParseVersion(JsonElement item, out ModVersion version)
    {
        version = null!;
        if (item.ValueKind != JsonValueKind.Object) return false;

        var id = Str(item, "id");
        var projectId = Str(item, "project_id");
        if (id is null || projectId is null) return false;

        // Версии, которые не опубликованы (черновики, архив), не предлагаются.
        var status = Str(item, "status");
        if (status is not null && status is not ("listed" or "unlisted" or "unknown")) return false;

        var file = PickFile(item);
        if (file is null) return false;

        var channel = Str(item, "version_type") switch
        {
            "beta" => ModReleaseChannel.Beta,
            "alpha" => ModReleaseChannel.Alpha,
            _ => ModReleaseChannel.Release
        };
        var published = DateTimeOffset.TryParse(Str(item, "date_published"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.MinValue;

        version = new ModVersion(
            id, projectId, Str(item, "name") ?? Str(item, "version_number") ?? id, Str(item, "version_number") ?? "",
            channel, published, Strings(item, "game_versions"), Strings(item, "loaders"), file, ParseDependencies(item));
        return true;
    }

    /// <summary>Основной файл версии; если ни один не помечен основным, берётся первый (так сказано в документации Modrinth).</summary>
    private static ModFile? PickFile(JsonElement item)
    {
        if (!item.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;

        JsonElement? chosen = null;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) continue;
            chosen ??= file;
            if (file.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True)
            {
                chosen = file;
                break;
            }
        }
        if (chosen is not { } f) return null;

        var url = Str(f, "url");
        var name = Str(f, "filename");
        if (url is null || name is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        string? sha1 = null, sha512 = null;
        if (f.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
        {
            sha1 = Str(hashes, "sha1");
            sha512 = Str(hashes, "sha512");
        }
        var size = f.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
        return new ModFile(uri, name, sha1, sha512, size);
    }

    private static List<ModDependency> ParseDependencies(JsonElement item)
    {
        var result = new List<ModDependency>();
        if (!item.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Array) return result;

        foreach (var dep in deps.EnumerateArray())
        {
            if (dep.ValueKind != JsonValueKind.Object) continue;
            var kind = Str(dep, "dependency_type") switch
            {
                "required" => ModDependencyKind.Required,
                "optional" => ModDependencyKind.Optional,
                "incompatible" => ModDependencyKind.Incompatible,
                "embedded" => ModDependencyKind.Embedded,
                _ => (ModDependencyKind?)null
            };
            if (kind is null) continue;
            result.Add(new ModDependency(Str(dep, "project_id"), Str(dep, "version_id"), kind.Value));
        }
        return result;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Strings(JsonElement element, string name)
    {
        var list = new List<string>();
        if (element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var v in array.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s) list.Add(s);
        return list;
    }
}
