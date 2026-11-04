using System.IO.Compression;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Skins;
using Launcher.Tests.Support;

namespace Launcher.Tests;

public class SkinTests : TempDirectoryTest
{
    /// <summary>Минимальный «PNG»: подпись и заголовок IHDR с нужными размерами (содержимое картинки лаунчеру не нужно).</summary>
    private static byte[] FakePng(int width, int height)
    {
        var data = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        data[11] = 13;
        "IHDR"u8.CopyTo(data.AsSpan(12));
        for (var i = 0; i < 4; i++)
        {
            data[16 + i] = (byte)(width >> (24 - 8 * i));
            data[20 + i] = (byte)(height >> (24 - 8 * i));
        }
        data[24] = 8;
        data[25] = 6;
        return data;
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(64, 32)]
    public void Validate_AcceptsStandardSizes(int width, int height) => SkinImage.Validate(FakePng(width, height));

    [Theory]
    [InlineData(128, 128)]
    [InlineData(64, 48)]
    [InlineData(32, 32)]
    public void Validate_RejectsOtherSizes(int width, int height) =>
        Assert.Throws<LauncherException>(() => SkinImage.Validate(FakePng(width, height)));

    [Fact]
    public void Validate_RejectsNonPngAndTooSmallAndHuge()
    {
        Assert.Throws<LauncherException>(() => SkinImage.Validate("hello world, definitely not a png file"u8.ToArray()));
        Assert.Throws<LauncherException>(() => SkinImage.Validate(new byte[5]));
        Assert.Throws<LauncherException>(() => SkinImage.Validate(new byte[SkinImage.MaxBytes + 1]));
    }

    [Theory]
    [InlineData("1.13", 4)]
    [InlineData("1.16.5", 6)]
    [InlineData("1.19.3", 12)]
    [InlineData("1.20.1", 15)]
    [InlineData("1.20.4", 22)]
    [InlineData("1.21.1", 34)]
    [InlineData("1.21.4", 46)]
    [InlineData("26w01a", ResourcePackFormat.LatestKnown)]
    public void PackFormat_ByVersion(string version, int expected) =>
        Assert.Equal(expected, ResourcePackFormat.For(version));

    [Theory]
    [InlineData("1.21.1", true)]
    [InlineData("1.13", true)]
    [InlineData("1.12.2", false)]
    [InlineData("1.8.9", false)]
    [InlineData("24w14a", true)]
    [InlineData("17w43a", false)]
    public void IsSupported_ByVersion(string version, bool expected) =>
        Assert.Equal(expected, ResourcePackFormat.IsSupported(version));

    [Fact]
    public void PackBuilder_ContainsMetaAndAllTextures()
    {
        var png = FakePng(64, 64);
        var zip = SkinPackBuilder.Build(png, 34);

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("pack.mcmeta", names);
        Assert.Contains("assets/minecraft/textures/entity/steve.png", names);
        Assert.Contains("assets/minecraft/textures/entity/player/wide/steve.png", names);
        Assert.Contains("assets/minecraft/textures/entity/player/slim/alex.png", names);
        Assert.Equal(1 + SkinPackBuilder.TexturePaths.Count, names.Count);

        using var meta = new StreamReader(archive.GetEntry("pack.mcmeta")!.Open());
        Assert.Contains("\"pack_format\":34", meta.ReadToEnd());

        using var texture = archive.GetEntry("assets/minecraft/textures/entity/player/wide/steve.png")!.Open();
        using var copy = new MemoryStream();
        texture.CopyTo(copy);
        Assert.Equal(png, copy.ToArray());
    }

    private const string Entry = "file/WaundboxSkin.zip";

    [Fact]
    public void Options_EnableOnEmptyOrMissingLine()
    {
        Assert.True(OptionsFile.TryEnable(null, Entry, out var fromNothing));
        Assert.Equal("resourcePacks:[\"vanilla\",\"file/WaundboxSkin.zip\"]\n", fromNothing);

        Assert.True(OptionsFile.TryEnable("lang:ru_ru\nfov:0.5\n", Entry, out var added));
        Assert.Equal("lang:ru_ru\nfov:0.5\nresourcePacks:[\"vanilla\",\"file/WaundboxSkin.zip\"]\n", added);
    }

