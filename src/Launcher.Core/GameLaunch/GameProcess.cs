namespace Launcher.Core.GameLaunch;

/// <summary>Готовый к запуску план: что именно будет выполнено.</summary>
/// <param name="Arguments">Все аргументы Java (JVM, главный класс, аргументы игры).</param>
/// <param name="SecretValues">Значения, которые нельзя показывать в логах и на экране (токены).</param>
public sealed record GameLaunchPlan(
    string VersionId,
    string ProfileName,
    string JavaPath,
    int JavaMajorVersion,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> SecretValues)
{
    /// <summary>Командная строка для показа пользователю и записи в журнал: секреты заменены на ***.</summary>
    public string ToDisplayCommandLine()
    {
        string Mask(string argument)
        {
            foreach (var secret in SecretValues)
            {
                if (secret.Length == 0) continue;
                if (secret.Length >= 8) argument = argument.Replace(secret, "***", StringComparison.Ordinal);
                else if (argument == secret) argument = "***";
            }
            return argument;
        }

        string Quote(string argument) =>
            argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;

        return string.Join(' ', new[] { JavaPath }.Concat(Arguments).Select(a => Quote(Mask(a))));
    }
}

/// <summary>Строка вывода игры.</summary>
public sealed record GameOutputLine(bool IsError, string Text);

/// <summary>Как завершилась игра.</summary>
/// <param name="Killed">true — процесс был остановлен лаунчером.</param>
public sealed record GameExitResult(int ExitCode, bool Killed)
{
    public bool IsSuccess => ExitCode == 0 && !Killed;
}

/// <summary>Запущенный процесс игры.</summary>
public interface IGameProcess
{
    int ProcessId { get; }
    bool HasExited { get; }

    /// <summary>Завершается, когда игра закрылась (и весь её вывод получен).</summary>
    Task<GameExitResult> Completion { get; }

    /// <summary>Останавливает игру вместе с дочерними процессами. Безопасно вызывать, если игра уже закрыта.</summary>
    void Kill();
}

/// <summary>Запускает Java-процесс по плану (реализация — в Infrastructure).</summary>
public interface IGameProcessStarter
{
    /// <exception cref="Errors.LaunchException">Процесс не удалось запустить.</exception>
    IGameProcess Start(GameLaunchPlan plan, Action<GameOutputLine>? onOutput);
}
