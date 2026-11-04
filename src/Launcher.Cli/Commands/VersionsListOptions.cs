using Launcher.Core.Versions;

namespace Launcher.Cli.Commands;

/// <summary>Параметры команды "versions list".</summary>
public sealed record VersionsListOptions(VersionType? Type, int Limit, bool Refresh)
{
    public const int DefaultLimit = 20;

    /// <summary>
    /// Разбирает аргументы после "versions list": [--type release|snapshot|beta|alpha|all] [--limit N] [--refresh].
    /// </summary>
    /// <returns>Параметры или null и текст ошибки.</returns>
    public static VersionsListOptions? TryParse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        VersionType? type = VersionType.Release;
        var limit = DefaultLimit;
        var refresh = false;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--refresh":
                    refresh = true;
                    break;
                case "--type":
                    if (++i >= args.Count) { error = "После --type нужно указать значение."; return null; }
                    switch (args[i].ToLowerInvariant())
                    {
                        case "release": type = VersionType.Release; break;
                        case "snapshot": type = VersionType.Snapshot; break;
                        case "beta": type = VersionType.OldBeta; break;
                        case "alpha": type = VersionType.OldAlpha; break;
                        case "all": type = null; break;
                        default:
                            error = $"Неизвестный тип \"{args[i]}\". Допустимо: release, snapshot, beta, alpha, all.";
                            return null;
                    }
                    break;
                case "--limit":
                    if (++i >= args.Count || !int.TryParse(args[i], out limit) || limit < 0)
                    {
                        error = "После --limit нужно целое число 0 или больше (0 — показать все).";
                        return null;
                    }
                    break;
                default:
                    error = $"Неизвестный параметр \"{args[i]}\".";
                    return null;
            }
        }

        return new VersionsListOptions(type, limit, refresh);
    }
}