    [Fact]
    public void Options_EnableAppendsToExistingListAndKeepsOtherLines()
    {
        const string text = "lang:ru_ru\r\nresourcePacks:[\"vanilla\",\"fabric\"]\r\nfov:0.5\r\n";
        Assert.True(OptionsFile.TryEnable(text, Entry, out var result));
        Assert.Equal("lang:ru_ru\r\nresourcePacks:[\"vanilla\",\"fabric\",\"file/WaundboxSkin.zip\"]\r\nfov:0.5\r\n", result);

        // повторное включение ничего не меняет
        Assert.True(OptionsFile.TryEnable(result, Entry, out var again));
        Assert.Equal(result, again);
    }

    [Fact]
    public void Options_DisableRemovesOnlyOurPack()
    {
        const string text = "a:1\nresourcePacks:[\"vanilla\",\"file/other.zip\",\"file/WaundboxSkin.zip\"]\nb:2\n";
        Assert.True(OptionsFile.TryDisable(text, Entry, out var result));
        Assert.Equal("a:1\nresourcePacks:[\"vanilla\",\"file/other.zip\"]\nb:2\n", result);

        Assert.True(OptionsFile.TryDisable("a:1\n", Entry, out var untouched));
        Assert.Equal("a:1\n", untouched);
    }

    [Fact]
    public void Options_BrokenLineIsNotTouched()
    {
        const string text = "resourcePacks:[oops\n";
        Assert.False(OptionsFile.TryEnable(text, Entry, out var result));
        Assert.Equal(text, result);
        Assert.False(OptionsFile.TryDisable(text, Entry, out _));
    }

    private (SkinService Service, MinecraftLayout Layout) CreateService()
    {
        var layout = new MinecraftLayout(Path.Combine(Dir, "game"));
        return (new SkinService(layout, Path.Combine(Dir, "skin.png")), layout);
    }

    [Fact]
    public async Task Service_SetAndApplyWritesPackAndOptions_ThenRemoves()
    {
        var (service, layout) = CreateService();
        Assert.False(service.HasSkin);

        await service.SetSkinAsync(FakePng(64, 64), CancellationToken.None);
        Assert.True(service.HasSkin);

        var result = await service.ApplyAsync("Main", "1.21.1", enable: true, CancellationToken.None);
        Assert.Equal(SkinApplyResult.Applied, result);

        var instance = layout.InstanceDirectory("Main");
        Assert.True(File.Exists(Path.Combine(instance, "resourcepacks", "WaundboxSkin.zip")));
        Assert.Contains("file/WaundboxSkin.zip", await File.ReadAllTextAsync(Path.Combine(instance, "options.txt")));

        var removed = await service.ApplyAsync("Main", "1.21.1", enable: false, CancellationToken.None);
        Assert.Equal(SkinApplyResult.Removed, removed);
        Assert.False(File.Exists(Path.Combine(instance, "resourcepacks", "WaundboxSkin.zip")));
        Assert.DoesNotContain("WaundboxSkin", await File.ReadAllTextAsync(Path.Combine(instance, "options.txt")));
    }

    [Fact]
    public async Task Service_OldVersionIsUnsupported_AndInvalidFileIsRejected()
    {
        var (service, _) = CreateService();
        await Assert.ThrowsAsync<LauncherException>(() => service.SetSkinAsync(new byte[10], CancellationToken.None));
        Assert.False(service.HasSkin);

        await service.SetSkinAsync(FakePng(64, 32), CancellationToken.None);
        Assert.Equal(SkinApplyResult.Unsupported, await service.ApplyAsync("Old", "1.8.9", true, CancellationToken.None));

        service.RemoveSkin();
        Assert.False(service.HasSkin);
    }
}
