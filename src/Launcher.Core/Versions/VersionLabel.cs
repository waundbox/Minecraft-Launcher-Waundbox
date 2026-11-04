using System.Text.RegularExpressions;

namespace Launcher.Core.Versions;

public enum LoaderKind { Vanilla, Fabric, Quilt, Forge, NeoForge, Custom }

/// <summary>Что скрывается за идентификатором версии профиля: чистая игра, Fabric, Quilt, Forge или NeoForge.</summary>
/// <param name="LoaderVersion">Версия загрузчика (для Vanilla — null).</param>
public sealed record VersionLabel(LoaderKind Loader, string GameVersion, string? LoaderVersion)
{
    private static readonly Regex FabricPattern = new(
        @"^fabric-loader-(?<loader>\d+\.\d+\.\d+(?:-[A-Za-z]+\.\d+)?)-(?<game>.+)$", RegexOptions.CultureInvariant);

    // Профиль Quilt устроен так же, как у Fabric: «quilt-loader-0.26.0-1.20.1».
    private static readonly Regex QuiltPattern = new(
        @"^quilt-loader-(?<loader>\d+\.\d+\.\d+(?:-[A-Za-z]+\.\d+)?)-(?<game>.+)$", RegexOptions.CultureInvariant);

    // Установщик NeoForge называет версию «neoforge-21.1.172» — версии игры в имени нет, она выводится из номера.
    private static readonly Regex NeoForgePattern = new(@"^neoforge-(?<loader>\d+\.\d+\.[0-9A-Za-z._\-]+)$", RegexOptions.CultureInvariant);

    private static readonly Regex ForgePattern = new(
        @"^(?<game>.+?)-forge-(?<loader>.+)$", RegexOptions.CultureInvariant);

    // Своя версия из jar-файла: «имя+mc1.21.1» (имя и версия игры, на которой она основана).
    private static readonly Regex CustomPattern = new(
        @"^(?<name>[A-Za-z0-9][A-Za-z0-9_.\-]*)\+mc(?<game>[A-Za-z0-9._\-]+)$", RegexOptions.CultureInvariant);

    /// <summary>Разбирает идентификаторы вида "1.21.1", "fabric-loader-0.19.5-1.21.1", "1.21.1-forge-52.1.0", "neoforge-21.1.172".</summary>
    public static VersionLabel Parse(string versionId)
    {
        var custom = CustomPattern.Match(versionId);
        if (custom.Success)
            return new VersionLabel(LoaderKind.Custom, custom.Groups["game"].Value, custom.Groups["name"].Value);

        var fabric = FabricPattern.Match(versionId);
        if (fabric.Success)
            return new VersionLabel(LoaderKind.Fabric, fabric.Groups["game"].Value, fabric.Groups["loader"].Value);

        var quilt = QuiltPattern.Match(versionId);
        if (quilt.Success)
            return new VersionLabel(LoaderKind.Quilt, quilt.Groups["game"].Value, quilt.Groups["loader"].Value);

        var neo = NeoForgePattern.Match(versionId);
        if (neo.Success)
        {
            var loader = neo.Groups["loader"].Value;
            return new VersionLabel(LoaderKind.NeoForge, NeoForge.NeoForgeVersions.GameVersionOf(loader) ?? loader, loader);
        }

        var forge = ForgePattern.Match(versionId);
        if (forge.Success)
            return new VersionLabel(LoaderKind.Forge, forge.Groups["game"].Value, forge.Groups["loader"].Value);

        return new VersionLabel(LoaderKind.Vanilla, versionId, null);
    }

    /// <summary>Короткая подпись для интерфейса: "Fabric 1.21.1".</summary>
    public string Display => Loader switch
    {
        LoaderKind.Fabric => $"Fabric {GameVersion}",
        LoaderKind.Quilt => $"Quilt {GameVersion}",
        LoaderKind.Forge => $"Forge {GameVersion}",
        LoaderKind.NeoForge => $"NeoForge {GameVersion}",
        LoaderKind.Custom => $"{LoaderVersion} · {GameVersion}",
        _ => GameVersion
    };
}
