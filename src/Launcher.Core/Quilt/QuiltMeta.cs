using System.Text.Json;
using Launcher.Core.Errors;

namespace Launcher.Core.Quilt;

/// <summary>Версия загрузчика Quilt Loader из официального сервиса meta.quiltmc.org.</summary>
public sealed record QuiltLoaderVersion(string Version, bool Stable);

/// <summary>Доступ к официальному сервису метаданных Quilt (https://meta.quiltmc.org).</summary>
public interface IQuiltMetaClient
{
    /// <summary>Версии загрузчика для версии игры, от новых к старым (порядок сервиса). Пустой список — версия игры не поддерживается.</summary>
    /// <exception cref="QuiltException">Сервис недоступен или ответил неожиданно.</exception>
    Task<IReadOnlyList<QuiltLoaderVersion>> GetLoaderVersionsAsync(string gameVersion, CancellationToken ct);

    /// <summary>JSON профиля запуска (то, что кладётся в versions/{id}/{id}.json).</summary>
    /// <exception cref="QuiltException">Сервис недоступен или ответил неожиданно.</exception>
    Task<string> GetProfileJsonAsync(string gameVersion, string loaderVersion, CancellationToken ct);
}

/// <summary>Разбор списка версий загрузчика: [ { "loader": { "version": "0.26.0" }, ... }, ... ].</summary>
public static class QuiltMetaParser
{
    /// <exception cref="QuiltException">Формат ответа не соответствует ожидаемому.</exception>
    public static IReadOnlyList<QuiltLoaderVersion> ParseLoaders(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new QuiltException("Ответ Quilt: ожидался список версий загрузчика.");

            var result = new List<QuiltLoaderVersion>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("loader", out var loader) || loader.ValueKind != JsonValueKind.Object ||
                    !loader.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(version.GetString()))
                    throw new QuiltException("Ответ Quilt: у записи нет версии загрузчика.");

                // У Quilt нет поля «stable»: предварительные сборки отличаются суффиксом в версии (0.20.0-beta.9).
                var v = version.GetString()!;
                result.Add(new QuiltLoaderVersion(v, !v.Contains('-')));
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new QuiltException("Ответ Quilt не является корректным JSON.", ex);
        }
    }
}
