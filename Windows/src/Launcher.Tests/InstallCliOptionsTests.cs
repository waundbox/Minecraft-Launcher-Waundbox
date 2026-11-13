using Launcher.Cli.Commands;

namespace Launcher.Tests;

public class InstallCliOptionsTests
{
    [Fact]
    public void Parses_VersionAndOptions()
    {
        var o = InstallCliOptions.TryParse(["1.21.1", "--refresh", "--concurrency", "4"], out var error);

        Assert.Null(error);
        Assert.Equal("1.21.1", o!.VersionId);
        Assert.True(o.Refresh);
        Assert.Equal(4, o.Concurrency);
    }

    [Fact]
    public void Defaults_AreApplied()
    {
        var o = InstallCliOptions.TryParse(["1.20.4"], out _);

        Assert.False(o!.Refresh);
        Assert.Equal(InstallCliOptions.DefaultConcurrency, o.Concurrency);
    }

    [Fact]
    public void NoArguments_ReturnError()
    {
        Assert.Null(InstallCliOptions.TryParse([], out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("1.21", "1.20")]
    [InlineData("1.21", "--concurrency", "0")]
    [InlineData("1.21", "--concurrency", "999")]
    [InlineData("1.21", "--concurrency")]
    [InlineData("1.21", "--nope")]
    public void InvalidArguments_ReturnError(params string[] args)
    {
        var o = InstallCliOptions.TryParse(args, out var error);

        Assert.Null(o);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
