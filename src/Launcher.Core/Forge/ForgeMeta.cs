using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Core.Errors;

namespace Launcher.Core.Forge;

/// <summary>Официальные источники Forge: список рекомендованных сборок и Maven-репозиторий с установщиками.</summary>
public interface IForgeMetaClient
{
    /// <summary>Таблица promotions_slim.json: ключ вида "1.21.1-recommended" → версия Forge.</summary>
    /// <exception cref="ForgeException">Сервис недоступен или формат неожиданный.</exception>
    Task<IReadOnlyDictionary<string, string>> GetPromotionsAsync(CancellationToken ct);

    /// <summary>SHA-1 установщика из Maven-репозитория (файл .sha1) или null, если репозиторий его не публикует.</summary>
    /// <exception cref="ForgeException">Репозиторий недоступен.</exception>
    Task<string?> GetInstallerSha1Async(string gameVersion, string forgeVersion, CancellationToken ct);
}

/// <summary>Разбор списка сборок и построение адреса установщика.</summary>
public static partial class ForgeVersions
{
    public static readonly Uri MavenBase = new("https://maven.minecraftforge.net/net/minecraftforge/forge/");

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._\-]{0,39}$")]
    private static partial Regex SafeVersion();

    /// <exception cref="ForgeException">Формат ответа неожиданный.</exception>
    public static IReadOnlyDictionary<string, string> ParsePromotions(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("promos", out var promos) || promos.ValueKind != JsonValueKind.Object)
                throw new ForgeException("Ответ Forge: нет списка сборок (promos).");

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in promos.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                    result[p.Name] = p.Value.GetString()!;
            return result;
        }
        catch (JsonException ex)
        {
            throw new ForgeException("Ответ Forge не является корректным JSON.", ex);
        }
    }

    /// <summary>Явно заданная версия (если указана) → рекомендованная → последняя.</summary>
    /// <exception cref="ForgeException">Для версии Minecraft нет сборок Forge или версия записана некорректно.</exception>
    public static string Choose(IReadOnlyDictionary<string, string> promos, string gameVersion, string? requested)
    {
        EnsureSafe(gameVersion, "версия Minecraft");
        if (requested is not null)
        {
            EnsureSafe(requested, "версия Forge");
            return requested;
        }

        if (promos.TryGetValue($"{gameVersion}-recommended", out var recommended)) return recommended;
        if (promos.TryGetValue($"{gameVersion}-latest", out var latest)) return latest;
        throw new ForgeException($"Для Minecraft {gameVersion} нет сборок Forge в официальном списке.");
    }

    public static Uri InstallerUri(string gameVersion, string forgeVersion)
    {
        EnsureSafe(gameVersion, "версия Minecraft");
        EnsureSafe(forgeVersion, "версия Forge");
        var full = $"{gameVersion}-{forgeVersion}";
        return new Uri(MavenBase, $"{full}/forge-{full}-installer.jar");
    }

    public static string InstallerFileName(string gameVersion, string forgeVersion) =>
        $"forge-{gameVersion}-{forgeVersion}-installer.jar";

    private static void EnsureSafe(string value, string what)
    {
        if (!SafeVersion().IsMatch(value))
            throw new ForgeException($"Некорректная {what}: \"{value}\".");
    }
}
