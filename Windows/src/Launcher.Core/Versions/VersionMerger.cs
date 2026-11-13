using Launcher.Core.Libraries;

namespace Launcher.Core.Versions;

/// <summary>Объединяет версию-дополнение (Fabric/Forge) с базовой версией Minecraft.</summary>
public static class VersionMerger
{
    /// <summary>
    /// Идентификатор, главный класс и тип берутся у дополнения; ресурсы, клиент и Java — у базовой версии
    /// (если дополнение не задаёт свою Java). Библиотеки дополнения идут первыми и заменяют одноимённые
    /// библиотеки базовой версии (сравнение без учёта версии). Аргументы запуска складываются: сначала базовые, затем дополнения.
    /// </summary>
    public static VersionDetails Merge(VersionDetails parent, VersionOverlay overlay)
    {
        var libraries = new List<Library>(overlay.Libraries);
        var overridden = overlay.Libraries
            .Select(l => MavenCoordinates.KeyWithoutVersion(l.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        libraries.AddRange(parent.Libraries.Where(l => !overridden.Contains(MavenCoordinates.KeyWithoutVersion(l.Name))));

        return new VersionDetails(
            overlay.Id,
            overlay.Type ?? parent.Type,
            overlay.MainClass,
            overlay.InheritsFrom,
            parent.AssetIndex,
            // свой jar: размер и хеш родительского клиента не подходят, проверяется только наличие файла
            overlay.UsesOwnJar ? parent.ClientDownload with { Sha1 = null, Size = null } : parent.ClientDownload,
            overlay.JavaMajorVersion ?? parent.JavaMajorVersion,
            libraries,
            overlay.RawJson,
            overlay.Logging ?? parent.Logging,
            [.. parent.AllRawJson, overlay.RawJson],
            overlay.UsesOwnJar ? overlay.Id : parent.ClientJarId);
    }
}
