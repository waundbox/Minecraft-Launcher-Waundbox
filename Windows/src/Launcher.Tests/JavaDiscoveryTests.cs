using Launcher.Cli.Commands;
using Launcher.Core.Configuration;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.JavaRuntime;
using Launcher.Core.Versions;
using Launcher.Infrastructure.Configuration;
using Launcher.Infrastructure.JavaRuntime;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class JavaCandidateFinderTests : TempDirectoryTest
{
    private static string Exe => JavaExecutable.FileName;

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([Dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Finds_AllSources_InPriorityOrder()
    {
        var javaHome = Touch("home", "bin", Exe);
        var onPath = Touch("pathdir", Exe);
        var jdk17 = Touch("installs", "jdk-17", "bin", Exe);
        var jdk21 = Touch("installs", "jdk-21", "bin", Exe);
        var mojang = Touch("mojang", "java-runtime-delta", "windows-x64", "java-runtime-delta", "bin", Exe);
        Directory.CreateDirectory(Path.Combine(Dir, "installs", "empty"));

        var found = JavaCandidateFinder.Find(new JavaSearchLocations(
            Path.Combine(Dir, "home"),
            [Path.Combine(Dir, "pathdir"), Path.Combine(Dir, "missing-dir")],
            [Path.Combine(Dir, "installs"), Path.Combine(Dir, "no-such-root")],
            [Path.Combine(Dir, "mojang")]));

        var expected = new[]
        {
            (javaHome, JavaSource.JavaHome), (onPath, JavaSource.Path), (jdk17, JavaSource.Installed),
            (jdk21, JavaSource.Installed), (mojang, JavaSource.MojangRuntime)
        };
        Assert.Equal(expected, found.Select(f => (f.Path, f.Source)).ToArray());
    }

    [Fact]
    public void SameFile_IsReportedOnce()
    {
        var java = Touch("home", "bin", Exe);

        var found = JavaCandidateFinder.Find(new JavaSearchLocations(
            Path.Combine(Dir, "home"), [Path.Combine(Dir, "home", "bin")], [], []));

        Assert.Equal(java, Assert.Single(found).Path);
    }

    [Fact]
    public void BadPathEntries_DoNotThrow() =>
        Assert.Empty(JavaCandidateFinder.Find(new JavaSearchLocations(null, ["", "\0bad"], [], [])));
}

public class JavaDetectorTests : TempDirectoryTest
{
    [Fact]
    public async Task Detect_ProbesCandidates_AndSkipsBrokenOnes()
    {
        string Touch(string name)
        {
            var path = Path.Combine(Dir, "installs", name, "bin", JavaExecutable.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
            return path;
        }

        Touch("jdk-17"); Touch("jdk-21"); Touch("broken");
        var runner = new FakeProcessRunner(file =>
            file.Contains("jdk-17") ? JavaSamples.Ok("17.0.9")
            : file.Contains("jdk-21") ? JavaSamples.Ok("21.0.4")
            : new ProcessResult(-1, "", "boom", false));

        var detector = new JavaDetector(
            () => new JavaSearchLocations(null, [], [Path.Combine(Dir, "installs")], []),
            new JavaProbe(runner));

        var found = await detector.DetectAsync(default);

        Assert.Equal(new[] { 17, 21 }, found.Select(f => f.MajorVersion).ToArray()); // jdk-17 < jdk-21 по алфавиту, broken отброшен
        Assert.Equal(3, runner.Calls.Count);
    }

    [Fact]
    public async Task Detect_NothingOnDisk_ReturnsEmpty()
    {
        var detector = new JavaDetector(
            () => new JavaSearchLocations(null, [], [], []),
            new JavaProbe(new FakeProcessRunner(_ => JavaSamples.Ok("21"))));

        Assert.Empty(await detector.DetectAsync(default));
    }
}

public class JavaExecutableTests : TempDirectoryTest
{
    [Fact]
    public void ResolvePath_FolderWithBin_ReturnsExecutable()
    {
        var exe = Path.Combine(Dir, "jdk", "bin", JavaExecutable.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");

        Assert.Equal(exe, JavaExecutable.ResolvePath(Path.Combine(Dir, "jdk")));
        Assert.Equal(exe, JavaExecutable.ResolvePath($"\"{Path.Combine(Dir, "jdk")}\""));
    }

    [Fact]
    public void ResolvePath_File_IsReturnedAsIs() =>
        Assert.Equal("/some/java", JavaExecutable.ResolvePath("/some/java"));
}

public class JsonSettingsStoreTests : TempDirectoryTest
{
    private string File_ => Path.Combine(Dir, "sub", "settings.json");

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var store = new JsonSettingsStore(File_);

        await store.SaveAsync(new LauncherSettings("C:\\jdk\\bin\\java.exe"), default);

        Assert.Equal("C:\\jdk\\bin\\java.exe", (await new JsonSettingsStore(File_).LoadAsync(default)).JavaPath);
        Assert.False(File.Exists(File_ + ".tmp"));
    }

    [Fact]
    public async Task NewSettings_RoundTrip_AndDefaultsAreSane()
    {
        var store = new JsonSettingsStore(File_);
        await store.SaveAsync(new LauncherSettings(ModelSpin: false, DeveloperMode: true, IconSize: 64, ShowModel3D: false), default);

        var loaded = await new JsonSettingsStore(File_).LoadAsync(default);
        Assert.False(loaded.ModelSpin);
        Assert.True(loaded.DeveloperMode);
        Assert.Equal(64, loaded.IconSize);
        Assert.False(loaded.ShowModel3D);

        var defaults = new LauncherSettings();
        Assert.True(defaults.ModelSpin);
        Assert.True(defaults.ShowModel3D);
        Assert.False(defaults.DeveloperMode);
        Assert.Equal(32, defaults.IconSize);
        Assert.False(defaults.ShowFps);
        Assert.Null(defaults.TabOrder);

        await store.SaveAsync(new LauncherSettings(ShowFps: true, TabOrder: "home,logs", HiddenTabs: "account", HiddenHomeParts: "tagline", HomeLayout: "{}", BackgroundGlow: false), default);
        var layout = await new JsonSettingsStore(File_).LoadAsync(default);
        Assert.True(layout.ShowFps);
        Assert.Equal("home,logs", layout.TabOrder);
        Assert.Equal("account", layout.HiddenTabs);
        Assert.Equal("tagline", layout.HiddenHomeParts);
        Assert.Equal("{}", layout.HomeLayout);
        Assert.False(layout.BackgroundGlow);
        Assert.True(defaults.BackgroundGlow);
    }

    [Fact]
    public void HomeLayout_RoundTrips_SkipsDefaults_AndClamps()
    {
        var map = new Dictionary<string, ElementLayout>
        {
            ["model"] = new(X: 40, Scale: 0.8, Visible: false),
            ["panel"] = new(),
            ["play"] = new(Corner: 30, Z: 7)
        };

        var json = HomeLayout.Serialize(map);
        var back = HomeLayout.Parse(json);

        Assert.DoesNotContain("panel", back.Keys);
        Assert.Equal(40, back["model"].X);
        Assert.False(back["model"].Visible);
        Assert.Equal(30, back["play"].Corner);
        Assert.Equal(7, back["play"].Z);
        Assert.Equal(ElementLayout.MaxLayer, HomeLayout.Parse("{\"a\":{\"z\":900}}")["a"].Z);
        Assert.Null(HomeLayout.Serialize(new Dictionary<string, ElementLayout>()));
        Assert.Empty(HomeLayout.Parse("{ not json"));

        var wild = HomeLayout.Parse("{\"x\":{\"x\":99999,\"scale\":50,\"opacity\":-3}}")["x"];
        Assert.Equal(ElementLayout.MaxOffset, wild.X);
        Assert.Equal(ElementLayout.MaxScale, wild.Scale);
        Assert.Equal(0, wild.Opacity);
    }

    [Fact]
    public void HomeLayout_KeepsPageElementIds_AndScaleLimits()
    {
        var map = new Dictionary<string, ElementLayout>
        {
            ["settings/theme"] = new(Scale: 1.4),
            ["skins/skin"] = new(Scale: 0.1),
            ["profiles/list"] = new(Y: -20, Opacity: 0.5),
            ["profiles"] = new(X: 5) // главная и страницы не пересекаются
        };

        var back = HomeLayout.Parse(HomeLayout.Serialize(map));

        Assert.Equal(1.4, back["settings/theme"].Scale);
        Assert.Equal(ElementLayout.MinScale, back["skins/skin"].Scale);
        Assert.Equal(-20, back["profiles/list"].Y);
        Assert.Equal(5, back["profiles"].X);
        Assert.Equal(0, back["profiles/list"].X);
    }

    [Fact]
    public async Task Settings_KeepSecondLayoutAndProfileOrder()
    {
        var store = new JsonSettingsStore(File_);
        await store.SaveAsync(new LauncherSettings(HomeLayout: "{\"a\":{\"x\":1}}", HomeLayoutNoModel: "{\"b\":{\"y\":2}}", ProfileOrder: "Zeta,Alpha"), default);

        var back = await new JsonSettingsStore(File_).LoadAsync(default);

        Assert.Equal("{\"b\":{\"y\":2}}", back.HomeLayoutNoModel);
        Assert.Equal("Zeta,Alpha", back.ProfileOrder);
        Assert.Equal(2, HomeLayout.Parse(back.HomeLayoutNoModel)["b"].Y);
        Assert.Null((await new JsonSettingsStore(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".json")).LoadAsync(default)).ProfileOrder);
    }

    [Fact]
    public async Task MissingFile_GivesDefaults() =>
        Assert.Null((await new JsonSettingsStore(File_).LoadAsync(default)).JavaPath);

    [Fact]
    public async Task CorruptFile_GivesDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, "{ not json");

        Assert.Null((await new JsonSettingsStore(File_).LoadAsync(default)).JavaPath);
    }
}

