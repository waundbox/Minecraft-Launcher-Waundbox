using Launcher.Core.Authentication;
using Launcher.Core.Configuration;
using Launcher.Core.Fabric;
using Launcher.Core.Custom;
using Launcher.Core.Quilt;
using Launcher.Core.Forge;
using Launcher.Core.GameLaunch;
using Launcher.Core.Installation;
using Launcher.Core.JavaRuntime;
using Launcher.Core.Libraries;
using Launcher.Core.Logging;
using Launcher.Core.Modpacks;
using Launcher.Core.NeoForge;
using Launcher.Core.Mods;
using Launcher.Core.Profiles;
using Launcher.Core.Skins;
using Launcher.Core.Versions;
using Launcher.Infrastructure.Authentication;
using Launcher.Infrastructure.Configuration;
using Launcher.Infrastructure.Downloads;
using Launcher.Infrastructure.Fabric;
using Launcher.Infrastructure.Quilt;
using Launcher.Infrastructure.Forge;
using Launcher.Infrastructure.GameLaunch;
using Launcher.Infrastructure.JavaRuntime;
using Launcher.Infrastructure.Logging;
using Launcher.Infrastructure.Mods;
using Launcher.Infrastructure.NeoForge;
using Launcher.Infrastructure.Profiles;
using Launcher.Infrastructure.Versions;

namespace Launcher.UI.Services;

/// <summary>
/// Сборка сервисов ядра для графического интерфейса. Повторяет сборку консольной версии (CliServices);
/// общий код можно будет вынести в Infrastructure, когда стабилизируются оба интерфейса.
/// </summary>
internal sealed class AppServices : IDisposable
{
    private readonly HttpClient _metadataHttp;
    private readonly HttpClient _downloadHttp;

    public LauncherPaths Paths { get; }
    public ILauncherLogger Logger { get; }
    public IVersionManifestProvider Manifest { get; }
    public IMinecraftInstaller Installer { get; }
    public ISettingsStore Settings { get; }
    public IJavaDetector JavaDetector { get; }
    public MinecraftLayout Layout { get; }
    public IProfileStore Profiles { get; }
    public GameLaunchService Launch { get; }
    public IFabricMetaClient FabricMeta { get; }
    public FabricInstaller Fabric { get; }
    public IQuiltMetaClient QuiltMeta { get; }
    public QuiltInstaller Quilt { get; }
    public IForgeMetaClient ForgeMeta { get; }
    public ForgeInstaller Forge { get; }
    public NeoForgeInstaller NeoForge { get; }
    public INeoForgeMetaClient NeoForgeMeta { get; }
    public AccountService Accounts { get; }
    public ElyAccountService Ely { get; }

    /// <summary>Скачивает небольшой файл (например, скин) обычным запросом.</summary>
    public Task<byte[]> GetBytesAsync(string url, CancellationToken ct) => _metadataHttp.GetByteArrayAsync(url, ct);
    public SkinService Skins { get; }
    public SkinLibrary SkinLibrary { get; }
    public IModrinthClient Modrinth { get; }
    public ModManager Mods { get; }
    public MrpackImporter Mrpack { get; }
    public ICurseForgeClient CurseForge { get; }
    public CustomJarInstaller CustomJar { get; }

    private AppServices(
        HttpClient metadataHttp, HttpClient downloadHttp, LauncherPaths paths, ILauncherLogger logger,
        IVersionManifestProvider manifest, IMinecraftInstaller installer, ISettingsStore settings,
        IJavaDetector javaDetector, MinecraftLayout layout, IProfileStore profiles, GameLaunchService launch,
        IFabricMetaClient fabricMeta, FabricInstaller fabric, IQuiltMetaClient quiltMeta, QuiltInstaller quilt, IForgeMetaClient forgeMeta, ForgeInstaller forge, INeoForgeMetaClient neoForgeMeta, NeoForgeInstaller neoForge,
        AccountService accounts, ElyAccountService ely, SkinService skins, IModrinthClient modrinth, ModManager mods, MrpackImporter mrpack,
        ICurseForgeClient curseForge, SkinLibrary skinLibrary, CustomJarInstaller customJar)
    {
        _metadataHttp = metadataHttp;
        _downloadHttp = downloadHttp;
        Paths = paths;
        Logger = logger;
        Manifest = manifest;
        Installer = installer;
        Settings = settings;
        JavaDetector = javaDetector;
        Layout = layout;
        Profiles = profiles;
        Launch = launch;
        FabricMeta = fabricMeta;
        Fabric = fabric;
        QuiltMeta = quiltMeta;
        Quilt = quilt;
        ForgeMeta = forgeMeta;
        Forge = forge;
        NeoForgeMeta = neoForgeMeta;
        NeoForge = neoForge;
        Accounts = accounts;
        Ely = ely;
        Skins = skins;
        Modrinth = modrinth;
        Mods = mods;
        Mrpack = mrpack;
        CurseForge = curseForge;
        SkinLibrary = skinLibrary;
        CustomJar = customJar;
    }

