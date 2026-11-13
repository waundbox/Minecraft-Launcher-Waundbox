namespace Launcher.Core.Mods;

/// <summary>Доступ к каталогу Modrinth (https://docs.modrinth.com/api/).</summary>
public interface IModrinthClient
{
    Task<ModSearchResult> SearchAsync(ModSearchQuery query, CancellationToken ct);

    /// <summary>Версии проекта, подходящие под версию игры (и загрузчик для модов). Порядок не гарантируется.</summary>
    Task<IReadOnlyList<ModVersion>> GetVersionsAsync(string projectId, string gameVersion, string? loader, CancellationToken ct);

    /// <summary>Одна версия по идентификатору или null, если её нет.</summary>
    Task<ModVersion?> GetVersionAsync(string versionId, CancellationToken ct);

    /// <summary>Проекты по идентификаторам или slug. Неизвестные пропускаются.</summary>
    Task<IReadOnlyList<ModProject>> GetProjectsAsync(IReadOnlyCollection<string> idsOrSlugs, CancellationToken ct);
}
