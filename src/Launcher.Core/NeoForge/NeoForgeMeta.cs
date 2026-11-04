using System.Text.RegularExpressions;
using System.Xml.Linq;
using Launcher.Core.Errors;

namespace Launcher.Core.NeoForge;

/// <summary>Официальный Maven-репозиторий NeoForge: список версий и установщики.</summary>
public interface INeoForgeMetaClient
{
    /// <summary>Все версии NeoForge из maven-metadata.xml (от старых к новым), включая сборки для 1.20.1, если их список доступен.</summary>
    /// <exception cref="NeoForgeException">Сервис недоступен или формат неожиданный.</exception>
    Task<IReadOnlyList<string>> GetVersionsAsync(CancellationToken ct);

    /// <summary>SHA-1 установщика из Maven-репозитория (файл .sha1) или null, если репозиторий его не публикует.</summary>
    /// <exception cref="NeoForgeException">Репозиторий недоступен.</exception>
    Task<string?> GetInstallerSha1Async(string neoForgeVersion, CancellationToken ct);
}

/// <summary>
/// Версии NeoForge и адреса установщиков.
/// Схема версий (по документации NeoForge): для Minecraft 1.A.B версия начинается с «A.B.» (1.21.1 → 21.1.x, 1.21 → 21.0.x);
/// для Minecraft 26.1 и новее — с полного номера Minecraft с «0» вместо отсутствующей третьей цифры (26.1 → 26.1.0.x).
/// Версия 1.20.1 устроена иначе: артефакт net.neoforged:forge, версии вида «1.20.1-47.1.106»
/// (список — по адресу <see cref="LegacyMetadataUri"/>). NeoForge сам рекомендует Forge для 1.20.1; поддержка здесь — по просьбе пользователя.
/// </summary>
public static partial class NeoForgeVersions
{
    public static readonly Uri MavenBase = new("https://maven.neoforged.net/releases/net/neoforged/neoforge/");
    public static readonly Uri MetadataUri = new(MavenBase, "maven-metadata.xml");

    /// <summary>Артефакт net.neoforged:forge — сборки NeoForge для Minecraft 1.20.1.</summary>
    public static readonly Uri LegacyMavenBase = new("https://maven.neoforged.net/releases/net/neoforged/forge/");
    public static readonly Uri LegacyMetadataUri = new(LegacyMavenBase, "maven-metadata.xml");

    private const string LegacyPrefix = "1.20.1-";

