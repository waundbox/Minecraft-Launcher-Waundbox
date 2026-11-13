using System.Diagnostics;
using Launcher.Core.Downloads;
using Launcher.Core.Installation;

namespace Launcher.Cli.Commands;

/// <summary>Команда "install": устанавливает версию и показывает прогресс.</summary>
public sealed class InstallCommand
{
    private readonly IMinecraftInstaller _installer;
    private readonly TextWriter _out;
    private readonly bool _interactive;

    /// <param name="interactive">true — перерисовывать одну строку прогресса; false (вывод в файл) — печатать только этапы.</param>
    public InstallCommand(IMinecraftInstaller installer, TextWriter output, bool interactive)
    {
        _installer = installer;
        _out = output;
        _interactive = interactive;
    }

    public async Task<int> RunAsync(InstallCliOptions options, CancellationToken ct)
    {
        var lastDraw = Stopwatch.StartNew();
        var lockObject = new object();
        var lineOpen = false;

        var progress = new InlineProgress<InstallProgress>(p =>
        {
            lock (lockObject)
            {
                if (p.Stage != InstallStage.Files)
                {
                    EndLine(ref lineOpen);
                    _out.WriteLine(StageTitle(p.Stage));
                    return;
                }

                if (!_interactive) return;
                var finished = p.FilesTotal > 0 && p.FilesDone == p.FilesTotal;
                if (!finished && lastDraw.ElapsedMilliseconds < 100) return; // не чаще 10 раз в секунду
                lastDraw.Restart();

                var percent = p.BytesTotal > 0 ? Math.Min(100, p.BytesDone * 100 / p.BytesTotal) : 0;
                _out.Write($"\r  Файлы: {p.FilesDone}/{p.FilesTotal}  {FormatMb(p.BytesDone)} / {FormatMb(p.BytesTotal)} ({percent}%)   ");
                lineOpen = true;
            }
        });

        var result = await _installer.InstallAsync(
            options.VersionId, new InstallOptions(options.Refresh, options.Concurrency), progress, ct);

        lock (lockObject) EndLine(ref lineOpen);

        _out.WriteLine();
        _out.WriteLine($"Версия {result.VersionId} установлена.");
        _out.WriteLine($"  Всего файлов:           {result.FilesTotal}");
        _out.WriteLine($"  Скачано:                {result.FilesDownloaded} ({FormatMb(result.BytesDownloaded)})");
        _out.WriteLine($"  Уже были в порядке:     {result.FilesAlreadyValid}");
        _out.WriteLine($"  Клиент:                 {result.ClientJarPath}");
        return 0;
    }

    private void EndLine(ref bool lineOpen)
    {
        if (!lineOpen) return;
        _out.WriteLine();
        lineOpen = false;
    }

    private static string StageTitle(InstallStage stage) => stage switch
    {
        InstallStage.VersionList => "Получаю список версий...",
        InstallStage.VersionMetadata => "Скачиваю описание версии...",
        InstallStage.AssetIndex => "Скачиваю индекс ресурсов...",
        InstallStage.Natives => "Распаковываю нативные библиотеки...",
        InstallStage.Completed => "Готово.",
        _ => stage.ToString()
    };

    private static string FormatMb(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} МБ";
}
