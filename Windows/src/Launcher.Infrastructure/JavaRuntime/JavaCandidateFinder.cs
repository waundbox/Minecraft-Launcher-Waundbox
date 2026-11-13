using Launcher.Core.JavaRuntime;

namespace Launcher.Infrastructure.JavaRuntime;

/// <summary>Находит файлы, которые могут быть Java (ещё не проверенные запуском).</summary>
public static class JavaCandidateFinder
{
    private const int MaxRuntimeDepth = 4;

    public static IReadOnlyList<(string Path, JavaSource Source)> Find(JavaSearchLocations locations)
    {
        var result = new List<(string, JavaSource)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exe = JavaExecutable.FileName;

        void Add(string path, JavaSource source)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full) && seen.Add(full)) result.Add((full, source));
        }

        if (!string.IsNullOrWhiteSpace(locations.JavaHome))
            Add(Path.Combine(locations.JavaHome.Trim('"'), "bin", exe), JavaSource.JavaHome);

        foreach (var entry in locations.PathEntries)
            TryAdd(() => Add(Path.Combine(entry, exe), JavaSource.Path));

        foreach (var root in locations.InstallRoots)
        {
            foreach (var home in SafeDirectories(root))
            {
                Add(Path.Combine(home, "bin", exe), JavaSource.Installed);
                Add(Path.Combine(home, "Contents", "Home", "bin", exe), JavaSource.Installed); // macOS
            }
        }

        foreach (var root in locations.MojangRuntimeRoots)
            foreach (var path in FindBinJava(root, exe, MaxRuntimeDepth))
                Add(path, JavaSource.MojangRuntime);

        return result;
    }

    private static IEnumerable<string> FindBinJava(string directory, string exe, int depth)
    {
        if (!Directory.Exists(directory)) yield break;

        var candidate = Path.Combine(directory, "bin", exe);
        if (File.Exists(candidate)) yield return candidate;
        if (depth <= 0) yield break;

        foreach (var child in SafeDirectories(directory))
            foreach (var found in FindBinJava(child, exe, depth - 1))
                yield return found;
    }

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            var directories = Directory.GetDirectories(directory);
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase); // одинаковый порядок на любой ОС
            return directories;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void TryAdd(Action add)
    {
        try { add(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { /* некорректная запись в PATH */ }
    }
}
