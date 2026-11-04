using Launcher.Core.Forge;
using Launcher.Core.Installation;

namespace Launcher.Cli.Commands;

/// <summary>Команда "forge": список сборок и установка Forge.</summary>
public sealed class ForgeCommand
{
    private readonly IForgeMetaClient _meta;
    private readonly ForgeInstaller _installer;
    private readonly TextWriter _out;

    public ForgeCommand(IForgeMetaClient meta, ForgeInstaller installer, TextWriter output)
    {
        _meta = meta;
        _installer = installer;
        _out = output;
    }

    public Task<int> RunAsync(ForgeCliOptions o, CancellationToken ct) =>
        o.Action == ForgeAction.Versions ? VersionsAsync(o, ct) : InstallAsync(o, ct);

    private async Task<int> VersionsAsync(ForgeCliOptions o, CancellationToken ct)
    {
        var promos = await _meta.GetPromotionsAsync(ct);
        var found = false;
        foreach (var kind in new[] { "recommended", "latest" })
            if (promos.TryGetValue($"{o.GameVersion}-{kind}", out var version))
            {
                _out.WriteLine($"  {(kind == "recommended" ? "рекомендованная" : "последняя")}: {version}");
                found = true;
            }

        if (!found)
        {
            _out.WriteLine($"Для Minecraft {o.GameVersion} нет сборок Forge в официальном списке.");
            return 2;
        }
        return 0;
    }

    private async Task<int> InstallAsync(ForgeCliOptions o, CancellationToken ct)
    {
        var result = await _installer.InstallAsync(o.GameVersion, o.ForgeVersion, new InstallOptions(), new InlineStatus(_out), ct);

        _out.WriteLine();
        _out.WriteLine($"Готово: {result.VersionId} (Forge {result.ForgeVersion}).");
        _out.WriteLine($"Создайте профиль:  profiles create <имя> --version {result.VersionId} --memory 4G");
        return 0;
    }

    /// <summary>Печатает сообщения сразу (Progress&lt;T&gt; доставляет их асинхронно и может отстать от вывода).</summary>
    private sealed class InlineStatus(TextWriter output) : IProgress<string>
    {
        public void Report(string value) => output.WriteLine(value);
    }
}
