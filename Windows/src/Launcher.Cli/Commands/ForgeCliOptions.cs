namespace Launcher.Cli.Commands;

public enum ForgeAction { Versions, Install }

/// <summary>Параметры "forge versions &lt;версия&gt;" и "forge install &lt;версия&gt; [--forge X]".</summary>
public sealed record ForgeCliOptions(ForgeAction Action, string GameVersion, string? ForgeVersion)
{
    public static ForgeCliOptions? TryParse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        if (args.Count == 0 || args[0] is not ("versions" or "install"))
        {
            error = "Ожидается: forge versions <версия> или forge install <версия> [--forge X].";
            return null;
        }

        var action = args[0] == "versions" ? ForgeAction.Versions : ForgeAction.Install;
        string? game = null, forge = null;

        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--forge" && action == ForgeAction.Install)
            {
                if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    error = "После --forge нужно указать версию Forge.";
                    return null;
                }
                forge = args[i];
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Неизвестный параметр \"{args[i]}\".";
                return null;
            }
            else if (game is not null)
            {
                error = "Версия Minecraft указана дважды.";
                return null;
            }
            else game = args[i];
        }

        if (game is null)
        {
            error = "Не указана версия Minecraft. Пример: forge install 1.21.1";
            return null;
        }
        return new ForgeCliOptions(action, game, forge);
    }
}
