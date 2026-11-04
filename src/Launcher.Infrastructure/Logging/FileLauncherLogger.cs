using Launcher.Core.Logging;

namespace Launcher.Infrastructure.Logging;

/// <summary>Пишет события в текстовый файл. Ошибки записи лога никогда не ломают работу лаунчера.</summary>
public sealed class FileLauncherLogger : ILauncherLogger
{
    private readonly string _path;
    private readonly object _lock = new();

    public FileLauncherLogger(string path) => _path = path;

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (exception is not null) line += $" | {exception.GetType().Name}: {exception.Message}";
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Лог недоступен — это не повод падать.
        }
    }
}
