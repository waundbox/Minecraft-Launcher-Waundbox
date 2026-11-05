using Launcher.Core.Logging;

namespace Launcher.Core.Updates;

/// <summary>Проверка обновлений: сравнивает выпуски на GitHub с запущенной версией.</summary>
public sealed class UpdateChecker
{
    private readonly IUpdateClient _client;
    private readonly ILauncherLogger _logger;

    public UpdateChecker(IUpdateClient client, ILauncherLogger? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    /// <summary>Спрашивает у GitHub список выпусков и решает, есть ли обновление. При ошибке сети бросает <see cref="UpdateException"/>.</summary>
    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct)
    {
        var releases = await _client.GetReleasesAsync(ct).ConfigureAwait(false);
        var result = Evaluate(releases, current);

        // сумма не указана в самом релизе — берём из файла «.sha256», если он приложен
        if (result.Latest is { InstallerSha256: null, HashFileUrl: { } hashUrl } latest)
        {
            try
            {
                var sha = await _client.ReadHashFileAsync(hashUrl, ct).ConfigureAwait(false);
                if (sha is not null) result = result with { Latest = latest with { InstallerSha256 = sha } };
            }
            catch (Exception ex) when (ex is UpdateException)
            {
                _logger.Warn("Не удалось прочитать файл контрольной суммы обновления.", ex);
            }
        }

        _logger.Info(result.IsUpdateAvailable
            ? $"Доступно обновление {result.Latest!.Version} (сейчас {result.Current}){(result.IsRequired ? ", обязательное" : "")}."
            : $"Обновлений нет (версия {result.Current}).");
        return result;
    }

    /// <summary>
    /// Выбирает самый новый выпуск новее текущей версии. Обязательным обновление считается, если обязателен
    /// любой из выпусков новее текущей версии: пропустить обязательный выпуск нельзя, даже если после него вышел обычный.
    /// </summary>
    public static UpdateCheckResult Evaluate(IEnumerable<ReleaseInfo> releases, Version current)
    {
        current = AppVersion.Normalize(current);
        var newer = releases.Where(r => r.Version > current).ToList();
        if (newer.Count == 0) return new UpdateCheckResult(current, null, false);
        var latest = newer.MaxBy(r => r.Version)!;
        return new UpdateCheckResult(current, latest, newer.Any(r => r.IsRequired));
    }
}
