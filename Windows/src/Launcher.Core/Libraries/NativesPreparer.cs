using Launcher.Core.Installation;

namespace Launcher.Core.Libraries;

/// <summary>Распаковка нативных библиотек версии в папку natives (нужна старым версиям с классификаторами natives).</summary>
public static class NativesPreparer
{
    public static bool HasNatives(IReadOnlyList<ResolvedLibrary> libraries) =>
        libraries.Any(l => l.NativeArtifact is not null);

    /// <summary>Пересоздаёт папку natives и распаковывает в неё все нужные архивы.</summary>
    public static void Prepare(MinecraftLayout layout, string versionId, IReadOnlyList<ResolvedLibrary> libraries)
    {
        var nativesDir = layout.NativesDirectory(versionId);
        var natives = libraries.Where(l => l.NativeArtifact is not null).ToList();
        if (natives.Count == 0) return;

        if (Directory.Exists(nativesDir)) Directory.Delete(nativesDir, recursive: true);
        foreach (var lib in natives)
            NativesExtractor.Extract(layout.LibraryPath(lib.NativeArtifact!.Path!), nativesDir, lib.ExtractExclude);
    }

    /// <summary>true, если natives нужны, но папка отсутствует или пуста.</summary>
    public static bool IsMissing(MinecraftLayout layout, string versionId, IReadOnlyList<ResolvedLibrary> libraries)
    {
        if (!HasNatives(libraries)) return false;
        var dir = layout.NativesDirectory(versionId);
        return !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();
    }
}
