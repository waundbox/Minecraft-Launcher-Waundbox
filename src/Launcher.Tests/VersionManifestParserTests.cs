using Launcher.Core.Errors;
using Launcher.Core.Versions;

namespace Launcher.Tests;

public class VersionManifestParserTests
{
    [Fact]
    public void Parse_ReadsLatestAndVersions()
    {
        var m = VersionManifestParser.Parse(TestData.ManifestJson);

        Assert.Equal("1.21.1", m.LatestRelease);
        Assert.Equal("24w40a", m.LatestSnapshot);
        Assert.Equal(3, m.Versions.Count);
    }

    [Fact]
    public void Parse_DistinguishesTypes()
    {
        var m = VersionManifestParser.Parse(TestData.ManifestJson);

        Assert.Equal(VersionType.Snapshot, m.Find("24w40a")!.Type);
        Assert.Equal(VersionType.Release, m.Find("1.21.1")!.Type);
        Assert.Equal(VersionType.OldBeta, m.Find("b1.7.3")!.Type);
    }

    [Fact]
    public void Parse_KeepsUrlAndSha1FromMetadata()
    {
        var v = VersionManifestParser.Parse(TestData.ManifestJson).Find("1.21.1")!;

        Assert.Equal("https://piston-meta.mojang.com/v1/packages/bbb/1.21.1.json", v.Url.ToString());
        Assert.Equal("bbb", v.Sha1);
    }

    [Fact]
    public void Parse_UnknownType_BecomesUnknownButIsKept()
    {
        var json = TestData.ManifestJson.Replace("\"old_beta\"", "\"experimental\"");
        var v = VersionManifestParser.Parse(json).Find("b1.7.3")!;

        Assert.Equal(VersionType.Unknown, v.Type);
        Assert.Equal("experimental", v.RawType);
    }

    [Fact]
    public void OfType_FiltersAndNullReturnsAll()
    {
        var m = VersionManifestParser.Parse(TestData.ManifestJson);

        Assert.Single(m.OfType(VersionType.Release));
        Assert.Equal(3, m.OfType(null).Count());
    }

    [Fact]
    public void Find_UnknownId_ReturnsNull() =>
        Assert.Null(VersionManifestParser.Parse(TestData.ManifestJson).Find("9.9.9"));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"versions\": [ { \"id\": \"1.0\" } ] }")]
    [InlineData("{\"versions\": [ { \"id\": \"1.0\", \"type\": \"release\", \"url\": \"not a url\", \"time\": \"2020-01-01T00:00:00+00:00\", \"releaseTime\": \"2020-01-01T00:00:00+00:00\" } ] }")]
    [InlineData("{\"versions\": [ { \"id\": \"1.0\", \"type\": \"release\", \"url\": \"https://x.test/a.json\", \"time\": \"вчера\", \"releaseTime\": \"2020-01-01T00:00:00+00:00\" } ] }")]
    public void Parse_InvalidInput_ThrowsManifestFormatException(string json) =>
        Assert.Throws<ManifestFormatException>(() => VersionManifestParser.Parse(json));
}
