using Launcher.Core.Configuration;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.JavaRuntime;

namespace Launcher.Cli.Commands;

/// <summary>Команда "java": поиск, выбор и настройка Java.</summary>
public sealed class JavaCommand
{
    private readonly ISettingsStore _settings;
    private readonly IJavaDetector _detector;
    private readonly JavaProbe _probe;
    private readonly JavaResolver _resolver;
    private readonly InstalledVersionRepository _installed;
    private readonly TextWriter _out;

    public JavaCommand(
        ISettingsStore settings, IJavaDetector detector, JavaProbe probe, JavaResolver resolver,
        InstalledVersionRepository installed, TextWriter output)
    {
        _settings = settings;
        _detector = detector;
        _probe = probe;
        _resolver = resolver;
        _installed = installed;
        _out = output;
    }

    public Task<int> RunAsync(JavaCliOptions options, CancellationToken ct) => options.Action switch
    {
        JavaAction.Detect => DetectAsync(options.ForVersion, ct),
        JavaAction.Set => SetAsync(options.Path!, ct),
        JavaAction.Clear => ClearAsync(ct),
        _ => throw new ArgumentOutOfRangeException(nameof(options))
    };

    private async Task<int> DetectAsync(string? forVersion, CancellationToken ct)
    {
        var settings = await _settings.LoadAsync(ct);
        _out.WriteLine(settings.JavaPath is null
            ? "Путь к Java не задан вручную (автоматический поиск)."
            : $"Задан вручную: {settings.JavaPath}");
        _out.WriteLine();

        _out.WriteLine("Ищу Java на этом компьютере...");
        var found = await _detector.DetectAsync(ct);

        if (found.Count == 0)
        {
            _out.WriteLine("Java не найдена.");
        }
        else
        {
            _out.WriteLine($"{"Java",-6} {"Версия",-14} {"Бит",-4} {"Источник",-22} Путь");
            foreach (var j in found)
                _out.WriteLine($"{j.MajorVersion,-6} {j.Version,-14} {(j.Is64Bit ? "64" : "32"),-4} {SourceName(j.Source),-22} {j.Path}");
        }

        if (forVersion is null) return 0;

        var details = await _installed.LoadAsync(forVersion, ct);
        var required = JavaRequirement.For(details);
        _out.WriteLine();
        _out.WriteLine($"Для Minecraft {details.Id} нужна Java {required} или новее.");

        var resolution = await _resolver.ResolveAsync(required, settings.JavaPath, ct);
        _out.WriteLine($"Будет использована: Java {resolution.Installation.MajorVersion} ({resolution.Installation.Path})");
        if (resolution.Warning is not null) _out.WriteLine($"Внимание: {resolution.Warning}");
        return 0;
    }

    private async Task<int> SetAsync(string path, CancellationToken ct)
    {
        var executable = JavaExecutable.ResolvePath(path);
        if (!File.Exists(executable))
            throw new JavaNotFoundException($"Файл не найден: {executable}. Укажите путь к java.exe или к папке установки Java.");

        var java = await _probe.ProbeAsync(executable, JavaSource.Configured, ct)
            ?? throw new JavaNotFoundException($"Файл {executable} не запускается как Java. Проверьте путь.");

        var settings = await _settings.LoadAsync(ct);
        await _settings.SaveAsync(settings with { JavaPath = java.Path }, ct);

        _out.WriteLine($"Сохранено: Java {java.MajorVersion} ({java.Version}) — {java.Path}");
        return 0;
    }

    private async Task<int> ClearAsync(CancellationToken ct)
    {
        var settings = await _settings.LoadAsync(ct);
        await _settings.SaveAsync(settings with { JavaPath = null }, ct);
        _out.WriteLine("Ручной путь к Java сброшен, будет использоваться автоматический поиск.");
        return 0;
    }

    private static string SourceName(JavaSource source) => source switch
    {
        JavaSource.Configured => "указана вручную",
        JavaSource.JavaHome => "JAVA_HOME",
        JavaSource.Path => "PATH",
        JavaSource.Installed => "установленная",
        JavaSource.MojangRuntime => "среда Minecraft",
        _ => source.ToString()
    };
}
