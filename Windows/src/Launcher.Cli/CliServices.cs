using Launcher.Core.Authentication;
using Launcher.Core.Configuration;
using Launcher.Core.Fabric;
using Launcher.Core.Forge;
using Launcher.Core.GameLaunch;
using Launcher.Core.Installation;
using Launcher.Core.JavaRuntime;
using Launcher.Core.Libraries;
using Launcher.Core.Logging;
using Launcher.Core.Profiles;
using Launcher.Core.Versions;
using Launcher.Infrastructure.Authentication;
using Launcher.Infrastructure.Configuration;
using Launcher.Infrastructure.Downloads;
using Launcher.Infrastructure.Fabric;
using Launcher.Infrastructure.Forge;
using Launcher.Infrastructure.GameLaunch;
using Launcher.Infrastructure.JavaRuntime;
using Launcher.Infrastructure.Logging;
using Launcher.Infrastructure.Profiles;
using Launcher.Infrastructure.Versions;

namespace Launcher.Cli;

/// <summary>Собирает все сервисы лаунчера для консольного интерфейса (простая замена DI-контейнеру).</summary>
internal sealed class CliServices : IDisposable
{
    private readonly HttpClient _metadataHttp;
    private readonly HttpClient _downloadHttp;

    public ILauncherLogger Logger { get; }
    public IVersionManifestProvider Manifest { get; }
    public IMinecraftInstaller Installer { get; }
    public ISettingsStore Settings { get; }
    public IJavaDetector JavaDetector { get; }
    public JavaProbe JavaProbe { get; }
    public JavaResolver JavaResolver { get; }
    public InstalledVersionRepository InstalledVersions { get; }
    public MinecraftLayout Layout { get; }
    public IProfileStore Profiles { get; }
    public GameLaunchService LaunchService { get; }
    public Func<string, string> GameLogPath { get; }
    public IFabricMetaClient FabricMeta { get; }
    public FabricInstaller FabricInstaller { get; }
    public IForgeMetaClient ForgeMeta { get; }
    public ForgeInstaller ForgeInstaller { get; }
    public AccountService Accounts { get; }

    private CliServices(HttpClient metadataHttp, HttpClient downloadHttp, ILauncherLogger logger,
        IVersionManifestProvider manifest, IMinecraftInstaller installer, ISettingsStore settings,
        IJavaDetector javaDetector, JavaProbe javaProbe, JavaResolver javaResolver, InstalledVersionRepository installedVersions,
        MinecraftLayout layout, IProfileStore profiles, GameLaunchService launchService, Func<string, string> gameLogPath,
        IFabricMetaClient fabricMeta, FabricInstaller fabricInstaller,
        IForgeMetaClient forgeMeta, ForgeInstaller forgeInstaller, AccountService accounts)
    {
        _metadataHttp = metadataHttp;
        _downloadHttp = downloadHttp;
        Logger = logger;
        Manifest = manifest;
        Installer = installer;
        Settings = settings;
        JavaDetector = javaDetector;
        JavaProbe = javaProbe;
        JavaResolver = javaResolver;
        InstalledVersions = installedVersions;
        Layout = layout;
        Profiles = profiles;
        LaunchService = launchService;
        GameLogPath = gameLogPath;
        FabricMeta = fabricMeta;
        FabricInstaller = fabricInstaller;
        ForgeMeta = forgeMeta;
        ForgeInstaller = forgeInstaller;
        Accounts = accounts;
    }

    public static CliServices Create()
    {
        var paths = LauncherPaths.CreateDefault();
        var logger = new FileLauncherLogger(paths.LogFile);

        // Для небольших JSON — обычный таймаут; для больших файлов таймаута нет, за зависание отвечает загрузчик.
        var metadataHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var downloadHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var client in new[] { metadataHttp, downloadHttp })
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Waundbox-Launcher/0.7");

        var manifest = new VersionManifestProvider(metadataHttp, paths.ManifestCacheFile, TimeSpan.FromHours(1), logger);
        var downloader = new HttpFileDownloader(downloadHttp, logger);
        var layout = new MinecraftLayout(paths.GameRoot);
        var installer = new MinecraftInstaller(manifest, downloader, layout, PlatformInfo.Current(), logger);

        var settings = new JsonSettingsStore(paths.SettingsFile, logger);
        var javaProbe = new JavaProbe(new SystemProcessRunner(), logger);
        var javaDetector = new JavaDetector(JavaSearchLocations.ForCurrentSystem, javaProbe, logger);
        var javaResolver = new JavaResolver(javaDetector, javaProbe, logger);

        var installedVersions = new InstalledVersionRepository(layout);
        var launchService = new GameLaunchService(
            installedVersions, layout, javaResolver, settings, new SystemGameProcessStarter(), PlatformInfo.Current(), logger);

        var fabricMeta = new HttpFabricMetaClient(metadataHttp);
        var fabricInstaller = new FabricInstaller(fabricMeta, installer, downloader, layout, PlatformInfo.Current(), logger);

        var forgeMeta = new HttpForgeMetaClient(metadataHttp);
        var forgeInstaller = new ForgeInstaller(
            forgeMeta, installer, downloader, new SystemProcessRunner(), javaResolver, settings, installedVersions,
            layout, paths.InstallersDirectory, logger);

        var accounts = new AccountService(
            new MicrosoftOAuthClient(metadataHttp), new XboxClient(metadataHttp), new MinecraftServicesClient(metadataHttp),
            new DpapiAccountStore(paths.AccountFile, logger), settings, logger);

        return new CliServices(metadataHttp, downloadHttp, logger, manifest, installer, settings,
            javaDetector, javaProbe, javaResolver, installedVersions,
            layout, new JsonProfileStore(paths.ProfilesFile), launchService, paths.GameLogFile,
            fabricMeta, fabricInstaller, forgeMeta, forgeInstaller, accounts);
    }

    public void Dispose()
    {
        _metadataHttp.Dispose();
        _downloadHttp.Dispose();
    }
}
