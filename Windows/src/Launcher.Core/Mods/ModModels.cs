using Launcher.Core.Versions;

namespace Launcher.Core.Mods;

/// <summary>Что ставится: от типа зависят папка профиля и способ подбора версий.</summary>
public enum ModContentType
{
    Mod,
    ResourcePack,
    Shader
}

public static class ModContentTypes
{
    /// <summary>Название типа в Modrinth (<c>project_type</c>).</summary>
    public static string ProjectType(this ModContentType type) => type switch
    {
        ModContentType.Mod => "mod",
        ModContentType.ResourcePack => "resourcepack",
        ModContentType.Shader => "shader",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static ModContentType? FromProjectType(string? value) => value switch
    {
        "mod" => ModContentType.Mod,
        "resourcepack" => ModContentType.ResourcePack,
        "shader" => ModContentType.Shader,
        _ => null
    };

    /// <summary>Папка внутри папки профиля.</summary>
    public static string FolderName(this ModContentType type) => type switch
    {
        ModContentType.Mod => "mods",
        ModContentType.ResourcePack => "resourcepacks",
        ModContentType.Shader => "shaderpacks",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static string Title(this ModContentType type) => type switch
    {
        ModContentType.Mod => "Моды",
        ModContentType.ResourcePack => "Ресурспаки",
        ModContentType.Shader => "Шейдеры",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>
    /// Загрузчик для фильтра Modrinth. Нужен только модам: у ресурспаков и шейдеров он не определяет совместимость.
    /// null для мода означает, что профиль без загрузчика — моды в него не ставятся.
    /// </summary>
    public static string? ModrinthLoader(this LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "fabric", // Quilt Loader загружает моды Fabric; отдельных модов «только для Quilt» мало
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => null
    };
}

/// <summary>Порядок результатов поиска (значения параметра <c>index</c> в Modrinth).</summary>
public enum ModSort
{
    Relevance,
    Downloads,
    Follows,
    Newest,
    Updated
}

/// <param name="Text">Строка поиска (может быть пустой — тогда показываются самые подходящие).</param>
/// <param name="Loader">Загрузчик для модов (fabric/forge); для остальных типов игнорируется.</param>
public sealed record ModSearchQuery(
    string Text,
    ModContentType Type,
    string GameVersion,
    string? Loader,
    ModSort Sort = ModSort.Relevance,
    int Offset = 0,
    int Limit = 20);

public sealed record ModProject(
    string ProjectId,
    string? Slug,
    string Title,
    string Description,
    string? Author,
    string? IconUrl,
    long Downloads,
    ModContentType? Type);

public sealed record ModSearchResult(IReadOnlyList<ModProject> Hits, int TotalHits);

public enum ModDependencyKind { Required, Optional, Incompatible, Embedded }

public sealed record ModDependency(string? ProjectId, string? VersionId, ModDependencyKind Kind);

public sealed record ModFile(Uri Url, string FileName, string? Sha1, string? Sha512, long Size);

public enum ModReleaseChannel { Release = 0, Beta = 1, Alpha = 2 }

public sealed record ModVersion(
    string Id,
    string ProjectId,
    string Name,
    string VersionNumber,
    ModReleaseChannel Channel,
    DateTimeOffset Published,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    ModFile File,
    IReadOnlyList<ModDependency> Dependencies);

/// <summary>Запись об установленном файле. Хранится в служебном файле профиля.</summary>
/// <param name="AutoInstalled">Поставлен как зависимость другого мода, а не по прямому выбору пользователя.</param>
public sealed record InstalledMod(
    string ProjectId,
    string VersionId,
    string Title,
    ModContentType Type,
    string FileName,
    string? Sha1,
    string VersionNumber,
    bool AutoInstalled,
    DateTimeOffset InstalledAt);

/// <summary>Запись об обновлении, которое можно поставить.</summary>
public sealed record ModUpdate(InstalledMod Installed, ModVersion Available);
