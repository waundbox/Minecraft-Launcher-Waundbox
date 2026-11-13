using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core.Skins;

/// <summary>
/// Собирает ресурспак, который подменяет стандартные скины игроков (Steve, Alex и остальные) картинкой пользователя.
/// В одиночной игре и в режиме разработки у игрока нет скина от Mojang, поэтому игра берёт один из стандартных —
/// а ресурспак заменяет их все. Другие игроки на сервере такой скин не увидят.
/// </summary>
public static class SkinPackBuilder
{
    public const string PackFileName = "WaundboxSkin.zip";

    private static readonly string[] DefaultSkinNames =
        { "alex", "ari", "efe", "kai", "makena", "noor", "steve", "sunny", "zuri" };

    /// <summary>Пути текстур, которые нужно заменить (старые версии и версии 1.19.3+).</summary>
    public static IReadOnlyList<string> TexturePaths { get; } = BuildPaths();

    private static List<string> BuildPaths()
    {
        var paths = new List<string>
        {
            "assets/minecraft/textures/entity/steve.png",
            "assets/minecraft/textures/entity/alex.png"
        };
        foreach (var model in new[] { "wide", "slim" })
            foreach (var name in DefaultSkinNames)
                paths.Add($"assets/minecraft/textures/entity/player/{model}/{name}.png");
        return paths;
    }

    public static byte[] Build(byte[] skinPng, int packFormat)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "pack.mcmeta",
                Encoding.UTF8.GetBytes($"{{\"pack\":{{\"pack_format\":{packFormat},\"description\":\"Waundbox skin\"}}}}"));
            foreach (var path in TexturePaths) WriteEntry(zip, path, skinPng);
        }
        return memory.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }
}

/// <summary>Номер формата ресурспака для версии игры (по справочнику minecraft.wiki; для более новых версий — последний известный).</summary>
public static class ResourcePackFormat
{
    public const int LatestKnown = 46;

    public static int For(string gameVersion)
    {
        if (!TryParse(gameVersion, out var minor, out var patch)) return LatestKnown;

        return (minor, patch) switch
        {
            (<= 14, _) => 4,
            (15, _) => 5,
            (16, 0 or 1) => 5,
            (16, _) => 6,
            (17, _) => 7,
            (18, _) => 8,
            (19, <= 2) => 9,
            (19, 3) => 12,
            (19, _) => 13,
            (20, <= 1) => 15,
            (20, 2) => 18,
            (20, 3 or 4) => 22,
            (20, _) => 32,
            (21, <= 1) => 34,
            (21, 2 or 3) => 42,
            _ => LatestKnown
        };
    }

    /// <summary>Подмена скина рассчитана на версии 1.13 и новее (в старых другой формат options.txt и ресурсов).</summary>
    public static bool IsSupported(string gameVersion)
    {
        if (Regex.IsMatch(gameVersion, @"^\d{2}w\d{2}[a-z]$"))
            return int.Parse(gameVersion[..2]) >= 18;
        return !TryParse(gameVersion, out var minor, out _) || minor >= 13;
    }

    private static bool TryParse(string version, out int minor, out int patch)
    {
        minor = 0;
        patch = 0;
        var parts = version.Split('.', '-');
        if (parts.Length < 2 || parts[0] != "1" || !int.TryParse(parts[1], out minor)) return false;
        if (parts.Length >= 3) int.TryParse(parts[2], out patch);
        return true;
    }
}
