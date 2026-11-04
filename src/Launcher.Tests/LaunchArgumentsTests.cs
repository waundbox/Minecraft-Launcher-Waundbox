using Launcher.Core.Errors;
using Launcher.Core.GameLaunch;
using Launcher.Core.Libraries;

namespace Launcher.Tests;

public class CommandLineSplitterTests
{
    [Fact]
    public void Splits_ByWhitespace_AndKeepsQuotedValuesTogether() =>
        Assert.Equal(
            new[] { "-XX:+UseG1GC", "-Dname=a b", "--flag" },
            CommandLineSplitter.Split("  -XX:+UseG1GC   -Dname=\"a b\"  --flag ").ToArray());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_GivesNoArguments(string? text) => Assert.Empty(CommandLineSplitter.Split(text));

    [Fact]
    public void UnclosedQuote_Throws() =>
        Assert.Throws<LauncherException>(() => CommandLineSplitter.Split("-Dx=\"abc"));
}

public class LaunchArgumentsParserTests
{
    [Fact]
    public void Modern_ParsesGameAndJvm_WithRules()
    {
        var args = LaunchArgumentsParser.Parse(LaunchTestData.VersionJson);

        Assert.False(args.IsLegacy);
        Assert.Contains(args.Game, i => i.Values.Count == 1 && i.Values[0] == "--demo" && i.Rules.Single().Features["is_demo_user"]);
        Assert.Contains(args.Jvm, i => i.Values.Contains("-XstartOnFirstThread") && i.Rules.Single().OsName == "osx");
        Assert.Contains(args.Jvm, i => i.Values.Count == 1 && i.Values[0] == "-cp" && i.Rules.Count == 0);
    }

    [Fact]
    public void Legacy_SplitsString_AndAddsDefaultJvmArguments()
    {
        var args = LaunchArgumentsParser.Parse(LaunchTestData.LegacyJson);

        Assert.True(args.IsLegacy);
        Assert.Equal("--username", args.Game[0].Values.Single());
        Assert.Equal("${auth_player_name}", args.Game[1].Values.Single());
        Assert.Contains(args.Jvm, i => i.Values.Single() == "-Djava.library.path=${natives_directory}");
        Assert.Contains(args.Jvm, i => i.Values.Single() == "${classpath}");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("nope")]
    [InlineData("{\"arguments\":{\"game\":[{\"rules\":[{\"action\":\"maybe\"}],\"value\":\"x\"}]}}")]
    [InlineData("{\"arguments\":{\"game\":[5]}}")]
    public void Invalid_Throws(string json) =>
        Assert.Throws<VersionMetadataException>(() => LaunchArgumentsParser.Parse(json));
}

public class LaunchArgumentBuilderTests
{
    private static readonly PlatformInfo Windows = new("windows", "x86_64", "10.0.22631.0");
    private static readonly PlatformInfo Mac = new("osx", "arm64", null);
    private static readonly HashSet<string> NoFeatures = new();

    private static Dictionary<string, string> Vars() => new()
    {
        ["auth_player_name"] = "Steve", ["version_name"] = "1.7.10", ["game_directory"] = "C:\\game dir",
        ["assets_root"] = "assets", ["assets_index_name"] = "legacy", ["auth_uuid"] = "uuid", ["auth_access_token"] = "tok",
        ["user_properties"] = "{}", ["user_type"] = "legacy", ["version_type"] = "release", ["natives_directory"] = "natives",
        ["launcher_name"] = "Waundbox", ["launcher_version"] = "0.4", ["classpath"] = "a.jar;b.jar"
    };

    [Fact]
    public void Substitute_ReplacesKnownVariables() =>
        Assert.Equal("--gameDir C:\\game dir", LaunchArgumentBuilder.Substitute("--gameDir ${game_directory}", Vars()));

    [Fact]
    public void Substitute_UnknownVariable_IsAnError()
    {
        var ex = Assert.Throws<LaunchException>(() => LaunchArgumentBuilder.Substitute("--x ${mystery}", Vars()));
        Assert.Contains("mystery", ex.Message);
    }

    [Fact]
    public void Legacy_FullBuild_ProducesClassicArguments()
    {
        var (jvm, game) = LaunchArgumentBuilder.Build(
            LaunchArgumentsParser.Parse(LaunchTestData.LegacyJson), Windows, Vars(), NoFeatures);

        Assert.Equal(new[] { "--username", "Steve" }, game.Take(2).ToArray());
        Assert.Contains("C:\\game dir", game); // значение с пробелом остаётся одним аргументом
        Assert.Equal(new[] { "-cp", "a.jar;b.jar" }, jvm.Skip(jvm.Count - 2).ToArray());
        Assert.Contains("-Djava.library.path=natives", jvm);
    }

    [Fact]
    public void Modern_OsRules_SelectPlatformSpecificArguments()
    {
        var args = LaunchArgumentsParser.Parse(LaunchTestData.VersionJson);
        var vars = Vars();
        vars["resolution_width"] = "1"; vars["resolution_height"] = "1";

        var (winJvm, _) = LaunchArgumentBuilder.Build(args, Windows, vars, NoFeatures);
        var (macJvm, _) = LaunchArgumentBuilder.Build(args, Mac, vars, NoFeatures);

        Assert.Contains("-XX:HeapDumpPath=MojangTricks.heapdump", winJvm);
        Assert.DoesNotContain("-XstartOnFirstThread", winJvm);
        Assert.Contains("-XstartOnFirstThread", macJvm);
        Assert.DoesNotContain("-XX:HeapDumpPath=MojangTricks.heapdump", macJvm);
    }

    [Fact]
    public void Modern_FeatureRules_AreOffByDefault_AndOnWhenEnabled()
    {
        var args = LaunchArgumentsParser.Parse(LaunchTestData.VersionJson);
        var vars = Vars();
        vars["resolution_width"] = "1280"; vars["resolution_height"] = "720";

        var (_, off) = LaunchArgumentBuilder.Build(args, Windows, vars, NoFeatures);
        var (_, on) = LaunchArgumentBuilder.Build(args, Windows, vars, new HashSet<string> { "has_custom_resolution" });

        Assert.DoesNotContain("--width", off);
        Assert.DoesNotContain("--demo", off);
        Assert.Contains("--width", on);
        Assert.Contains("1280", on);
    }

    [Fact]
    public void Rules_LastMatchingRuleWins()
    {
        ArgumentRule[] rules =
        [
            new(true, null, null, null, new Dictionary<string, bool>()),
            new(false, "osx", null, null, new Dictionary<string, bool>())
        ];

        Assert.True(LaunchArgumentBuilder.IsAllowed(rules, Windows, NoFeatures));
        Assert.False(LaunchArgumentBuilder.IsAllowed(rules, Mac, NoFeatures));
    }
}
