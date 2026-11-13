using System.Text.RegularExpressions;

namespace Launcher.Cli.Commands;

public enum AuthAction { Setup, Login, Status, Logout }

/// <summary>Параметры команды "auth setup &lt;client-id&gt; | login | status | logout".</summary>
public sealed partial record AuthCliOptions(AuthAction Action, string? ClientId)
{
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();

    public static AuthCliOptions? TryParse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        if (args.Count == 0)
        {
            error = "Ожидается: auth setup <client-id> | auth login | auth status | auth logout.";
            return null;
        }

        switch (args[0])
        {
            case "login" or "status" or "logout" when args.Count == 1:
                return new AuthCliOptions(args[0] switch { "login" => AuthAction.Login, "status" => AuthAction.Status, _ => AuthAction.Logout }, null);
            case "setup":
                if (args.Count != 2)
                {
                    error = "Укажите client id приложения Azure. Пример: auth setup 00000000-0000-0000-0000-000000000000";
                    return null;
                }
                if (!GuidPattern().IsMatch(args[1]))
                {
                    error = "Client id — это идентификатор вида xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx (Application (client) ID в Azure).";
                    return null;
                }
                return new AuthCliOptions(AuthAction.Setup, args[1].ToLowerInvariant());
            default:
                error = "Неизвестная подкоманда auth. Доступны: setup, login, status, logout.";
                return null;
        }
    }
}
