namespace Launcher.Core.Versions;

/// <summary>Тип версии Minecraft по официальным метаданным.</summary>
public enum VersionType
{
    Release,
    Snapshot,
    OldBeta,
    OldAlpha,
    /// <summary>Тип, которого лаунчер ещё не знает (метаданные могут расширяться).</summary>
    Unknown
}

public static class VersionTypeParser
{
    /// <summary>Преобразует строку из манифеста ("release", "snapshot", "old_beta", "old_alpha").</summary>
    public static VersionType Parse(string? raw) => raw switch
    {
        "release" => VersionType.Release,
        "snapshot" => VersionType.Snapshot,
        "old_beta" => VersionType.OldBeta,
        "old_alpha" => VersionType.OldAlpha,
        _ => VersionType.Unknown
    };
}
