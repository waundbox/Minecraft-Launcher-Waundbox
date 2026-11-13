namespace Launcher.Core.Modpacks;

/// <summary>Проверка путей из чужих архивов и сборок: файл обязан остаться внутри папки профиля.</summary>
public static class SafePath
{
    /// <summary>
    /// Склеивает папку профиля и относительный путь из сборки. Возвращает null, если путь опасен:
    /// пустой, абсолютный, с диском, с «..» или выходит за пределы папки.
    /// </summary>
    public static string? Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;

        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':')) return null;
        if (normalized.Split('/').Any(part => part == "..")) return null;
        if (normalized.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized));
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && full.Length > rootFull.Length ? full : null;
    }

    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".ps1", ".msi", ".scr", ".vbs", ".lnk", ".com"
    };

    /// <summary>Файлы, которые можно запустить двойным щелчком: в сборках им делать нечего, они пропускаются.</summary>
    public static bool IsRunnable(string path) => RunnableExtensions.Contains(Path.GetExtension(path));
}
