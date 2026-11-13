using Launcher.Core.Errors;
using Launcher.Core.Skins;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class SkinLibraryTests : TempDirectoryTest
{
    private static byte[] Png(int w = 64, int h = 64)
    {
        var data = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(data, 0);
        data[19] = (byte)w; data[23] = (byte)h;
        return data;
    }

    private SkinLibrary Create() => new(Path.Combine(Dir, "skins"));

    [Fact]
    public async Task AddListReadRemove_RoundTrip()
    {
        var lib = Create();
        var entry = await lib.AddAsync("  Мой скин ", Png(), slim: true, default);

        var list = await lib.ListAsync(default);
        Assert.Equal(entry, Assert.Single(list));
        Assert.Equal("Мой скин", entry.Name);
        Assert.True(entry.Slim);
        Assert.Equal(Png(), await lib.ReadPngAsync(entry.Id, default));

        await lib.RemoveAsync(entry.Id, default);
        Assert.Empty(await lib.ListAsync(default));
        Assert.Null(await lib.ReadPngAsync(entry.Id, default));
    }

    [Fact]
    public async Task Add_RejectsNonSkinImages() =>
        await Assert.ThrowsAsync<LauncherException>(() => Create().AddAsync("x", new byte[] { 1, 2, 3 }, false, default));

    [Fact]
    public async Task UnsafeIds_AreIgnored()
    {
        var lib = Create();
        await lib.AddAsync("a", Png(), false, default);
        Assert.Null(await lib.ReadPngAsync("../../secret", default));
        await lib.RemoveAsync("../library", default); // не должно ничего удалить и не должно падать
        Assert.Single(await lib.ListAsync(default));
    }

    [Fact]
    public async Task BrokenIndex_StartsEmpty_AndMissingFilesAreDropped()
    {
        var lib = Create();
        var entry = await lib.AddAsync("a", Png(), false, default);
        File.Delete(Path.Combine(Dir, "skins", entry.Id + ".png"));
        Assert.Empty(await lib.ListAsync(default));

        File.WriteAllText(Path.Combine(Dir, "skins", "library.json"), "not json");
        Assert.Empty(await lib.ListAsync(default));
    }

    [Theory]
    [InlineData(null, "Скин")]
    [InlineData("   ", "Скин")]
    [InlineData("a\nb", "ab")]
    public void CleanName_Normalises(string? input, string expected) => Assert.Equal(expected, SkinLibrary.CleanName(input));

    [Fact]
    public void CleanName_LimitsLength() => Assert.Equal(32, SkinLibrary.CleanName(new string('я', 80)).Length);

    [Fact]
    public void SkinDetect_TransparentArmColumns_MeansSlim()
    {
        var opaque = new byte[64 * 64 * 4];
        for (var i = 0; i < opaque.Length; i += 4) { opaque[i] = 40; opaque[i + 1] = 60; opaque[i + 2] = 80; opaque[i + 3] = 255; }
        Assert.False(SkinDetect.IsSlim(opaque, 64, 64, fallback: true));

        var slim = (byte[])opaque.Clone();
        foreach (var (x0, y0, w, h) in new[] { (50, 16, 2, 4), (54, 20, 2, 12), (42, 48, 2, 4), (46, 52, 2, 12) })
            for (var y = y0; y < y0 + h; y++)
                for (var x = x0; x < x0 + w; x++)
                    slim[(y * 64 + x) * 4 + 3] = 0;
        Assert.True(SkinDetect.IsSlim(slim, 64, 64, fallback: false));
    }

    [Fact]
    public void SkinDetect_OtherSizes_UseFallback()
    {
        Assert.True(SkinDetect.IsSlim(new byte[64 * 32 * 4], 64, 32, fallback: true));
        Assert.False(SkinDetect.IsSlim(new byte[128 * 128 * 4], 128, 128, fallback: false));
    }
}
