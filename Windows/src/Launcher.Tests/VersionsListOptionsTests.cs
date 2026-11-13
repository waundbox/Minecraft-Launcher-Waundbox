using Launcher.Cli.Commands;
using Launcher.Core.Versions;

namespace Launcher.Tests;

public class VersionsListOptionsTests
{
    [Fact]
    public void Defaults_AreReleasesLimit20()
    {
        var o = VersionsListOptions.TryParse([], out var error);

        Assert.Null(error);
        Assert.Equal(VersionType.Release, o!.Type);
        Assert.Equal(20, o.Limit);
        Assert.False(o.Refresh);
    }

    [Fact]
    public void ParsesAllOptions()
    {
        var o = VersionsListOptions.TryParse(["--type", "all", "--limit", "0", "--refresh"], out _);

        Assert.Null(o!.Type);
        Assert.Equal(0, o.Limit);
        Assert.True(o.Refresh);
    }

    [Theory]
    [InlineData("--type", "bogus")]
    [InlineData("--limit", "abc")]
    [InlineData("--limit", "-5")]
    [InlineData("--wat")]
    [InlineData("--type")]
    public void InvalidArguments_ReturnNullWithError(params string[] args)
    {
        var o = VersionsListOptions.TryParse(args, out var error);

        Assert.Null(o);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task Command_PrintsRequestedVersionsOnly()
    {
        var manifest = VersionManifestParser.Parse(TestData.ManifestJson);
        var provider = new FakeProvider(new ManifestResult(manifest, ManifestSource.Network, DateTimeOffset.UtcNow));
        var writer = new StringWriter();

        var code = await new VersionsListCommand(provider, writer)
            .RunAsync(new VersionsListOptions(VersionType.Release, 20, false), CancellationToken.None);

        var text = writer.ToString();
        Assert.Equal(0, code);
        Assert.Contains("1.21.1", text);
        Assert.DoesNotContain("b1.7.3", text);
    }

    private sealed class FakeProvider(ManifestResult result) : IVersionManifestProvider
    {
        public Task<ManifestResult> GetManifestAsync(bool forceRefresh, CancellationToken ct) => Task.FromResult(result);
    }
}
