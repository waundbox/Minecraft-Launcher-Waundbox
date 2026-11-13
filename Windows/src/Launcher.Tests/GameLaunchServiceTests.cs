using Launcher.Core.Authentication;
using Launcher.Core.Configuration;
using Launcher.Core.Errors;
using Launcher.Core.GameLaunch;
using Launcher.Core.Installation;
using Launcher.Core.JavaRuntime;
using Launcher.Core.Libraries;
using Launcher.Core.Profiles;
using Launcher.Core.Versions;
using Launcher.Infrastructure.GameLaunch;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class GameLaunchServiceTests : TempDirectoryTest
{
    private static readonly PlatformInfo Windows = new("windows", "x86_64", "10.0.22631.0");
    private static readonly GameAccount Account = DevelopmentAccount.Create("Steve");

    private readonly MinecraftLayout _layout;
    private readonly FakeStarter _starter = new();

    public GameLaunchServiceTests() => _layout = new MinecraftLayout(Path.Combine(Dir, "game"));

    /// <summary>Создаёт на диске «установленную» версию: все файлы существуют и имеют ожидаемые размеры.</summary>
    private void InstallFixture(string? versionJson = null)
    {
        var json = versionJson ?? LaunchTestData.VersionJson;
        Write(_layout.VersionJsonPath("1.21.1"), json);

        var details = VersionDetailsParser.Parse(LaunchTestData.VersionJson);
        foreach (var lib in LibraryResolver.Resolve(details.Libraries, Windows))
        {
            if (lib.Artifact is not null) Bytes(_layout.LibraryPath(lib.Artifact.Path!), (int)lib.Artifact.Size!.Value);
            if (lib.NativeArtifact is not null) Bytes(_layout.LibraryPath(lib.NativeArtifact.Path!), (int)lib.NativeArtifact.Size!.Value);
        }
        Bytes(_layout.ClientJarPath("1.21.1"), 5000);

        Write(_layout.AssetIndexPath("17"), VersionTestData.AssetIndexJson);
        Bytes(_layout.AssetObjectPath(VersionTestData.HashA), 3665);
        Bytes(_layout.AssetObjectPath(VersionTestData.HashC), 120);
        Bytes(_layout.LogConfigPath("client-1.12.xml"), 888);
        Bytes(Path.Combine(_layout.NativesDirectory("1.21.1"), "lwjgl.dll"), 10);
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Bytes(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    private GameLaunchService CreateService(
        IReadOnlyList<JavaInstallation>? java = null, LauncherSettings? settings = null, Func<string, ProcessResult>? probe = null,
        IAuthlibInjectorProvider? authlib = null)
    {
        var installations = (java ?? [JavaSamples.Make(21, path: "/jdk21/bin/java")]).ToArray();
        var javaProbe = new JavaProbe(new FakeProcessRunner(probe ?? (_ => JavaSamples.Ok("21.0.4"))));
        return new GameLaunchService(
            new InstalledVersionRepository(_layout), _layout,
            new JavaResolver(new FakeJavaDetector(installations), javaProbe),
            new MemorySettings(settings), _starter, Windows, authlib: authlib);
    }

    private static string After(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        Assert.True(i >= 0 && i + 1 < args.Count, $"аргумент {flag} не найден");
        return args[i + 1];
    }

    private Task<GameLaunchPlan> Prepare(GameProfile? profile = null) =>
        CreateService().PrepareAsync(profile ?? new GameProfile("Main", "1.21.1"), Account, default);

    [Fact]
    public async Task Prepare_BuildsClasspathAndArgumentsInTheRightOrder()
    {
        InstallFixture();

        var plan = await Prepare(new GameProfile("Main", "1.21.1", 4096, 1024));
        var args = plan.Arguments;

        Assert.Equal("-Xmx4096M", args[0]);
        Assert.Contains("-Xms1024M", args);
        Assert.Equal("/jdk21/bin/java", plan.JavaPath);

        // главный класс — после всех JVM-аргументов и ровно один раз
        var mainIndex = args.ToList().IndexOf("net.minecraft.client.main.Main");
        Assert.True(mainIndex > args.ToList().IndexOf("-cp"));
        Assert.Single(args, a => a == "net.minecraft.client.main.Main");
        Assert.True(args.ToList().IndexOf("--username") > mainIndex);

        // classpath: библиотеки текущей ОС, клиент последним
        var classpath = After(args, "-cp").Split(Path.PathSeparator);
        Assert.EndsWith("1.21.1.jar", classpath[^1]);
        Assert.Contains(classpath, p => p.EndsWith("logging-1.2.7.jar"));
        Assert.Contains(classpath, p => p.EndsWith("natives-windows.jar") && p.Contains("lwjgl-3.3.3"));
        Assert.DoesNotContain(classpath, p => p.Contains("natives-linux"));
        Assert.All(classpath, p => Assert.True(File.Exists(p)));

        Assert.Contains("-Djava.library.path=" + _layout.NativesDirectory("1.21.1"), args);
        Assert.Contains("-Dminecraft.launcher.brand=Waundbox", args);
        Assert.Contains("-XX:HeapDumpPath=MojangTricks.heapdump", args);
        Assert.DoesNotContain("-XstartOnFirstThread", args);
    }

    [Fact]
    public async Task Prepare_SubstitutesGameVariables_AndSkipsFeatureArguments()
    {
        InstallFixture();

        var plan = await Prepare();
        var args = plan.Arguments;

        Assert.Equal("Steve", After(args, "--username"));
        Assert.Equal("1.21.1", After(args, "--version"));
        Assert.Equal(_layout.InstanceDirectory("Main"), After(args, "--gameDir"));
        Assert.Equal(_layout.InstanceDirectory("Main"), plan.WorkingDirectory);
        Assert.Equal(_layout.AssetsDirectory, After(args, "--assetsDir"));
        Assert.Equal("17", After(args, "--assetIndex"));
        Assert.Equal(Account.Uuid, After(args, "--uuid"));
        Assert.Equal("legacy", After(args, "--userType"));
        Assert.Equal("release", After(args, "--versionType"));
        Assert.DoesNotContain("--demo", args);
        Assert.DoesNotContain("--width", args);
        Assert.DoesNotContain(args, a => a.Contains("${"));
        Assert.True(Directory.Exists(plan.WorkingDirectory));
    }

    [Fact]
    public async Task Prepare_AddsLog4jConfiguration()
    {
        InstallFixture();

        var plan = await Prepare();

        Assert.Contains("-Dlog4j.configurationFile=" + _layout.LogConfigPath("client-1.12.xml"), plan.Arguments);
    }

    [Fact]
    public async Task Prepare_AddsUserArguments_InTheirPlaces()
    {
        InstallFixture();

        var plan = await Prepare(new GameProfile("Main", "1.21.1", JvmArguments: "-XX:+UseG1GC -Dname=\"a b\"", GameArguments: "--fullscreen"));
        var args = plan.Arguments.ToList();
        var main = args.IndexOf("net.minecraft.client.main.Main");

        Assert.True(args.IndexOf("-XX:+UseG1GC") is > 0 && args.IndexOf("-XX:+UseG1GC") < main);
        Assert.True(args.IndexOf("-Dname=a b") < main);
        Assert.Equal("--fullscreen", args[^1]);
    }

    [Fact]
    public async Task Prepare_AddsChosenGarbageCollector_BeforeUserArguments()
    {
        InstallFixture();

        var plan = await Prepare(new GameProfile("Main", "1.21.1", GarbageCollector: "zgc"));
        var args = plan.Arguments.ToList();

        Assert.True(args.IndexOf("-XX:+UseZGC") is > 0 && args.IndexOf("-XX:+UseZGC") < args.IndexOf("net.minecraft.client.main.Main"));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public async Task Prepare_DefaultGarbageCollector_AddsNothing()
    {
        InstallFixture();

        var plan = await Prepare();

        Assert.DoesNotContain(plan.Arguments, a => a.StartsWith("-XX:+Use", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepare_GarbageCollectorInUserArguments_WinsAndWarns()
    {
        InstallFixture();

        var plan = await Prepare(new GameProfile("Main", "1.21.1", JvmArguments: "-XX:+UseParallelGC", GarbageCollector: "zgc"));

        Assert.DoesNotContain("-XX:+UseZGC", plan.Arguments);
        Assert.Contains("-XX:+UseParallelGC", plan.Arguments);
        Assert.Contains(plan.Warnings, w => w.Contains("сборщик"));
    }

    [Fact]
    public async Task Prepare_GarbageCollectorUnsupportedByJava_FallsBackWithWarning()
    {
        InstallFixture(LaunchTestData.VersionJson.Replace("\"majorVersion\": 21", "\"majorVersion\": 8"));
        var service = CreateService([JavaSamples.Make(8, path: "/jdk8/bin/java")], probe: _ => JavaSamples.Ok("1.8.0_402"));

        var plan = await service.PrepareAsync(new GameProfile("Main", "1.21.1", GarbageCollector: "zgc"), Account, default);

        Assert.DoesNotContain("-XX:+UseZGC", plan.Arguments);
        Assert.Contains(plan.Warnings, w => w.Contains("ZGC"));
    }

    [Fact]
    public async Task Prepare_NeverShowsTheAccessToken()
    {
        InstallFixture();

        var plan = await Prepare();
        var text = plan.ToDisplayCommandLine();

        Assert.Contains(DevelopmentAccount.PlaceholderToken, plan.Arguments);
        Assert.DoesNotContain(DevelopmentAccount.PlaceholderToken, text);
        Assert.Contains("***", text);
    }

    [Fact]
    public async Task Prepare_MissingLibrary_SaysWhichAndHowToFix()
    {
        InstallFixture();
        File.Delete(_layout.LibraryPath("com/mojang/logging/1.2.7/logging-1.2.7.jar"));

        var ex = await Assert.ThrowsAsync<LaunchException>(() => Prepare());

        Assert.Contains("com.mojang:logging", ex.Message);
        Assert.Contains("install 1.21.1", ex.Message);
    }

    [Fact]
    public async Task Prepare_LibraryWithWrongSize_IsReportedAsCorrupt()
    {
        InstallFixture();
        File.WriteAllBytes(_layout.ClientJarPath("1.21.1"), new byte[10]);

        var ex = await Assert.ThrowsAsync<LaunchException>(() => Prepare());

        Assert.Contains("повреждён", ex.Message);
    }

    [Fact]
    public async Task Prepare_MissingNativesArchive_IsReported()
    {
        InstallFixture();
        File.Delete(_layout.LibraryPath("org/lwjgl/lwjgl/lwjgl-platform/2.9.4/lwjgl-platform-2.9.4-natives-windows.jar"));

        var ex = await Assert.ThrowsAsync<LaunchException>(() => Prepare());

        Assert.Contains("natives", ex.Message);
    }

    [Fact]
    public async Task Prepare_MissingLogConfig_IsReported()
    {
        InstallFixture();
        File.Delete(_layout.LogConfigPath("client-1.12.xml"));

        var ex = await Assert.ThrowsAsync<LaunchException>(() => Prepare());

        Assert.Contains("логирования", ex.Message);
    }

    [Fact]
    public async Task Prepare_MissingAssets_IsReported()
    {
        InstallFixture();
        File.Delete(_layout.AssetObjectPath(VersionTestData.HashC));

        var ex = await Assert.ThrowsAsync<LaunchException>(() => Prepare());

        Assert.Contains("ресурс", ex.Message);
        Assert.Contains("install", ex.Message);
    }

    [Fact]
    public async Task Prepare_NotInstalledVersion_SaysToInstall()
    {
        var ex = await Assert.ThrowsAsync<VersionNotFoundException>(() => Prepare());

        Assert.Contains("install 1.21.1", ex.Message);
    }

    [Fact]
    public async Task Prepare_InheritedVersionWithoutBase_SaysToInstallBase()
    {
        Write(_layout.VersionJsonPath(FabricTestData.ChildId), FabricTestData.ProfileJson);

        var ex = await Assert.ThrowsAsync<VersionNotFoundException>(
            () => Prepare(new GameProfile("Main", FabricTestData.ChildId)));

        Assert.Contains("install 1.21.1", ex.Message);
    }

    [Fact]
    public async Task Prepare_FabricProfile_MergesWithBaseVersion()
    {
        InstallFixture();
        Write(_layout.VersionJsonPath(FabricTestData.ChildId), FabricTestData.ProfileJson);
        Bytes(_layout.LibraryPath("org/ow2/asm/asm/9.10.1/asm-9.10.1.jar"), 40);
        Bytes(_layout.LibraryPath("com/mojang/logging/9.9.9/logging-9.9.9.jar"), 7);
        File.WriteAllBytes(_layout.LibraryPath("net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar").EnsureDir(), new byte[3]);

        var plan = await Prepare(new GameProfile("Main", FabricTestData.ChildId));
        var args = plan.Arguments;

        // главный класс Fabric вместо ванильного
        Assert.Single(args, a => a == "net.fabricmc.loader.impl.launch.knot.KnotClient");
        Assert.DoesNotContain("net.minecraft.client.main.Main", args);
        // JVM-аргумент Fabric — одним элементом, с пробелами внутри
        Assert.Contains("-DFabricMcEmu= net.minecraft.client.main.Main ", args);
        // игровые аргументы базовой версии сохранились, версия — идентификатор Fabric
        Assert.Equal(FabricTestData.ChildId, After(args, "--version"));
        // classpath: библиотеки Fabric первыми, одноимённая библиотека базовой версии заменена, client.jar базовой версии — последний
        var classpath = After(args, "-cp").Split(Path.PathSeparator);
        Assert.Contains(classpath, p => p.EndsWith("asm-9.10.1.jar"));
        Assert.Contains(classpath, p => p.EndsWith("logging-9.9.9.jar"));
        Assert.DoesNotContain(classpath, p => p.EndsWith("logging-1.2.7.jar"));
        Assert.EndsWith(Path.Combine("1.21.1", "1.21.1.jar"), classpath[^1]);
        Assert.Equal(21, plan.JavaMajorVersion);
    }

    [Fact]
    public async Task Prepare_FabricProfile_MissingLoaderLibrary_IsReported()
    {
        InstallFixture();
        Write(_layout.VersionJsonPath(FabricTestData.ChildId), FabricTestData.ProfileJson);

        var ex = await Assert.ThrowsAsync<LaunchException>(() => Prepare(new GameProfile("Main", FabricTestData.ChildId)));

        Assert.Contains("fabric", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prepare_ForgeProfile_UsesInstallerCreatedJar_AndSubstitutesLibraryVariables()
    {
        InstallFixture();
        Write(_layout.VersionJsonPath(ForgeTestData.ForgeId), ForgeTestData.VersionJson);
        Bytes(_layout.LibraryPath("net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-universal.jar"), 9);
        Bytes(_layout.LibraryPath("cpw/mods/bootstraplauncher/1.1.2/bootstraplauncher-1.1.2.jar"), 5);

        var plan = await Prepare(new GameProfile("Main", ForgeTestData.ForgeId));
        var args = plan.Arguments;

        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", args[args.ToList().FindIndex(a => a.StartsWith("cpw."))]);
        Assert.Contains("-DlibraryDirectory=" + _layout.LibrariesDirectory, args);
        var sep = Path.PathSeparator;
        Assert.Equal($"{_layout.LibrariesDirectory}/a.jar{sep}{_layout.LibrariesDirectory}/b.jar", After(args, "-p"));
        Assert.Contains("forgeclient", args);
        Assert.Contains(After(args, "-cp").Split(sep), p => p.EndsWith("forge-1.21.1-52.1.0-universal.jar"));
    }

    [Fact]
    public async Task Prepare_JavaTooOld_ExplainsWhatToInstall()
    {
        InstallFixture();
        var service = CreateService(java: [JavaSamples.Make(8)]);

        var ex = await Assert.ThrowsAsync<JavaNotFoundException>(
            () => service.PrepareAsync(new GameProfile("Main", "1.21.1"), Account, default));

        Assert.Contains("Java 21", ex.Message);
    }

    [Fact]
    public async Task Prepare_ProfileJavaPath_BeatsGlobalSettingAndDetection()
    {
        InstallFixture();
        var service = CreateService(settings: new LauncherSettings("/global/java"));

        var plan = await service.PrepareAsync(new GameProfile("Main", "1.21.1", JavaPath: "/profile/java"), Account, default);

        Assert.Equal("/profile/java", plan.JavaPath);
    }

    [Fact]
    public async Task Prepare_GlobalJavaSetting_BeatsDetection()
    {
        InstallFixture();
        var service = CreateService(settings: new LauncherSettings("/global/java"));

        var plan = await service.PrepareAsync(new GameProfile("Main", "1.21.1"), Account, default);

        Assert.Equal("/global/java", plan.JavaPath);
    }

    [Fact]
    public async Task Prepare_32BitJavaWithLotsOfMemory_Warns()
    {
        InstallFixture();
        var service = CreateService(java: [JavaSamples.Make(21, is64: false, path: "/jdk32/java")]);

        var plan = await service.PrepareAsync(new GameProfile("Main", "1.21.1", 4096), Account, default);

        Assert.Contains(plan.Warnings, w => w.Contains("32-бит"));
    }

    [Fact]
    public async Task Prepare_InvalidProfile_IsRejectedBeforeAnythingElse() =>
        await Assert.ThrowsAsync<ProfileException>(() => Prepare(new GameProfile("bad/name", "1.21.1")));

    [Fact]
    public async Task Launch_PassesThePlanToTheProcessStarter()
    {
        InstallFixture();
        var service = CreateService();
        var plan = await service.PrepareAsync(new GameProfile("Main", "1.21.1"), Account, default);

        var process = service.Launch(plan, null);

        Assert.Same(plan, _starter.Plan);
        Assert.Equal(4242, process.ProcessId);
        Assert.True((await process.Completion).IsSuccess);
    }

    [Fact]
    public void RealStarter_MissingJava_GivesReadableError()
    {
        var plan = new GameLaunchPlan("1.21.1", "Main", Path.Combine(Dir, "no-such-java"), 21, ["-version"], Dir, [], []);

        var ex = Assert.Throws<LaunchException>(() => new SystemGameProcessStarter().Start(plan, null));

        Assert.Contains("Java", ex.Message);
    }

    private sealed class FakeAuthlib : IAuthlibInjectorProvider
    {
        public Task<string> GetJarPathAsync(CancellationToken ct) => Task.FromResult("/runtime/authlib-injector.jar");
    }

    private static readonly GameAccount ElyAccount =
        new("Steve", "abc123", "tok", "mojang", IsDevelopment: false, AuthlibServer: "ely.by");

    [Fact]
    public async Task ElyAccount_AddsAuthlibInjectorBeforeMainClass()
    {
        InstallFixture();
        var plan = await CreateService(authlib: new FakeAuthlib())
            .PrepareAsync(new GameProfile("Main", "1.21.1"), ElyAccount, default);

        var agent = plan.Arguments.ToList().IndexOf("-javaagent:/runtime/authlib-injector.jar=ely.by");
        Assert.True(agent >= 0, "аргумент -javaagent не найден");
        Assert.True(agent < plan.Arguments.ToList().IndexOf(plan.Arguments.First(a => a.StartsWith("net.minecraft"))));
    }

    [Fact]
    public async Task ElyAccount_WithoutProvider_Throws()
    {
        InstallFixture();
        await Assert.ThrowsAsync<LaunchException>(() => CreateService()
            .PrepareAsync(new GameProfile("Main", "1.21.1"), ElyAccount, default));
    }

    [Fact]
    public async Task OtherAccounts_DoNotGetAuthlibInjector()
    {
        InstallFixture();
        var plan = await CreateService(authlib: new FakeAuthlib()).PrepareAsync(new GameProfile("Main", "1.21.1"), Account, default);
        Assert.DoesNotContain(plan.Arguments, a => a.StartsWith("-javaagent"));
    }
}

internal static class PathTestExtensions
{
    /// <summary>Создаёт папку для файла и возвращает путь.</summary>
    public static string EnsureDir(this string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
