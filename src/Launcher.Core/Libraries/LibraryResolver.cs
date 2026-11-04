using System.Text.RegularExpressions;
using Launcher.Core.Versions;

namespace Launcher.Core.Libraries;

/// <summary>Библиотека, подходящая для текущей платформы.</summary>
/// <param name="Artifact">Основной JAR (идёт в classpath), если есть.</param>
/// <param name="NativeArtifact">Архив natives, который нужно распаковать (старые версии), если есть.</param>
public sealed record ResolvedLibrary(
    string Name, DownloadInfo? Artifact, DownloadInfo? NativeArtifact, IReadOnlyList<string> ExtractExclude);

/// <summary>Применяет правила библиотек и выбирает файлы для конкретной ОС и архитектуры.</summary>
public static class LibraryResolver
{
    /// <summary>Правила проверяются по порядку; последнее подошедшее решает. Без правил — разрешено, если ни одно не подошло — запрещено.</summary>
    public static bool IsAllowed(IReadOnlyList<LibraryRule> rules, PlatformInfo platform)
    {
        if (rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
            if (Matches(rule, platform)) allowed = rule.Allow;
        return allowed;
    }

    public static IReadOnlyList<ResolvedLibrary> Resolve(IEnumerable<Library> libraries, PlatformInfo platform)
    {
        var result = new List<ResolvedLibrary>();
        foreach (var lib in libraries)
        {
            if (!IsAllowed(lib.Rules, platform)) continue;

            DownloadInfo? native = null;
            if (lib.Natives.TryGetValue(platform.OsName, out var classifier))
            {
                classifier = classifier.Replace("${arch}", platform.ArchBits, StringComparison.Ordinal);
                lib.Classifiers.TryGetValue(classifier, out native);
            }

            if (lib.Artifact is null && native is null) continue;
            result.Add(new ResolvedLibrary(lib.Name, lib.Artifact, native, lib.ExtractExclude));
        }
        return result;
    }

    private static bool Matches(LibraryRule rule, PlatformInfo p)
    {
        if (rule.HasFeatures) return false;
        if (rule.OsName is not null && rule.OsName != p.OsName) return false;
        if (rule.OsArch is not null && rule.OsArch != p.Arch) return false;
        if (rule.OsVersion is not null)
        {
            if (p.OsVersion is null) return false;
            try { if (!Regex.IsMatch(p.OsVersion, rule.OsVersion)) return false; }
            catch (ArgumentException) { return false; }
        }
        return true;
    }
}
