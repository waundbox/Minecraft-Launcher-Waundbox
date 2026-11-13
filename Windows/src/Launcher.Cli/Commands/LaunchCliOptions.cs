namespace Launcher.Cli.Commands;

/// <summary>Параметры команды "launch &lt;профиль&gt; [--dev] [--username ник] [--dry-run] [--quiet]".</summary>
/// <param name="Username">Ник для development-режима; null — стандартный.</param>
/// <param name="Dev">Принудительно development-режим (без официального входа). Включается также параметром --username.</param>
public sealed record LaunchCliOptions(string ProfileName, string? Username, bool Dev, bool DryRun, bool Quiet)
{
    public static LaunchCliOptions? TryParse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        string? profile = null;
        string? username = null;
        bool dev = false, dryRun = false, quiet = false;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--dry-run": dryRun = true; break;
                case "--quiet": quiet = true; break;
                case "--dev": dev = true; break;
                case "--username":
                    if (++i >= args.Count) { error = "После --username нужно указать ник."; return null; }
                    username = args[i];
                    dev = true;
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) { error = $"Неизвестный параметр \"{args[i]}\"."; return null; }
                    if (profile is not null) { error = "Профиль указан дважды. Если в имени есть пробелы, возьмите его в кавычки."; return null; }
                    profile = args[i];
                    break;
            }
        }

        if (profile is null)
        {
            error = "Не указан профиль. Пример: launch MyProfile (список профилей: profiles list)";
            return null;
        }
        return new LaunchCliOptions(profile, username, dev, dryRun, quiet);
    }
}
