using Launcher.Cli.Commands;
using Launcher.Core.Authentication;
using Launcher.Core.Errors;
using Launcher.Core.GameLaunch;
using Launcher.Core.Profiles;
using Launcher.Infrastructure.Profiles;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class DevelopmentAccountTests
{
    [Fact]
    public void Create_MarksAccountAsDevelopment()
    {
        var account = DevelopmentAccount.Create("Steve_01");

        Assert.True(account.IsDevelopment);
        Assert.Equal("legacy", account.UserType);
        Assert.Equal("Steve_01", account.Username);
    }

    [Fact]
    public void OfflineUuid_IsStable_Hex32_AndRfc4122Version3()
    {
        var uuid = DevelopmentAccount.OfflineUuid("Steve");

        Assert.Equal(uuid, DevelopmentAccount.OfflineUuid("Steve"));
        Assert.NotEqual(uuid, DevelopmentAccount.OfflineUuid("Alex"));
        Assert.Equal(32, uuid.Length);
        Assert.All(uuid, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.Equal('3', uuid[12]);
        Assert.Contains(uuid[16], "89ab");
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("a_very_long_name_over_16")]
    [InlineData("bad name")]
    [InlineData("имя")]
    public void BadUsername_IsRejected(string name) =>
        Assert.Throws<LauncherException>(() => DevelopmentAccount.Create(name));
}

public class GameProfileTests
{
    [Fact]
    public void Valid_Passes() => new GameProfile("My Profile-1.2", "1.21.1", 4096, 1024, "-XX:+UseG1GC", "--fullscreen").Validate();

    [Theory]
    [InlineData("")]
    [InlineData(" lead")]
    [InlineData("trail ")]
    [InlineData("dot.")]
    [InlineData("con")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void BadName_Throws(string name) =>
        Assert.Throws<ProfileException>(() => new GameProfile(name, "1.21.1").Validate());

    [Theory]
    [InlineData(100)]
    [InlineData(20_000_000)]
    public void MemoryOutOfRange_Throws(int memory) =>
        Assert.Throws<ProfileException>(() => new GameProfile("p", "1.21.1", memory).Validate());

    [Fact]
    public void HugeMemory_IsAllowed_AndUnknownGarbageCollector_Throws()
    {
        new GameProfile("p", "1.21.1", 745_472).Validate(); // 728 ГБ: лаунчер только предупреждает
        new GameProfile("p", "1.21.1", GarbageCollector: "g1").Validate();
        Assert.Throws<ProfileException>(() => new GameProfile("p", "1.21.1", GarbageCollector: "nope").Validate());
    }

    [Theory]
    [InlineData("-XX:+UseG1GC", true)]
    [InlineData("-XX:+UseZGC", true)]
    [InlineData("-XX:-UseSerialGC", true)]
    [InlineData("-XX:MaxGCPauseMillis=50", false)]
    [InlineData("-Xmx2G", false)]
    public void ContainsGcFlag_DetectsOnlyCollectorSwitches(string argument, bool expected) =>
        Assert.Equal(expected, GarbageCollectors.ContainsGcFlag([argument]));

    [Fact]
    public void MinMemoryAboveMax_Throws() =>
        Assert.Throws<ProfileException>(() => new GameProfile("p", "1.21.1", 1024, 2048).Validate());

    [Fact]
    public void BrokenQuotesInArguments_Throw() =>
        Assert.Throws<ProfileException>(() => new GameProfile("p", "1.21.1", JvmArguments: "-Dx=\"oops").Validate());
}

public class JsonProfileStoreTests : TempDirectoryTest
{
    private string FilePath => Path.Combine(Dir, "profiles.json");

    [Fact]
    public async Task SaveAndLoad_RoundTrips_AllFields()
    {
        var profile = new GameProfile("Main", "1.21.1", 4096, 1024, "-XX:+UseG1GC", "--fullscreen", "C:\\jdk\\bin\\java.exe");

        await new JsonProfileStore(FilePath).SaveAsync(profile, default);
        var loaded = await new JsonProfileStore(FilePath).FindAsync("Main", default);

        Assert.Equal(profile, loaded);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public async Task Icon_RoundTrips_AndOldFilesWithoutIconLoad()
    {
        var store = new JsonProfileStore(FilePath);
        await store.SaveAsync(new GameProfile("Main", "1.21.1", Icon: "creeper.png"), default);

        Assert.Equal("creeper.png", (await store.FindAsync("Main", default))!.Icon);

        File.WriteAllText(FilePath, "[{\"name\":\"Old\",\"versionId\":\"1.20.1\"}]");
        Assert.Null((await new JsonProfileStore(FilePath).FindAsync("Old", default))?.Icon);
    }

    [Fact]
    public async Task Find_IsCaseInsensitive_AndSaveReplacesSameName()
    {
        var store = new JsonProfileStore(FilePath);
        await store.SaveAsync(new GameProfile("Main", "1.20.4"), default);
        await store.SaveAsync(new GameProfile("MAIN", "1.21.1"), default);

        var all = await store.ListAsync(default);

        Assert.Equal("1.21.1", Assert.Single(all).VersionId);
        Assert.NotNull(await store.FindAsync("main", default));
    }

    [Fact]
    public async Task List_IsSortedByName()
    {
        var store = new JsonProfileStore(FilePath);
        await store.SaveAsync(new GameProfile("beta", "1"), default);
        await store.SaveAsync(new GameProfile("Alpha", "1"), default);

        Assert.Equal(new[] { "Alpha", "beta" }, (await store.ListAsync(default)).Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Delete_RemovesProfile_AndReportsWhetherItExisted()
    {
        var store = new JsonProfileStore(FilePath);
        await store.SaveAsync(new GameProfile("Main", "1.21.1"), default);

        Assert.True(await store.DeleteAsync("main", default));
        Assert.False(await store.DeleteAsync("main", default));
        Assert.Empty(await store.ListAsync(default));
    }

    [Fact]
    public async Task MissingFile_MeansNoProfiles() =>
        Assert.Empty(await new JsonProfileStore(FilePath).ListAsync(default));

    [Fact]
    public async Task CorruptFile_IsReported_AndNotOverwritten()
    {
        File.WriteAllText(FilePath, "{ definitely not json");
        var store = new JsonProfileStore(FilePath);

        await Assert.ThrowsAsync<ProfileException>(() => store.ListAsync(default));
        await Assert.ThrowsAsync<ProfileException>(() => store.SaveAsync(new GameProfile("Main", "1"), default));
        Assert.Equal("{ definitely not json", File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task InvalidProfile_IsNotSaved()
    {
        var store = new JsonProfileStore(FilePath);

        await Assert.ThrowsAsync<ProfileException>(() => store.SaveAsync(new GameProfile("bad/name", "1.21.1"), default));
        Assert.False(File.Exists(FilePath));
    }
}

public class Stage4CliOptionsTests
{
    [Theory]
    [InlineData("4096", 4096)]
    [InlineData("4096M", 4096)]
    [InlineData("4G", 4096)]
    [InlineData("2g", 2048)]
    public void MemorySize_Parses(string text, int expected)
    {
        Assert.True(MemorySize.TryParseMegabytes(text, out var mb));
        Assert.Equal(expected, mb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("G")]
    public void MemorySize_RejectsGarbage(string text) => Assert.False(MemorySize.TryParseMegabytes(text, out _));

    [Fact]
    public void Launch_ParsesOptions()
    {
        var o = LaunchCliOptions.TryParse(["Main", "--username", "Steve", "--dry-run", "--quiet"], out var error);

        Assert.Null(error);
        Assert.Equal("Main", o!.ProfileName);
        Assert.Equal("Steve", o.Username);
        Assert.True(o.Dev); // --username означает development-режим
        Assert.True(o.DryRun);
        Assert.True(o.Quiet);
    }

    [Fact]
    public void Launch_ByDefault_UsesSignedInAccount()
    {
        var o = LaunchCliOptions.TryParse(["Main"], out _)!;

        Assert.False(o.Dev);
        Assert.Null(o.Username);
    }

    [Fact]
    public void Launch_DevFlag_ForcesDevelopmentMode() =>
        Assert.True(LaunchCliOptions.TryParse(["Main", "--dev"], out _)!.Dev);

    [Theory]
    [InlineData("a", "b")]
    [InlineData("a", "--username")]
    [InlineData("a", "--nope")]
    public void Launch_Invalid_ReturnsError(params string[] args)
    {
        Assert.Null(LaunchCliOptions.TryParse(args, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void NoArguments_ReturnErrors()
    {
        Assert.Null(LaunchCliOptions.TryParse([], out var launchError));
        Assert.False(string.IsNullOrWhiteSpace(launchError));
        Assert.Null(ProfilesCliOptions.TryParse([], out var profilesError));
        Assert.False(string.IsNullOrWhiteSpace(profilesError));
    }

    [Fact]
    public void ProfilesCreate_ParsesAllOptions()
    {
        var o = ProfilesCliOptions.TryParse(
            ["create", "Main", "--version", "1.21.1", "--memory", "4G", "--min-memory", "1G",
             "--jvm-args", "-XX:+UseG1GC", "--game-args", "--fullscreen", "--java", "C:\\jdk"], out var error);

        Assert.Null(error);
        Assert.Equal(ProfilesAction.Create, o!.Action);
        Assert.Equal(4096, o.MaxMemoryMb);
        Assert.Equal(1024, o.MinMemoryMb);
        Assert.Equal("-XX:+UseG1GC", o.JvmArguments);
        Assert.Equal("--fullscreen", o.GameArguments);
        Assert.Equal("C:\\jdk", o.JavaPath);
    }

    [Fact]
    public void ProfilesEdit_EmptyValueMeansReset()
    {
        var o = ProfilesCliOptions.TryParse(["edit", "Main", "--jvm-args", "", "--min-memory", "0"], out _)!;

        Assert.True(o.JvmArgumentsSet);
        Assert.Null(o.JvmArguments);
        Assert.True(o.MinMemorySet);
        Assert.Null(o.MinMemoryMb);
    }

    [Theory]
    [InlineData("fly")]
    [InlineData("show")]
    [InlineData("create", "Main")]
    [InlineData("create", "Main", "--version")]
    [InlineData("create", "Main", "--version", "1.21.1", "--memory", "lots")]
    [InlineData("show", "Main", "--memory", "2G")]
    [InlineData("list", "extra")]
    [InlineData("edit", "Main", "--nope", "x")]
    public void Profiles_Invalid_ReturnsError(params string[] args)
    {
        Assert.Null(ProfilesCliOptions.TryParse(args, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

public class FabricCliOptionsTests
{
    [Fact]
    public void Install_ParsesVersionLoaderAndConcurrency()
    {
        var o = Launcher.Cli.Commands.FabricCliOptions.TryParse(["install", "1.21.1", "--loader", "0.19.4", "--concurrency", "4"], out var error);

        Assert.Null(error);
        Assert.Equal(Launcher.Cli.Commands.FabricAction.Install, o!.Action);
        Assert.Equal("1.21.1", o.GameVersion);
        Assert.Equal("0.19.4", o.LoaderVersion);
        Assert.Equal(4, o.Concurrency);
    }

    [Fact]
    public void Loaders_NeedsVersion()
    {
        Assert.Null(Launcher.Cli.Commands.FabricCliOptions.TryParse(["loaders"], out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("install 1.21.1 --loader")]
    [InlineData("install 1.21.1 --bogus")]
    [InlineData("loaders 1.21.1 --loader 0.1")]
    [InlineData("remove 1.21.1")]
    public void BadInput_IsRejected(string line) =>
        Assert.Null(Launcher.Cli.Commands.FabricCliOptions.TryParse(line.Split(' '), out _));
}

public class ForgeCliOptionsTests
{
    [Fact]
    public void Install_ParsesVersionAndForge()
    {
        var o = ForgeCliOptions.TryParse(["install", "1.21.1", "--forge", "52.1.19"], out var error);

        Assert.Null(error);
        Assert.Equal(ForgeAction.Install, o!.Action);
        Assert.Equal("1.21.1", o.GameVersion);
        Assert.Equal("52.1.19", o.ForgeVersion);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("install 1.21.1 --forge")]
    [InlineData("versions 1.21.1 --forge 1")]
    [InlineData("remove 1.21.1")]
    public void BadInput_IsRejected(string line) =>
        Assert.Null(ForgeCliOptions.TryParse(line.Split(' '), out _));
}