public class InstalledVersionRepositoryTests : TempDirectoryTest
{
    private MinecraftLayout Layout => new(Path.Combine(Dir, "game"));

    [Fact]
    public async Task NotInstalled_SaysWhatToDo()
    {
        var ex = await Assert.ThrowsAsync<VersionNotFoundException>(
            () => new InstalledVersionRepository(Layout).LoadAsync("1.21.1", default));

        Assert.Contains("install 1.21.1", ex.Message);
    }

    [Fact]
    public async Task Installed_IsParsed()
    {
        var path = Layout.VersionJsonPath("1.21.1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, VersionTestData.VersionJson);

        var details = await new InstalledVersionRepository(Layout).LoadAsync("1.21.1", default);

        Assert.Equal(21, JavaRequirement.For(details));
        Assert.Equal(8, JavaRequirement.For(details with { JavaMajorVersion = null }));
    }
}

public class JavaCliOptionsTests
{
    [Fact]
    public void Detect_WithAndWithoutVersion()
    {
        Assert.Equal(JavaAction.Detect, JavaCliOptions.TryParse(["detect"], out _)!.Action);
        Assert.Equal("1.21.1", JavaCliOptions.TryParse(["detect", "--for", "1.21.1"], out _)!.ForVersion);
    }

    [Fact]
    public void Set_TakesPath() =>
        Assert.Equal("C:\\jdk", JavaCliOptions.TryParse(["set", "C:\\jdk"], out _)!.Path);

    [Fact]
    public void Clear_IsParsed() =>
        Assert.Equal(JavaAction.Clear, JavaCliOptions.TryParse(["clear"], out _)!.Action);

    [Theory]
    [InlineData("detect", "--for")]
    [InlineData("detect", "--nope")]
    [InlineData("set")]
    [InlineData("clear", "x")]
    [InlineData("fly")]
    public void Invalid_ReturnsError(params string[] args)
    {
        Assert.Null(JavaCliOptions.TryParse(args, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void NoArguments_ReturnsError()
    {
        Assert.Null(JavaCliOptions.TryParse([], out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
