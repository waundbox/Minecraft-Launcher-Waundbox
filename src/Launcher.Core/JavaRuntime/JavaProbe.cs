using Launcher.Core.Logging;

namespace Launcher.Core.JavaRuntime;

/// <summary>Проверяет, что файл действительно запускается как Java, и узнаёт её версию.</summary>
public sealed class JavaProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IProcessRunner _runner;
    private readonly ILauncherLogger _logger;

    public JavaProbe(IProcessRunner runner, ILauncherLogger? logger = null)
    {
        _runner = runner;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    /// <returns>Описание Java или null, если файл не запустился или это не Java.</returns>
    public async Task<JavaInstallation?> ProbeAsync(string javaPath, JavaSource source, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            javaPath, ["-XshowSettings:properties", "-version"], Timeout, ct).ConfigureAwait(false);

        if (result.TimedOut || result.ExitCode != 0)
        {
            _logger.Warn($"Файл {javaPath} не удалось запустить как Java (код {result.ExitCode}, таймаут: {result.TimedOut}).");
            return null;
        }

        // Java пишет данные в stderr, но на всякий случай разбираем оба потока.
        var info = JavaProbeOutputParser.Parse(result.StdErr + Environment.NewLine + result.StdOut);
        if (info is null)
        {
            _logger.Warn($"Вывод {javaPath} не похож на вывод Java.");
            return null;
        }

        return new JavaInstallation(javaPath, info.Major, info.Version, info.Vendor, info.Is64Bit, source);
    }
}
