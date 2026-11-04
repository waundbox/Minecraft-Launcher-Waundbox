using System.Text.Encodings.Web;
using System.Text.Json;

namespace Launcher.Core.Skins;

/// <summary>Правка строки resourcePacks в options.txt (список включённых ресурспаков). Остальные строки не затрагиваются.</summary>
public static class OptionsFile
{
    private const string Key = "resourcePacks:";

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string PackEntry(string packFileName) => "file/" + packFileName;

    /// <summary>Включает ресурспак (в конец списка — это высший приоритет). false — строку не удалось разобрать, текст не изменён.</summary>
    public static bool TryEnable(string? text, string entry, out string result)
    {
        result = text ?? "";
        var newline = result.Contains("\r\n") ? "\r\n" : "\n";

        if (string.IsNullOrEmpty(text))
        {
            result = $"{Key}{Serialize(new List<string> { "vanilla", entry })}{newline}";
            return true;
        }

        var lines = SplitLines(text);
        var index = lines.FindIndex(l => l.StartsWith(Key, StringComparison.Ordinal));
        if (index < 0)
        {
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add(Key + Serialize(new List<string> { "vanilla", entry }));
            result = string.Join(newline, lines) + newline;
            return true;
        }

        if (!TryParseList(lines[index], out var packs)) return false;
        if (!packs.Contains(entry, StringComparer.Ordinal)) packs.Add(entry);
        lines[index] = Key + Serialize(packs);
        result = string.Join(newline, lines);
        return true;
    }

    /// <summary>Выключает ресурспак. false — строку не удалось разобрать, текст не изменён.</summary>
    public static bool TryDisable(string text, string entry, out string result)
    {
        result = text;
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLines(text);
        var index = lines.FindIndex(l => l.StartsWith(Key, StringComparison.Ordinal));
        if (index < 0) return true;

        if (!TryParseList(lines[index], out var packs)) return false;
        packs.RemoveAll(p => string.Equals(p, entry, StringComparison.Ordinal));
        lines[index] = Key + Serialize(packs);
        result = string.Join(newline, lines);
        return true;
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').ToList();

    private static string Serialize(List<string> packs) => JsonSerializer.Serialize(packs, JsonOptions);

    private static bool TryParseList(string line, out List<string> packs)
    {
        packs = new List<string>();
        try
        {
            packs = JsonSerializer.Deserialize<List<string>>(line[Key.Length..].Trim()) ?? new List<string>();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
