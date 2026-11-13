namespace Launcher.Core.Versions;

/// <summary>Одна запись из version_manifest_v2.json.</summary>
/// <param name="Id">Идентификатор версии, например "1.21.1".</param>
/// <param name="Type">Разобранный тип версии.</param>
/// <param name="RawType">Тип в исходном виде (сохраняем, чтобы не терять неизвестные типы).</param>
/// <param name="Url">Адрес JSON этой версии (берётся из метаданных, не угадывается).</param>
/// <param name="Sha1">SHA-1 JSON версии, если указан.</param>
public sealed record VersionEntry(
    string Id,
    VersionType Type,
    string RawType,
    Uri Url,
    DateTimeOffset Time,
    DateTimeOffset ReleaseTime,
    string? Sha1);

/// <summary>Манифест версий Minecraft.</summary>
public sealed record VersionManifest(
    string? LatestRelease,
    string? LatestSnapshot,
    IReadOnlyList<VersionEntry> Versions)
{
    /// <summary>Ищет версию по идентификатору (регистр важен). Возвращает null, если её нет.</summary>
    public VersionEntry? Find(string id) =>
        Versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.Ordinal));

    /// <summary>Возвращает версии заданного типа или все, если тип не указан. Порядок сохраняется (новые первыми).</summary>
    public IEnumerable<VersionEntry> OfType(VersionType? type) =>
        type is null ? Versions : Versions.Where(v => v.Type == type.Value);
}
