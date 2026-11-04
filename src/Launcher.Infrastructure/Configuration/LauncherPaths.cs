namespace Launcher.Infrastructure.Configuration;

/// <summary>Расположение данных лаунчера на диске.</summary>
public sealed class LauncherPaths
{
    /// <summary>Переменная окружения, позволяющая сменить корневую папку данных.</summary>
    public const string HomeEnvironmentVariable = "WAUNDBOX_HOME";

    public string Root { get; }
    /// <summary>Папка игры (versions, libraries, assets).</summary>
    public string GameRoot => Path.Combine(Root, "minecraft");
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string InstallersDirectory => Path.Combine(CacheDirectory, "installers");
    public string ManifestCacheFile => Path.Combine(CacheDirectory, "version_manifest_v2.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string SkinFile => Path.Combine(Root, "skin.png");
    /// <summary>Коллекция сохранённых скинов.</summary>
    public string SkinLibraryDirectory => Path.Combine(Root, "skins");
    public string AccountFile => Path.Combine(Root, "account.dat");
    public string ElyAccountFile => Path.Combine(Root, "ely-account.dat");
    /// <summary>Папка с authlib-injector (нужен для входа через Ely.by).</summary>
    public string AuthlibDirectory => Path.Combine(Root, "runtime", "authlib-injector");
    public string ProfilesFile => Path.Combine(Root, "profiles.json");
    public string GameLogFile(string profileName) => Path.Combine(Root, "logs", $"game-{profileName}.log");
    public string LogFile => Path.Combine(Root, "logs", "launcher.log");

    public LauncherPaths(string root) => Root = root;

    /// <summary>По умолчанию: %APPDATA%\.waundbox (или значение WAUNDBOX_HOME).</summary>
    public static LauncherPaths CreateDefault()
    {
        var overridden = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return new LauncherPaths(overridden);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new LauncherPaths(Path.Combine(appData, ".waundbox"));
    }
}
