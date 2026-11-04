using Launcher.Core.Downloads;
using Launcher.Core.Fabric;
using Launcher.Core.Installation;

namespace Launcher.Cli.Commands;

/// <summary>Команда "fabric": список загрузчиков и установка Fabric.</summary>
public sealed class FabricCommand
{
    private readonly IFabricMetaClient _meta;
    private readonly FabricInstaller _installer;
    private readonly TextWriter _out;

    public FabricCommand(IFabricMetaClient meta, FabricInstaller installer, TextWriter output)
    {
        _meta = meta;
        _installer = installer;
        _out = output;
    }

    public Task<int> RunAsync(FabricCliOptions o, CancellationToken ct) =>
        o.Action == FabricAction.Loaders ? LoadersAsync(o, ct) : InstallAsync(o, ct);

    private async Task<int> LoadersAsync(FabricCliOptions o, CancellationToken ct)
    {
        var loaders = await _meta.GetLoaderVersionsAsync(o.GameVersion, ct);
        if (loaders.Count == 0)
        {
            _out.WriteLine($"Fabric не поддерживает версию Minecraft \"{o.GameVersion}\".");
            return 2;
        }
        _out.WriteLine($"Загрузчики Fabric для Minecraft {o.GameVersion} (новые сверху):");
        foreach (var loader in loaders.Take(15))
            _out.WriteLine($"  {loader.Version}{(loader.Stable ? "" : "  (нестабильная)")}");
        return 0;
    }

    private async Task<int> InstallAsync(FabricCliOptions o, CancellationToken ct)
    {
        var progress = new InlineProgress<InstallProgress>(p =>
        {
            if (p.Stage == InstallStage.Files && p.FilesTotal > 0 && p.FilesDone == p.FilesTotal)
                _out.WriteLine($"  Файлы: {p.FilesDone}/{p.FilesTotal}");
        });

        _out.WriteLine($"Устанавливаю Fabric для Minecraft {o.GameVersion} (сначала проверяется сама игра)...");
        var result = await _installer.InstallAsync(
            o.GameVersion, o.LoaderVersion, new InstallOptions(false, o.Concurrency), progress, ct);

        _out.WriteLine();
        _out.WriteLine($"Готово: {result.VersionId} (загрузчик {result.LoaderVersion}).");
        _out.WriteLine($"Создайте профиль:  profiles create <имя> --version {result.VersionId} --memory 4G");
        return 0;
    }
}
