using System.Text.Json;
using Launcher.Core.Errors;

namespace Launcher.Core.Fabric;

/// <summary>Версия загрузчика Fabric Loader из официального сервиса meta.fabricmc.net.</summary>
public sealed record FabricLoaderVersion(string Version, bool Stable);

/// <summary>Доступ к официальному сервису метаданных Fabric (https://meta.fabricmc.net).</summary>
public interface IFabricMetaClient
{
    /// <summary>Версии загрузчика для версии игры, от новых к старым (порядок сервиса). Пустой список — версия игры не поддерживается.</summary>
    /// <exception cref="FabricException">Сервис недоступен или ответил неожиданно.</exception>
    Task<IReadOnlyList<FabricLoaderVersion>> GetLoaderVersionsAsync(string gameVersion, CancellationToken ct);

    /// <summary>JSON профиля запуска (то, что кладётся в versions/{id}/{id}.json).</summary>
    /// <exception cref="FabricException">Сервис недоступен или ответил неожиданно.</exception>
    Task<string> GetProfileJsonAsync(string gameVersion, string loaderVersion, CancellationToken ct);
}

/// <summary>Разбор списка версий загрузчика: [ { "loader": { "version": "0.19.5", "stable": true }, ... }, ... ].</summary>
public static class FabricMetaParser
{
    /// <exception cref="FabricException">Формат ответа не соответствует ожидаемому.</exception>
    public static IReadOnlyList<FabricLoaderVersion> ParseLoaders(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new FabricException("Ответ Fabric: ожидался список версий загрузчика.");

            var result = new List<FabricLoaderVersion>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("loader", out var loader) || loader.ValueKind != JsonValueKind.Object ||
                    !loader.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(version.GetString()))
                    throw new FabricException("Ответ Fabric: у записи нет версии загрузчика.");

                var stable = loader.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True;
                result.Add(new FabricLoaderVersion(version.GetString()!, stable));
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new FabricException("Ответ Fabric не является корректным JSON.", ex);
        }
    }
}
