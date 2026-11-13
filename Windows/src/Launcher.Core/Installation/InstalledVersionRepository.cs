using Launcher.Core.Errors;
using Launcher.Core.Versions;

namespace Launcher.Core.Installation;

/// <summary>Читает описание уже установленной версии с диска.</summary>
public sealed class InstalledVersionRepository
{
    private readonly MinecraftLayout _layout;

    public InstalledVersionRepository(MinecraftLayout layout) => _layout = layout;

    private const int MaxInheritanceDepth = 5;

    /// <summary>Загружает версию; если она наследуется от другой (Fabric, Forge), объединяет всю цепочку.</summary>
    /// <exception cref="VersionNotFoundException">Версия или её базовая версия не установлена.</exception>
    public Task<VersionDetails> LoadAsync(string versionId, CancellationToken ct) =>
        LoadAsync(versionId, null, [], ct);

    private async Task<VersionDetails> LoadAsync(string versionId, string? requiredBy, HashSet<string> chain, CancellationToken ct)
    {
        if (!chain.Add(versionId) || chain.Count > MaxInheritanceDepth)
            throw new VersionMetadataException($"Версия {versionId}: слишком длинная или зацикленная цепочка inheritsFrom.");

        var path = _layout.VersionJsonPath(versionId);
        if (!File.Exists(path))
            throw new VersionNotFoundException(requiredBy is null
                ? $"Версия \"{versionId}\" не установлена. Сначала выполните: install {versionId}"
                : $"Для версии {requiredBy} нужна базовая версия \"{versionId}\", она не установлена. Выполните: install {versionId}");

        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var overlay = VersionDetailsParser.ParseOverlay(json);
        if (string.IsNullOrEmpty(overlay.InheritsFrom))
            return VersionDetailsParser.Parse(json);

        var parent = await LoadAsync(overlay.InheritsFrom, versionId, chain, ct).ConfigureAwait(false);
        return VersionMerger.Merge(parent, overlay);
    }
}
