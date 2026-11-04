using Launcher.Core.Errors;
using Launcher.Core.Logging;

namespace Launcher.Core.JavaRuntime;

/// <summary>Подбирает Java для запуска: сначала путь, заданный пользователем, затем автоматический поиск.</summary>
public sealed class JavaResolver
{
    private readonly IJavaDetector _detector;
    private readonly JavaProbe _probe;
    private readonly ILauncherLogger _logger;

    public JavaResolver(IJavaDetector detector, JavaProbe probe, ILauncherLogger? logger = null)
    {
        _detector = detector;
        _probe = probe;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    /// <param name="requiredMajor">Минимально необходимая основная версия Java.</param>
    /// <param name="configuredPath">Путь, настроенный пользователем, или null.</param>
    /// <exception cref="JavaNotFoundException">Подходящей Java нет — сообщение содержит инструкцию, что делать.</exception>
    public async Task<JavaResolution> ResolveAsync(int requiredMajor, string? configuredPath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = await _probe.ProbeAsync(configuredPath, JavaSource.Configured, ct).ConfigureAwait(false)
                ?? throw new JavaNotFoundException(
                    $"Указанная вами Java не запускается: {configuredPath}. " +
                    "Укажите другой путь командой \"java set <путь>\" или вернитесь к автоматическому поиску командой \"java clear\".");

            if (configured.MajorVersion < requiredMajor)
                throw new JavaNotFoundException(
                    $"Указанная вами Java {configured.MajorVersion} слишком старая: для этой версии Minecraft нужна Java {requiredMajor} или новее. " +
                    "Укажите другую командой \"java set <путь>\" или вернитесь к автоматическому поиску командой \"java clear\".");

            return new JavaResolution(configured, JavaSelector.NewerWarning(requiredMajor, configured.MajorVersion));
        }

        var found = await _detector.DetectAsync(ct).ConfigureAwait(false);
        var selected = JavaSelector.Select(found, requiredMajor);
        if (selected is not null)
        {
            _logger.Info($"Выбрана Java {selected.Installation.MajorVersion}: {selected.Installation.Path}");
            return selected;
        }

        throw new JavaNotFoundException(BuildNotFoundMessage(requiredMajor, found));
    }

    private static string BuildNotFoundMessage(int requiredMajor, IReadOnlyList<JavaInstallation> found)
    {
        var what = found.Count == 0
            ? "Java на этом компьютере не найдена."
            : $"Найдены только слишком старые версии: {string.Join(", ", found.Select(f => "Java " + f.MajorVersion).Distinct())}.";

        return $"Для этой версии Minecraft нужна Java {requiredMajor} или новее. {what} " +
               $"Установите Java {requiredMajor} (например, Eclipse Temurin: https://adoptium.net/) " +
               "или, если она уже установлена, укажите путь командой \"java set <путь>\".";
    }
}
