namespace Launcher.Core.Versions;

/// <summary>Откуда на самом деле получен манифест.</summary>
public enum ManifestSource
{
    /// <summary>Свежие данные с сервера Mojang.</summary>
    Network,
    /// <summary>Локальный кэш, срок годности которого ещё не истёк.</summary>
    FreshCache,
    /// <summary>Устаревший локальный кэш: сервер недоступен.</summary>
    StaleCache
}

/// <summary>Результат получения манифеста с информацией об источнике.</summary>
public sealed record ManifestResult(VersionManifest Manifest, ManifestSource Source, DateTimeOffset RetrievedAt);

/// <summary>Источник манифеста версий. Интерфейс позволяет подменять сеть в тестах.</summary>
public interface IVersionManifestProvider
{
    /// <param name="forceRefresh">true — игнорировать срок годности кэша и пробовать сеть.</param>
    /// <exception cref="Errors.ManifestUnavailableException">Нет сети и нет локальной копии.</exception>
    Task<ManifestResult> GetManifestAsync(bool forceRefresh, CancellationToken cancellationToken);
}
