namespace Launcher.Cli.Commands;

public enum JavaAction { Detect, Set, Clear }

/// <summary>Параметры команды "java": detect [--for версия] | set &lt;путь&gt; | clear.</summary>
public sealed record JavaCliOptions(JavaAction Action, string? Path = null, string? ForVersion = null)
{
    public static JavaCliOptions? TryParse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        if (args.Count == 0)
        {
            error = "Укажите действие: detect, set или clear.";
            return null;
        }

        switch (args[0])
        {
            case "detect":
                string? forVersion = null;
                for (var i = 1; i < args.Count; i++)
                {
                    if (args[i] == "--for" && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        forVersion = args[++i];
                    }
                    else
                    {
                        error = args[i] == "--for" ? "После --for нужно указать версию, например: java detect --for 1.21.1" : $"Неизвестный параметр \"{args[i]}\".";
                        return null;
                    }
                }
                return new JavaCliOptions(JavaAction.Detect, ForVersion: forVersion);

            case "set":
                if (args.Count != 2)
                {
                    error = "Укажите путь к java.exe или к папке Java. Пример: java set \"C:\\Program Files\\Java\\jdk-21\"";
                    return null;
                }
                return new JavaCliOptions(JavaAction.Set, Path: args[1]);

            case "clear":
                if (args.Count != 1)
                {
                    error = "Команда \"java clear\" не принимает параметров.";
                    return null;
                }
                return new JavaCliOptions(JavaAction.Clear);

            default:
                error = $"Неизвестное действие \"{args[0]}\". Допустимо: detect, set, clear.";
                return null;
        }
    }
}
