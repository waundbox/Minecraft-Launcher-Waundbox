namespace Launcher.Cli.Commands;

/// <summary>Параметры команды "install &lt;версия&gt; [--refresh] [--concurrency N]".</summary>
public sealed record InstallCliOptions(string VersionId, bool Refresh, int Concurrency)
{
    public const int DefaultConcurrency = 8;
    public const int MaxConcurrency = 32;

    public static InstallCliOptions? TryParse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        string? version = null;
        var refresh = false;
        var concurrency = DefaultConcurrency;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--refresh":
                    refresh = true;
                    break;
                case "--concurrency":
                    if (++i >= args.Count || !int.TryParse(args[i], out concurrency) || concurrency < 1 || concurrency > MaxConcurrency)
                    {
                        error = $"После --concurrency нужно целое число от 1 до {MaxConcurrency}.";
                        return null;
                    }
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"Неизвестный параметр \"{args[i]}\".";
                        return null;
                    }
                    if (version is not null)
                    {
                        error = "Версия указана дважды. Пример: install 1.21.1";
                        return null;
                    }
                    version = args[i];
                    break;
            }
        }

        if (version is null)
        {
            error = "Не указана версия. Пример: install 1.21.1";
            return null;
        }
        return new InstallCliOptions(version, refresh, concurrency);
    }
}
