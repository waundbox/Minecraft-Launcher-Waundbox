using Launcher.Core.Assets;
using Launcher.Core.Errors;
using Launcher.Core.Libraries;
using Launcher.Core.Versions;

namespace Launcher.Tests;

public class VersionDetailsParserTests
{
    private static VersionDetails Parsed() => VersionDetailsParser.Parse(VersionTestData.VersionJson);

    [Fact]
    public void Parse_ReadsMainFields()
    {
        var d = Parsed();

        Assert.Equal("1.21.1", d.Id);
        Assert.Equal("net.minecraft.client.main.Main", d.MainClass);
        Assert.Equal(21, d.JavaMajorVersion);
        Assert.Equal("17", d.AssetIndex.Id);
        Assert.Equal(5000, d.ClientDownload.Size);
        Assert.Equal(4, d.Libraries.Count);
    }

    [Fact]
    public void Parse_ReadsLegacyNativesAndRules()
    {
        var platform = Parsed().Libraries.Single(l => l.Name.Contains("lwjgl-platform"));
        Assert.Equal("natives-windows", platform.Natives["windows"]);
        Assert.Contains("META-INF/", platform.ExtractExclude);
        Assert.Equal(2, platform.Classifiers.Count);

        var linuxOnly = Parsed().Libraries.Single(l => l.Name.EndsWith("natives-linux"));
        Assert.Equal("linux", linuxOnly.Rules.Single().OsName);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Parse_Invalid_Throws(string json) =>
        Assert.Throws<VersionMetadataException>(() => VersionDetailsParser.Parse(json));

    [Fact]
    public void Parse_LibraryWithoutDownloads_ThrowsClearError()
    {
        var json = VersionTestData.VersionJson.Replace("\"libraries\": [", "\"libraries\": [ { \"name\": \"x:y:1\" },");
        var ex = Assert.Throws<VersionMetadataException>(() => VersionDetailsParser.Parse(json));
        Assert.Contains("x:y:1", ex.Message);
    }
}

public class LibraryResolverTests
{
    private static readonly PlatformInfo Windows = new("windows", "x86_64", "10.0.22631.0");
    private static readonly PlatformInfo Linux = new("linux", "x86_64", null);
    private static readonly PlatformInfo Mac = new("osx", "arm64", null);

    private static IReadOnlyList<ResolvedLibrary> Resolve(PlatformInfo p) =>
        LibraryResolver.Resolve(VersionDetailsParser.Parse(VersionTestData.VersionJson).Libraries, p);

    [Fact]
    public void Windows_PicksWindowsNativesOnly()
    {
        var libs = Resolve(Windows);

        Assert.Equal(3, libs.Count);
        Assert.Contains(libs, l => l.Name.EndsWith("natives-windows"));
        Assert.DoesNotContain(libs, l => l.Name.EndsWith("natives-linux"));
        var platform = libs.Single(l => l.Name.Contains("lwjgl-platform"));
        Assert.EndsWith("natives-windows.jar", platform.NativeArtifact!.Path);
    }

    [Fact]
    public void Linux_PicksLinuxNatives()
    {
        var libs = Resolve(Linux);

        Assert.Contains(libs, l => l.Name.EndsWith("natives-linux"));
        Assert.DoesNotContain(libs, l => l.Name.EndsWith("natives-windows"));
        Assert.EndsWith("natives-linux.jar", libs.Single(l => l.Name.Contains("lwjgl-platform")).NativeArtifact!.Path);
    }

    [Fact]
    public void Mac_HasNoNativesForLegacyLibrary()
    {
        var libs = Resolve(Mac);

        Assert.Equal(2, libs.Count); // logging + lwjgl-platform (без natives: для osx их нет в метаданных)
        Assert.Null(libs.Single(l => l.Name.Contains("lwjgl-platform")).NativeArtifact);
    }

    [Fact]
    public void Rules_NoRules_Allowed() =>
        Assert.True(LibraryResolver.IsAllowed([], Windows));

    [Fact]
    public void Rules_AllowEverythingExceptOsx()
    {
        LibraryRule[] rules =
        [
            new(true, null, null, null, false),
            new(false, "osx", null, null, false)
        ];

        Assert.True(LibraryResolver.IsAllowed(rules, Windows));
        Assert.False(LibraryResolver.IsAllowed(rules, Mac));
    }

    [Fact]
    public void Rules_ArchMismatch_DoesNotMatch() =>
        Assert.False(LibraryResolver.IsAllowed([new(true, "windows", "x86", null, false)], Windows));

    [Fact]
    public void Rules_FeatureRules_AreIgnoredForLibraries() =>
        Assert.False(LibraryResolver.IsAllowed([new(true, null, null, null, true)], Windows));

    [Fact]
    public void Rules_OsVersionRegex_IsApplied()
    {
        LibraryRule[] rules = [new(true, "windows", null, @"^10\.", false)];
        Assert.True(LibraryResolver.IsAllowed(rules, Windows));
        Assert.False(LibraryResolver.IsAllowed(rules, Windows with { OsVersion = "6.1.7601" }));
    }
}

public class AssetIndexParserTests
{
    [Fact]
    public void Parse_ReadsObjects()
    {
        var objects = AssetIndexParser.Parse(VersionTestData.AssetIndexJson);

        Assert.Equal(3, objects.Count);
        Assert.Equal(3665, objects.Single(o => o.Name == "icons/copy.png").Size);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("nope")]
    [InlineData("{\"objects\": { \"a\": { \"hash\": \"zz\", \"size\": 1 } } }")]
    [InlineData("{\"objects\": { \"a\": { \"hash\": \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" } } }")]
    public void Parse_Invalid_Throws(string json) =>
        Assert.Throws<VersionMetadataException>(() => AssetIndexParser.Parse(json));
}
