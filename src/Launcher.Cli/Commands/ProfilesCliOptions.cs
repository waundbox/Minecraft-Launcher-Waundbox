namespace Launcher.Cli.Commands;

public enum ProfilesAction { List, Show, Create, Edit, Delete }

/// <summary>
/// Параметры команды "profiles": list | show &lt;имя&gt; | delete &lt;имя&gt; |
/// create &lt;имя&gt; --version &lt;версия&gt; [параметры] | edit &lt;имя&gt; [параметры].
/// Параметры: --version, --memory 4G, --min-memory 1G, --jvm-args "...", --game-args "...", --java &lt;путь&gt;.
/// Пустое значение ("") сбрасывает необязательный параметр; --min-memory 0 тоже сбрасывает.
/// </summary>
public sealed record ProfilesCliOptions(
    ProfilesAction Action,
    string? Name = null,
    string? VersionId = null,
    int? MaxMemoryMb = null,
    int? MinMemoryMb = null,
    bool MinMemorySet = false,
    string? JvmArguments = null,
    bool JvmArgumentsSet = false,
    string? GameArguments = null,
    bool GameArgumentsSet = false,
    string? JavaPath = null,
    bool JavaPathSet = false)
{
    public static ProfilesCliOptions? TryParse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        if (args.Count == 0)
        {
            error = "Укажите действие: list, show, create, edit или delete.";
            return null;
        }

        ProfilesAction action;
        switch (args[0])
        {
            case "list": action = ProfilesAction.List; break;
            case "show": action = ProfilesAction.Show; break;
            case "create": action = ProfilesAction.Create; break;
            case "edit": action = ProfilesAction.Edit; break;
            case "delete": action = ProfilesAction.Delete; break;
            default:
                error = $"Неизвестное действие \"{args[0]}\". Допустимо: list, show, create, edit, delete.";
                return null;
        }

        if (action == ProfilesAction.List)
        {
            if (args.Count != 1) { error = "Команда \"profiles list\" не принимает параметров."; return null; }
            return new ProfilesCliOptions(action);
        }

        if (args.Count < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            error = "Укажите имя профиля. Пример: profiles create MyProfile --version 1.21.1";
            return null;
        }

        var result = new ProfilesCliOptions(action, Name: args[1]);

        for (var i = 2; i < args.Count; i++)
        {
            var option = args[i];
            if (action is ProfilesAction.Show or ProfilesAction.Delete)
            {
                error = $"Команда не принимает параметров, получено \"{option}\".";
                return null;
            }
            if (i + 1 >= args.Count)
            {
                error = $"После {option} нужно указать значение.";
                return null;
            }
            var value = args[++i];

            switch (option)
            {
                case "--version":
                    if (string.IsNullOrWhiteSpace(value)) { error = "Версия не может быть пустой."; return null; }
                    result = result with { VersionId = value };
                    break;
                case "--memory":
                    if (!MemorySize.TryParseMegabytes(value, out var max) || max == 0)
                    {
                        error = "Некорректный объём памяти. Примеры: 4096, 4096M, 4G.";
                        return null;
                    }
                    result = result with { MaxMemoryMb = max };
                    break;
                case "--min-memory":
                    if (!MemorySize.TryParseMegabytes(value, out var min))
                    {
                        error = "Некорректный объём памяти. Примеры: 1024, 1G; 0 — сбросить.";
                        return null;
                    }
                    result = result with { MinMemoryMb = min == 0 ? null : min, MinMemorySet = true };
                    break;
                case "--jvm-args":
                    result = result with { JvmArguments = string.IsNullOrWhiteSpace(value) ? null : value, JvmArgumentsSet = true };
                    break;
                case "--game-args":
                    result = result with { GameArguments = string.IsNullOrWhiteSpace(value) ? null : value, GameArgumentsSet = true };
                    break;
                case "--java":
                    result = result with { JavaPath = string.IsNullOrWhiteSpace(value) ? null : value, JavaPathSet = true };
                    break;
                default:
                    error = $"Неизвестный параметр \"{option}\".";
                    return null;
            }
        }

        if (action == ProfilesAction.Create && result.VersionId is null)
        {
            error = "Для создания профиля укажите версию: --version 1.21.1";
            return null;
        }
        return result;
    }
}
