using System.IO.Compression;
using Launcher.Core.Errors;

namespace Launcher.Core.Libraries;

/// <summary>Распаковка нативных библиотек (dll/so/dylib) из JAR/ZIP-архива.</summary>
public static class NativesExtractor
{
    /// <param name="exclude">Префиксы путей внутри архива, которые не нужно распаковывать (например "META-INF/").</param>
    /// <exception cref="LauncherException">Архив пытается записать файл за пределы целевой папки.</exception>
    public static void Extract(string archivePath, string targetDirectory, IReadOnlyList<string> exclude)
    {
        var targetRoot = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(targetRoot);
        var rootWithSeparator = targetRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // это папка
            if (exclude.Any(prefix => entry.FullName.StartsWith(prefix, StringComparison.Ordinal))) continue;

            var destination = Path.GetFullPath(Path.Combine(targetRoot, entry.FullName));
            if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new LauncherException($"Архив {Path.GetFileName(archivePath)} содержит небезопасный путь \"{entry.FullName}\".");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }
}
