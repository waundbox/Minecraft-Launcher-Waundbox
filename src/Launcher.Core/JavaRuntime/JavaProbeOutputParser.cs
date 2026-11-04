using System.Text.RegularExpressions;

namespace Launcher.Core.JavaRuntime;

/// <summary>Данные о Java, извлечённые из вывода "java -XshowSettings:properties -version".</summary>
public sealed record JavaProbeInfo(int Major, string Version, string? Vendor, bool Is64Bit);

/// <summary>Разбор вывода запуска Java (он идёт в stderr).</summary>
public static class JavaProbeOutputParser
{
    private static readonly Regex PropertyLine =
        new(@"^\s+([A-Za-z0-9_.]+) = (.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex VersionLine =
        new("(?:openjdk|java) version \"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MajorRegex = new(@"^(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant);

    /// <returns>Информация о Java или null, если вывод не похож на вывод Java.</returns>
    public static JavaProbeInfo? Parse(string output)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in PropertyLine.Matches(output))
            props.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Trim());

        props.TryGetValue("java.version", out var version);
        if (string.IsNullOrWhiteSpace(version))
        {
            var fallback = VersionLine.Match(output);
            if (fallback.Success) version = fallback.Groups[1].Value;
        }
        if (string.IsNullOrWhiteSpace(version)) return null;

        var major = ParseMajor(version);
        if (major is null) return null;

        props.TryGetValue("java.vendor", out var vendor);

        bool is64;
        if (props.TryGetValue("sun.arch.data.model", out var model))
            is64 = model == "64";
        else if (props.TryGetValue("os.arch", out var arch))
            is64 = arch is "amd64" or "x86_64" or "aarch64" or "arm64";
        else
            is64 = output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase);

        return new JavaProbeInfo(major.Value, version, string.IsNullOrWhiteSpace(vendor) ? null : vendor, is64);
    }

    /// <summary>"1.8.0_392" → 8, "17.0.9" → 17, "21" → 21, "21-ea" → 21.</summary>
    public static int? ParseMajor(string version)
    {
        var m = MajorRegex.Match(version.Trim());
        if (!m.Success) return null;
        var first = int.Parse(m.Groups[1].Value);
        if (first == 1 && m.Groups[2].Success) return int.Parse(m.Groups[2].Value);
        return first;
    }
}