    public static AppServices Create()
    {
        var paths = LauncherPaths.CreateDefault();
        var logger = new FileLauncherLogger(paths.LogFile);

        var metadataHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var downloadHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var client in new[] { metadataHttp, downloadHttp })
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "svik400/Minecraft-launcher-waundbox/0.11"); // формат Modrinth: автор/проект/версия (ParseAdd его не принимает)

        var platform = PlatformInfo.Current();
        var manifest = new VersionManifestProvider(metadataHttp, paths.ManifestCacheFile, TimeSpan.FromHours(1), logger);
        var downloader = new HttpFileDownloader(downloadHttp, logger);
        var layout = new MinecraftLayout(paths.GameRoot);
        var installer = new MinecraftInstaller(manifest, downloader, layout, platform, logger);

        var settings = new JsonSettingsStore(paths.SettingsFile, logger);
        var processRunner = new SystemProcessRunner();
        var javaProbe = new JavaProbe(processRunner, logger);
        var javaDetector = new JavaDetector(JavaSearchLocations.ForCurrentSystem, javaProbe, logger);
        var javaResolver = new JavaResolver(javaDetector, javaProbe, logger);

        var installed = new InstalledVersionRepository(layout);
        var authlib = new AuthlibInjectorProvider(metadataHttp, paths.AuthlibDirectory, logger);
        var launch = new GameLaunchService(installed, layout, javaResolver, settings, new SystemGameProcessStarter(), platform, logger,
            authlib: authlib);

        var fabricMeta = new HttpFabricMetaClient(metadataHttp);
        var fabric = new FabricInstaller(fabricMeta, installer, downloader, layout, platform, logger);
        var quiltMeta = new HttpQuiltMetaClient(metadataHttp);
        var quilt = new QuiltInstaller(quiltMeta, installer, downloader, layout, platform, logger);
        var forgeMeta = new HttpForgeMetaClient(metadataHttp);
        var forge = new ForgeInstaller(forgeMeta, installer, downloader, processRunner, javaResolver, settings, installed,
            layout, paths.InstallersDirectory, logger);

        var accounts = new AccountService(
            new MicrosoftOAuthClient(metadataHttp), new XboxClient(metadataHttp), new MinecraftServicesClient(metadataHttp),
            new DpapiAccountStore(paths.AccountFile, logger), settings, logger);

        var neoForgeMeta = new HttpNeoForgeMetaClient(metadataHttp);
        var neoForge = new NeoForgeInstaller(neoForgeMeta, installer, downloader, processRunner, javaResolver, settings, installed,
            layout, paths.InstallersDirectory, logger);

        var ely = new ElyAccountService(
            new HttpElyClient(metadataHttp), new DpapiElyAccountStore(paths.ElyAccountFile, logger), logger);

        var modrinth = new HttpModrinthClient(metadataHttp);
        var mods = new ModManager(modrinth, downloader, layout, logger);
        var mrpack = new MrpackImporter(downloader, layout, logger);
        var curseForge = new HttpCurseForgeClient(metadataHttp, async ct =>
        {
            // ключ из настроек → переменная среды WAUNDBOX_CURSEFORGE_KEY → встроенный ключ
            var key = (await settings.LoadAsync(ct)).CurseForgeApiKey;
            if (!string.IsNullOrWhiteSpace(key)) return key;
            var fromEnvironment = Environment.GetEnvironmentVariable("WAUNDBOX_CURSEFORGE_KEY");
            return string.IsNullOrWhiteSpace(fromEnvironment) ? BuiltInKeys.CurseForge : fromEnvironment;
        });

        return new AppServices(metadataHttp, downloadHttp, paths, logger, manifest, installer, settings, javaDetector,
            layout, new JsonProfileStore(paths.ProfilesFile), launch, fabricMeta, fabric, quiltMeta, quilt, forgeMeta, forge, neoForgeMeta, neoForge, accounts, ely,
            new SkinService(layout, paths.SkinFile, logger), modrinth, mods, mrpack, curseForge,
            new SkinLibrary(paths.SkinLibraryDirectory), new CustomJarInstaller(installer, installer, layout, logger));
    }

    public void Dispose()
    {
        _metadataHttp.Dispose();
        _downloadHttp.Dispose();
    }
}
