using System.IO.Compression;
using Launcher.Core.Errors;
using Launcher.Core.Libraries;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class NativesExtractorTests : TempDirectoryTest
{
    private string MakeZip(params string[] entryNames)
    {
        var path = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in entryNames)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("content");
        }
        return path;
    }

    [Fact]
    public void Extract_SkipsExcludedPrefixes()
    {
        var zip = MakeZip("lwjgl.dll", "sub/other.dll", "META-INF/MANIFEST.MF");
        var target = Path.Combine(Dir, "natives");

        NativesExtractor.Extract(zip, target, ["META-INF/"]);

        Assert.True(File.Exists(Path.Combine(target, "lwjgl.dll")));
        Assert.True(File.Exists(Path.Combine(target, "sub", "other.dll")));
        Assert.False(Directory.Exists(Path.Combine(target, "META-INF")));
    }

    [Fact]
    public void Extract_RejectsPathTraversal()
    {
        var zip = MakeZip("../evil.txt");
        var target = Path.Combine(Dir, "natives");

        Assert.Throws<LauncherException>(() => NativesExtractor.Extract(zip, target, []));
        Assert.False(File.Exists(Path.Combine(Dir, "evil.txt")));
    }
}
