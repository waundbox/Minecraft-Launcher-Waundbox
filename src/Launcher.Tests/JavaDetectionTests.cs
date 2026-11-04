using Launcher.Core.Errors;
using Launcher.Core.JavaRuntime;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class JavaProbeOutputParserTests
{
    [Theory]
    [InlineData("1.8.0_392", 8)]
    [InlineData("1.7.0_80", 7)]
    [InlineData("17.0.9", 17)]
    [InlineData("21", 21)]
    [InlineData("21-ea", 21)]
    [InlineData("9.0.4", 9)]
    public void ParseMajor_HandlesOldAndNewSchemes(string version, int expected) =>
        Assert.Equal(expected, JavaProbeOutputParser.ParseMajor(version));

    [Fact]
    public void Parse_ReadsVersionVendorAndBits_IgnoringContinuationLines()
    {
        var info = JavaProbeOutputParser.Parse(JavaSamples.Output("21.0.4"));

        Assert.NotNull(info);
        Assert.Equal(21, info!.Major);
        Assert.Equal("21.0.4", info.Version);
        Assert.Equal("Eclipse Adoptium", info.Vendor);
        Assert.True(info.Is64Bit);
    }

    [Fact]
    public void Parse_Detects32Bit() =>
        Assert.False(JavaProbeOutputParser.Parse(JavaSamples.Output("1.8.0_392", bits: "32"))!.Is64Bit);

    [Fact]
    public void Parse_FallsBackToVersionLine()
    {
        var info = JavaProbeOutputParser.Parse("openjdk version \"17.0.9\" 2023-10-17\nOpenJDK 64-Bit Server VM");

        Assert.Equal(17, info!.Major);
        Assert.True(info.Is64Bit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("'java' is not recognized as an internal or external command")]
    public void Parse_NotJava_ReturnsNull(string output) =>
        Assert.Null(JavaProbeOutputParser.Parse(output));
}

public class JavaSelectorTests
{
    [Fact]
    public void ExactMatch_BeatsNewerOne_EvenIfListedLater()
    {
        var result = JavaSelector.Select([JavaSamples.Make(21), JavaSamples.Make(17)], 17);

        Assert.Equal(17, result!.Installation.MajorVersion);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Prefers64Bit_ForSameMajor()
    {
        var result = JavaSelector.Select([JavaSamples.Make(17, is64: false), JavaSamples.Make(17)], 17);

        Assert.True(result!.Installation.Is64Bit);
    }

    [Fact]
    public void NoExact_TakesClosestNewer_WithWarning()
    {
        var result = JavaSelector.Select([JavaSamples.Make(21), JavaSamples.Make(17)], 8);

        Assert.Equal(17, result!.Installation.MajorVersion);
        Assert.Contains("Рекомендуется Java 8", result.Warning);
    }

    [Fact]
    public void TooOld_IsRejected() =>
        Assert.Null(JavaSelector.Select([JavaSamples.Make(8), JavaSamples.Make(11)], 17));

    [Fact]
    public void Empty_ReturnsNull() =>
        Assert.Null(JavaSelector.Select([], 8));

    [Fact]
    public void SameQuality_KeepsDiscoveryOrder()
    {
        var first = JavaSamples.Make(21, path: "/first/java");
        var second = JavaSamples.Make(21, path: "/second/java");

        Assert.Equal("/first/java", JavaSelector.Select([first, second], 21)!.Installation.Path);
    }
}

public class JavaProbeTests
{
    [Fact]
    public async Task Probe_ReturnsInstallation_AndAsksForVersion()
    {
        var runner = new FakeProcessRunner(_ => JavaSamples.Ok("21.0.4"));

        var java = await new JavaProbe(runner).ProbeAsync("/x/java", JavaSource.Path, default);

        Assert.Equal(21, java!.MajorVersion);
        Assert.Equal(JavaSource.Path, java.Source);
        Assert.Contains("-version", runner.Calls.Single().Args);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task Probe_FailureCases_ReturnNull(int exitCode, bool timedOut)
    {
        var runner = new FakeProcessRunner(_ => new ProcessResult(exitCode, "", "", timedOut));

        Assert.Null(await new JavaProbe(runner).ProbeAsync("/x/java", JavaSource.Path, default));
    }

    [Fact]
    public async Task Probe_NotJavaOutput_ReturnsNull()
    {
        var runner = new FakeProcessRunner(_ => new ProcessResult(0, "hello", "", false));

        Assert.Null(await new JavaProbe(runner).ProbeAsync("/x/tool", JavaSource.Path, default));
    }
}

public class JavaResolverTests
{
    private static JavaResolver Create(Func<string, ProcessResult> runner, params JavaInstallation[] detected) =>
        new(new FakeJavaDetector(detected), new JavaProbe(new FakeProcessRunner(runner)));

    private static ProcessResult Broken(string _) => new(-1, "", "not found", false);

    [Fact]
    public async Task ConfiguredPath_IsUsed_WithoutDetection()
    {
        var resolver = Create(_ => JavaSamples.Ok("21.0.4"), JavaSamples.Make(17));

        var result = await resolver.ResolveAsync(21, "/my/java", default);

        Assert.Equal("/my/java", result.Installation.Path);
        Assert.Equal(JavaSource.Configured, result.Installation.Source);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task ConfiguredPath_NewerThanRequired_GivesWarning()
    {
        var result = await Create(_ => JavaSamples.Ok("21.0.4")).ResolveAsync(17, "/my/java", default);

        Assert.NotNull(result.Warning);
    }

    [Fact]
    public async Task ConfiguredPath_Broken_ExplainsHowToFix()
    {
        var ex = await Assert.ThrowsAsync<JavaNotFoundException>(
            () => Create(Broken, JavaSamples.Make(21)).ResolveAsync(21, "/my/java", default));

        Assert.Contains("java set", ex.Message);
        Assert.Contains("java clear", ex.Message);
    }

    [Fact]
    public async Task ConfiguredPath_TooOld_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<JavaNotFoundException>(
            () => Create(_ => JavaSamples.Ok("1.8.0_392")).ResolveAsync(21, "/my/java", default));

        Assert.Contains("слишком старая", ex.Message);
    }

    [Fact]
    public async Task AutoDetect_PicksSuitableJava()
    {
        var result = await Create(Broken, JavaSamples.Make(8), JavaSamples.Make(21)).ResolveAsync(21, null, default);

        Assert.Equal(21, result.Installation.MajorVersion);
    }

    [Fact]
    public async Task NothingFound_MessageSaysWhatToInstall()
    {
        var ex = await Assert.ThrowsAsync<JavaNotFoundException>(() => Create(Broken).ResolveAsync(21, null, default));

        Assert.Contains("Java 21", ex.Message);
        Assert.Contains("adoptium", ex.Message);
        Assert.Contains("не найдена", ex.Message);
    }

    [Fact]
    public async Task OnlyOldFound_MessageListsThem()
    {
        var ex = await Assert.ThrowsAsync<JavaNotFoundException>(
            () => Create(Broken, JavaSamples.Make(8)).ResolveAsync(21, null, default));

        Assert.Contains("Java 8", ex.Message);
        Assert.Contains("старые", ex.Message);
    }
}
