using Launcher.Core.Downloads;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class FileIntegrityTests : TempDirectoryTest
{
    private static readonly byte[] Data = "hello"u8.ToArray();

    private string Write()
    {
        var path = Path.Combine(Dir, "f.bin");
        File.WriteAllBytes(path, Data);
        return path;
    }

    [Fact]
    public async Task KnownGood_RequiresMatchingSha1AndSize()
    {
        var path = Write();
        Assert.True(await FileIntegrity.IsKnownGoodAsync(path, Responses.Sha1Of(Data), Data.Length, default));
        Assert.False(await FileIntegrity.IsKnownGoodAsync(path, new string('0', 40), Data.Length, default));
        Assert.False(await FileIntegrity.IsKnownGoodAsync(path, Responses.Sha1Of(Data), Data.Length + 1, default));
    }

    [Fact]
    public async Task KnownGood_WithoutAnyExpectation_IsFalse() =>
        Assert.False(await FileIntegrity.IsKnownGoodAsync(Write(), null, null, default));

    [Fact]
    public async Task MissingFile_IsNotValid() =>
        Assert.False(await FileIntegrity.MatchesAsync(Path.Combine(Dir, "nope"), null, null, default));
}

public class MinecraftLayoutTests
{
    private readonly MinecraftLayout _layout = new(Path.Combine(Path.GetTempPath(), "wb-layout"));

    [Fact]
    public void LibraryPath_StaysInsideLibrariesFolder() =>
        Assert.StartsWith(_layout.LibrariesDirectory, _layout.LibraryPath("com/mojang/logging/1.2.7/logging-1.2.7.jar"));

    [Theory]
    [InlineData("../../evil.jar")]
    [InlineData("a/../../../evil.jar")]
    public void LibraryPath_RejectsTraversal(string relative) =>
        Assert.Throws<VersionMetadataException>(() => _layout.LibraryPath(relative));

    [Fact]
    public void VersionDirectory_RejectsTraversal() =>
        Assert.Throws<VersionMetadataException>(() => _layout.VersionDirectory("../../x"));

    [Fact]
    public void AssetObjectPath_UsesTwoLetterSubfolder()
    {
        var hash = "ab" + new string('c', 38);
        Assert.EndsWith(Path.Combine("objects", "ab", hash), _layout.AssetObjectPath(hash));
    }

    [Fact]
    public void AssetObjectPath_RejectsBadHash() =>
        Assert.Throws<VersionMetadataException>(() => _layout.AssetObjectPath("../x"));
}
