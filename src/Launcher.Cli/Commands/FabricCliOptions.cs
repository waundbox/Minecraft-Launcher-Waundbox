namespace Launcher.Cli.Commands;

public enum FabricAction { Loaders, Install }

/// <summary>Параметры команды "fabric loaders &lt;версия&gt;" и "fabric install &lt;версия&gt; [--loader X] [--concurrency N]".</summary>
public sealed record FabricCliOptions(FabricAction Action, string GameVersion, string? LoaderVersion, int Concurrency)
{
    public static FabricCliOptions? TryParse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        if (args.Count == 0 || args[0] is not ("loaders" or "install"))
        {
            error = "Ожидается: fabric loaders <версия> или fabric install <версия> [--loader X].";
            return null;
        }

        var action = args[0] == "loaders" ? FabricAction.Loaders : FabricAction.Install;
        string? game = null, loader = null;
        var concurrency = InstallCliOptions.DefaultConcurrency;

        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--loader" when action == FabricAction.Install:
                    if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        error = "После --loader нужно указать версию загрузчика.";
                        return null;
                    }
                    loader = args[i];
                    break;
                case "--concurrency" when action == FabricAction.Install:
                    if (++i >= args.Count || !int.TryParse(args[i], out concurrency) ||
                        concurrency < 1 || concurrency > InstallCliOptions.MaxConcurrency)
                    {
                        error = $"После --concurrency нужно целое число от 1 до {InstallCliOptions.MaxConcurrency}.";
                        return null;
                    }
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"Неизвестный параметр \"{args[i]}\".";
                        return null;
                    }
                    if (game is not null)
                    {
                        error = "Версия Minecraft указана дважды.";
                        return null;
                    }
                    game = args[i];
                    break;
            }
        }

        if (game is null)
        {
            error = "Не указана версия Minecraft. Пример: fabric install 1.21.1";
            return null;
        }
        return new FabricCliOptions(action, game, loader, concurrency);
    }
}
