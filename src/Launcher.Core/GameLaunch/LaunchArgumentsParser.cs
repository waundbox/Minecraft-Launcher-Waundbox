using System.Text.Json;
using Launcher.Core.Errors;

namespace Launcher.Core.GameLaunch;

/// <summary>
/// Разбор аргументов запуска из JSON версии. Поддерживает оба формата Mojang:
/// новый (объект "arguments" с "jvm" и "game", 1.13+) и старый (строка "minecraftArguments").
/// </summary>
public static class LaunchArgumentsParser
{
    private static readonly string[] LegacyJvmDefaults =
    [
        "-Djava.library.path=${natives_directory}",
        "-Dminecraft.launcher.brand=${launcher_name}",
        "-Dminecraft.launcher.version=${launcher_version}",
        "-cp",
        "${classpath}"
    ];

    /// <exception cref="VersionMetadataException">В JSON нет данных об аргументах или они повреждены.</exception>
    public static LaunchArguments Parse(string versionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(versionJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
            {
                return new LaunchArguments(
                    IsLegacy: false,
                    Jvm: ParseList(args, "jvm"),
                    Game: ParseList(args, "game"));
            }

            if (root.TryGetProperty("minecraftArguments", out var legacy) && legacy.ValueKind == JsonValueKind.String)
            {
                var game = CommandLineSplitter.Split(legacy.GetString())
                    .Select(a => new ArgumentItem([a], []))
                    .ToList();
                var jvm = LegacyJvmDefaults.Select(a => new ArgumentItem([a], [])).ToList();
                return new LaunchArguments(IsLegacy: true, jvm, game);
            }

            throw new VersionMetadataException("В JSON версии нет ни \"arguments\", ни \"minecraftArguments\".");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new VersionMetadataException("Аргументы запуска версии повреждены и не могут быть разобраны.", ex);
        }
    }

    private static IReadOnlyList<ArgumentItem> ParseList(JsonElement arguments, string name)
    {
        var items = new List<ArgumentItem>();
        if (!arguments.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return items;

        foreach (var element in list.EnumerateArray())
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    items.Add(new ArgumentItem([element.GetString()!], []));
                    break;
                case JsonValueKind.Object:
                    items.Add(ParseConditional(element));
                    break;
                default:
                    throw new VersionMetadataException($"Аргументы запуска: неожиданный элемент в списке \"{name}\".");
            }
        }
        return items;
    }

    private static ArgumentItem ParseConditional(JsonElement element)
    {
        var values = new List<string>();
        if (element.TryGetProperty("value", out var value))
        {
            if (value.ValueKind == JsonValueKind.String) values.Add(value.GetString()!);
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var v in value.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String) values.Add(v.GetString()!);
        }

        var rules = new List<ArgumentRule>();
        if (element.TryGetProperty("rules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Array)
            foreach (var rule in rulesEl.EnumerateArray())
                rules.Add(ParseRule(rule));

        return new ArgumentItem(values, rules);
    }

    private static ArgumentRule ParseRule(JsonElement rule)
    {
        var action = rule.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        if (action is not ("allow" or "disallow"))
            throw new VersionMetadataException($"Аргументы запуска: неизвестное действие правила \"{action}\".");

        string? osName = null, osArch = null, osVersion = null;
        if (rule.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.Object)
        {
            osName = GetString(os, "name");
            osArch = GetString(os, "arch");
            osVersion = GetString(os, "version");
        }

        var features = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (rule.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Object)
            foreach (var prop in f.EnumerateObject())
                features[prop.Name] = prop.Value.ValueKind == JsonValueKind.True;

        return new ArgumentRule(action == "allow", osName, osArch, osVersion, features);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
