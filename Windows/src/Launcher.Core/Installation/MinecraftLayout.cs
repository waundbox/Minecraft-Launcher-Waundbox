using Launcher.Core.Errors;

namespace Launcher.Core.Installation;

/// <summary>Структура папок игры. Все пути из метаданных проверяются: выйти за пределы папки игры нельзя.</summary>
public sealed class MinecraftLayout
{
    public string Root { get; }

    public MinecraftLayout(string root) => Root = Path.GetFullPath(root);

    public string VersionsDirectory => Path.Combine(Root, "versions");
    public string LibrariesDirectory => Path.Combine(Root, "libraries");
    public string AssetsDirectory => Path.Combine(Root, "assets");
    public string AssetIndexesDirectory => Path.Combine(AssetsDirectory, "indexes");
    public string AssetObjectsDirectory => Path.Combine(AssetsDirectory, "objects");

    public string VersionDirectory(string versionId) => SafeCombine(VersionsDirectory, versionId);
    public string VersionJsonPath(string versionId) => Path.Combine(VersionDirectory(versionId), versionId + ".json");
    public string ClientJarPath(string versionId) => Path.Combine(VersionDirectory(versionId), versionId + ".jar");
    public string NativesDirectory(string versionId) => Path.Combine(VersionDirectory(versionId), "natives");

    public string InstancesDirectory => Path.Combine(Root, "instances");

    /// <summary>Папка игры конкретного профиля (saves, mods, options.txt...). Для каждого профиля своя.</summary>
    public string InstanceDirectory(string profileName) => SafeCombine(InstancesDirectory, profileName);

    public string LogConfigPath(string fileId) => SafeCombine(Path.Combine(AssetsDirectory, "log_configs"), fileId);

    /// <summary>Папка «виртуальных» ресурсов для старых версий (assets/virtual/{id}).</summary>
    public string VirtualAssetsDirectory(string indexId) => SafeCombine(Path.Combine(AssetsDirectory, "virtual"), indexId);

    public string LibraryPath(string relativePath) => SafeCombine(LibrariesDirectory, relativePath);
    public string AssetIndexPath(string indexId) => SafeCombine(AssetIndexesDirectory, indexId + ".json");

    public string AssetObjectPath(string sha1Hash)
    {
        if (sha1Hash.Length != 40 || !sha1Hash.All(Uri.IsHexDigit))
            throw new VersionMetadataException($"Некорректный хеш ресурса \"{sha1Hash}\".");
        return Path.Combine(AssetObjectsDirectory, sha1Hash[..2], sha1Hash);
    }

    internal static string SafeCombine(string baseDirectory, string relative)
    {
        var baseFull = Path.GetFullPath(baseDirectory);
        var full = Path.GetFullPath(Path.Combine(baseFull, relative));
        var prefix = baseFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new VersionMetadataException($"Небезопасный путь в метаданных: \"{relative}\".");
        return full;
    }
}
