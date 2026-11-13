using System.Text.Json;
using Launcher.Core.Configuration;

namespace Launcher.UI.Services;

/// <summary>
/// Раскладки редактора по темам: что человек поправил в интерфейсе, лежит под ключом выбранной темы
/// и возвращается, когда эта тема включается снова.
/// </summary>
internal static class ThemeLayoutStore
{
    public sealed record Entry(string? Layout, string? NoModel, string? Nav);

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static Dictionary<string, Entry> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json, Options);
            return map is null ? new(StringComparer.OrdinalIgnoreCase) : new(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    public static Entry? Find(LauncherSettings s, string? key) =>
        key is not null && Read(s.ThemeLayouts).TryGetValue(key, out var e) ? e : null;

    private static LauncherSettings With(LauncherSettings s, Func<Entry, Entry> change)
    {
        if (s.ThemeKey is not { } key) return s;
        var map = Read(s.ThemeLayouts);
        map[key] = change(map.TryGetValue(key, out var e) ? e : new Entry(null, null, null));
        return s with { ThemeLayouts = JsonSerializer.Serialize(map, Options) };
    }

    /// <summary>Записывает раскладку главной (с моделью — 0, без — 1) в настройки и в запись выбранной темы.</summary>
    public static LauncherSettings WithLayout(LauncherSettings s, int space, string? json) =>
        With(space == 0 ? s with { HomeLayout = json } : s with { HomeLayoutNoModel = json },
             e => space == 0 ? e with { Layout = json } : e with { NoModel = json });

    /// <summary>Запоминает расположение меню за выбранной темой.</summary>
    public static LauncherSettings WithNav(LauncherSettings s, string? nav) =>
        With(s with { NavLayout = nav }, e => e with { Nav = nav ?? NavLayouts.Top });
}
