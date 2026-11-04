namespace Launcher.Core.JavaRuntime;

/// <summary>Результат запуска внешней программы.</summary>
/// <param name="ExitCode">Код завершения; -1, если программа не запустилась или была прервана.</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>Запуск внешней программы с ограничением по времени (интерфейс нужен, чтобы подменять его в тестах).</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}
