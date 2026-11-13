using System.Text.RegularExpressions;
using Launcher.Core.Errors;
using Launcher.Core.Libraries;

namespace Launcher.Core.GameLaunch;

/// <summary>Вычисляет итоговые JVM- и игровые аргументы: применяет правила и подставляет переменные.</summary>
public static class LaunchArgumentBuilder
{
    private static readonly Regex Placeholder = new(@"\$\{([A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant);

    /// <param name="features">Включённые возможности (например "is_demo_user"). Невключённые считаются выключенными.</param>
    /// <exception cref="LaunchException">В аргументах осталась переменная, значение которой лаунчер не знает.</exception>
    public static (IReadOnlyList<string> Jvm, IReadOnlyList<string> Game) Build(
        LaunchArguments arguments,
        PlatformInfo platform,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlySet<string> features) =>
        (Expand(arguments.Jvm, platform, variables, features), Expand(arguments.Game, platform, variables, features));

    /// <summary>Правила проверяются по порядку, решает последнее подошедшее; без правил аргумент разрешён.</summary>
    public static bool IsAllowed(IReadOnlyList<ArgumentRule> rules, PlatformInfo platform, IReadOnlySet<string> features)
    {
        if (rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
            if (Matches(rule, platform, features)) allowed = rule.Allow;
        return allowed;
    }

    /// <summary>Заменяет ${имя} значениями. Неизвестная переменная — ошибка, а не молчаливый мусор в командной строке.</summary>
    public static string Substitute(string template, IReadOnlyDictionary<string, string> variables)
    {
        var unknown = new List<string>();
        var result = Placeholder.Replace(template, m =>
        {
            if (variables.TryGetValue(m.Groups[1].Value, out var value)) return value;
            unknown.Add(m.Groups[1].Value);
            return m.Value;
        });

        if (unknown.Count > 0)
            throw new LaunchException(
                $"В аргументах запуска есть неизвестная лаунчеру переменная: {string.Join(", ", unknown.Distinct())}.");
        return result;
    }

    private static List<string> Expand(
        IReadOnlyList<ArgumentItem> items, PlatformInfo platform,
        IReadOnlyDictionary<string, string> variables, IReadOnlySet<string> features)
    {
        var result = new List<string>();
        foreach (var item in items)
        {
            if (!IsAllowed(item.Rules, platform, features)) continue;
            foreach (var value in item.Values)
                result.Add(Substitute(value, variables));
        }
        return result;
    }

    private static bool Matches(ArgumentRule rule, PlatformInfo p, IReadOnlySet<string> features)
    {
        if (rule.OsName is not null && rule.OsName != p.OsName) return false;
        if (rule.OsArch is not null && rule.OsArch != p.Arch) return false;
        if (rule.OsVersion is not null)
        {
            if (p.OsVersion is null) return false;
            try { if (!Regex.IsMatch(p.OsVersion, rule.OsVersion)) return false; }
            catch (ArgumentException) { return false; }
        }
        foreach (var (name, required) in rule.Features)
            if (features.Contains(name) != required) return false;
        return true;
    }
}
