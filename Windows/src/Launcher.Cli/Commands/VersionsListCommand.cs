using Launcher.Core.Versions;

namespace Launcher.Cli.Commands;

/// <summary>Команда "versions list": печатает список версий Minecraft.</summary>
public sealed class VersionsListCommand
{
    private readonly IVersionManifestProvider _provider;
    private readonly TextWriter _out;

    public VersionsListCommand(IVersionManifestProvider provider, TextWriter output)
    {
        _provider = provider;
        _out = output;
    }

    public async Task<int> RunAsync(VersionsListOptions options, CancellationToken ct)
    {
        var result = await _provider.GetManifestAsync(options.Refresh, ct);
        var manifest = result.Manifest;

        _out.WriteLine($"Последний релиз:   {manifest.LatestRelease ?? "—"}");
        _out.WriteLine($"Последний снапшот: {manifest.LatestSnapshot ?? "—"}");
        _out.WriteLine($"Источник данных:   {Describe(result)}");
        _out.WriteLine();

        var all = manifest.OfType(options.Type).ToList();
        var shown = options.Limit == 0 ? all : all.Take(options.Limit).ToList();

        _out.WriteLine($"{"Версия",-22} {"Тип",-10} Дата выхода");
        foreach (var v in shown)
            _out.WriteLine($"{v.Id,-22} {v.RawType,-10} {v.ReleaseTime:yyyy-MM-dd}");

        _out.WriteLine();
        _out.WriteLine($"Показано {shown.Count} из {all.Count}. Используйте --limit 0, чтобы показать все.");
        return 0;
    }

    private static string Describe(ManifestResult r) => r.Source switch
    {
        ManifestSource.Network => "загружено с сервера Mojang",
        ManifestSource.FreshCache => $"локальный кэш от {r.RetrievedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
        ManifestSource.StaleCache =>
            $"УСТАРЕВШИЙ кэш от {r.RetrievedAt.ToLocalTime():yyyy-MM-dd HH:mm} (сервер недоступен)",
        _ => "неизвестно"
    };
}