    /// <summary>Версия 1.20.1 («1.20.1-47.1.106») — из артефакта net.neoforged:forge.</summary>
    public static bool IsLegacy(string neoForgeVersion) => neoForgeVersion.StartsWith(LegacyPrefix, StringComparison.Ordinal);

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._\-]{0,39}$")]
    private static partial Regex SafeVersion();

    [GeneratedRegex(@"^1\.(?<a>\d+)(?:\.(?<b>\d+))?$")]
    private static partial Regex OldGameVersion();

    [GeneratedRegex(@"^(?<maj>\d+)\.(?<min>\d+)(?:\.(?<pat>\d+))?$")]
    private static partial Regex NewGameVersion();

    /// <exception cref="NeoForgeException">Формат неожиданный.</exception>
    public static IReadOnlyList<string> ParseMetadata(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var versions = doc.Descendants("versions").Elements("version")
                .Select(e => e.Value.Trim())
                .Where(v => SafeVersion().IsMatch(v))
                .ToList();
            if (versions.Count == 0) throw new NeoForgeException("Список версий NeoForge пуст.");
            return versions;
        }
        catch (System.Xml.XmlException ex)
        {
            throw new NeoForgeException("Ответ NeoForge не является корректным XML.", ex);
        }
    }

    /// <summary>Начало версий NeoForge для версии Minecraft (с точкой на конце) или null, если версия не поддерживается.</summary>
    public static string? Prefix(string gameVersion)
    {
        var old = OldGameVersion().Match(gameVersion);
        if (old.Success)
        {
            var a = int.Parse(old.Groups["a"].Value);
            var b = old.Groups["b"].Success ? int.Parse(old.Groups["b"].Value) : 0;
            if (a == 20 && b == 1) return LegacyPrefix;
            if (a < 20 || (a == 20 && b < 2)) return null; // раньше 1.20.1 NeoForge не существует
            return $"{a}.{b}.";
        }

        var modern = NewGameVersion().Match(gameVersion);
        if (modern.Success && int.Parse(modern.Groups["maj"].Value) >= 26)
        {
            var pat = modern.Groups["pat"].Success ? modern.Groups["pat"].Value : "0";
            return $"{modern.Groups["maj"].Value}.{modern.Groups["min"].Value}.{pat}.";
        }
        return null;
    }

    /// <summary>Версия Minecraft, для которой собрана версия NeoForge, или null, если схема не распознана.</summary>
    public static string? GameVersionOf(string neoForgeVersion)
    {
        if (IsLegacy(neoForgeVersion)) return "1.20.1";
        var parts = neoForgeVersion.Split('-', 2)[0].Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var first) || !int.TryParse(parts[1], out var second)) return null;

        if (first < 26)
            return first < 20 ? null : second == 0 ? $"1.{first}" : $"1.{first}.{second}";

        if (parts.Length < 3 || !int.TryParse(parts[2], out var patch)) return null;
        return patch == 0 ? $"{first}.{second}" : $"{first}.{second}.{patch}";
    }

    /// <summary>Версии для игры (по возрастанию).</summary>
    public static IReadOnlyList<string> ForGame(IReadOnlyList<string> all, string gameVersion)
    {
        var prefix = Prefix(gameVersion);
        return prefix is null ? [] : all.Where(v => v.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    /// <summary>Игры, для которых есть сборки NeoForge.</summary>
    public static IReadOnlySet<string> SupportedGames(IReadOnlyList<string> all) =>
        all.Select(GameVersionOf).Where(g => g is not null).Select(g => g!).ToHashSet(StringComparer.Ordinal);

    /// <summary>Явно заданная версия → последняя стабильная → последняя (в том числе beta).</summary>
    /// <exception cref="NeoForgeException">Нет сборок для этой игры или версия записана некорректно.</exception>
    public static string Choose(IReadOnlyList<string> all, string gameVersion, string? requested)
    {
        EnsureSafe(gameVersion, "версия Minecraft");
        if (requested is not null)
        {
            EnsureSafe(requested, "версия NeoForge");
            if (GameVersionOf(requested) is { } game && game != gameVersion)
                throw new NeoForgeException($"NeoForge {requested} рассчитан на Minecraft {game}, а запрошена {gameVersion}.");
            return requested;
        }

        if (Prefix(gameVersion) is null)
            throw new NeoForgeException(
                $"NeoForge для Minecraft {gameVersion} не поддерживается: лаунчер работает с версиями 1.20.1 и новее.");

        var candidates = ForGame(all, gameVersion);
        if (candidates.Count == 0)
            throw new NeoForgeException($"Для Minecraft {gameVersion} нет сборок NeoForge в официальном списке.");
        // «-beta» и подобное в конце — предварительная сборка; у 1.20.1 дефис стоит в самой схеме версии.
        return candidates.LastOrDefault(v => !(IsLegacy(v) ? v[LegacyPrefix.Length..] : v).Contains('-')) ?? candidates[^1];
    }

    public static Uri InstallerUri(string neoForgeVersion)
    {
        EnsureSafe(neoForgeVersion, "версия NeoForge");
        return IsLegacy(neoForgeVersion)
            ? new Uri(LegacyMavenBase, $"{neoForgeVersion}/{InstallerFileName(neoForgeVersion)}")
            : new Uri(MavenBase, $"{neoForgeVersion}/{InstallerFileName(neoForgeVersion)}");
    }

    public static string InstallerFileName(string neoForgeVersion) =>
        IsLegacy(neoForgeVersion) ? $"forge-{neoForgeVersion}-installer.jar" : $"neoforge-{neoForgeVersion}-installer.jar";

    private static void EnsureSafe(string value, string what)
    {
        if (!SafeVersion().IsMatch(value))
            throw new NeoForgeException($"Некорректная {what}: \"{value}\".");
    }
}
