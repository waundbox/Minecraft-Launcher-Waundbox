namespace Launcher.Core.Logging;

/// <summary>Простой логгер ядра. Секреты (токены, пароли) в логи передавать нельзя.</summary>
public interface ILauncherLogger
{
    void Info(string message);
    void Warn(string message, Exception? exception = null);
    void Error(string message, Exception? exception = null);
}

/// <summary>Логгер, который ничего не делает (для тестов и значений по умолчанию).</summary>
public sealed class NullLauncherLogger : ILauncherLogger
{
    public static readonly NullLauncherLogger Instance = new();
    private NullLauncherLogger() { }
    public void Info(string message) { }
    public void Warn(string message, Exception? exception = null) { }
    public void Error(string message, Exception? exception = null) { }
}
