namespace Launcher.Core.Skins;

/// <summary>Определяет по пикселям скина, тонкие ли у него руки (Alex).</summary>
public static class SkinDetect
{
    // Участки, которые у скина с тонкими руками остаются пустыми: правая и левая руки, основной слой и второй (как у skinview3d).
    private static readonly (int X, int Y, int W, int H)[] Areas =
    [
        (50, 16, 2, 4), (54, 20, 2, 12), (42, 48, 2, 4), (46, 52, 2, 12)
    ];

    /// <summary>
    /// <paramref name="bgra"/> — пиксели по 4 байта (B, G, R, A). Для скина не 64×64 (в том числе старого 64×32) точно сказать нельзя — возвращается <paramref name="fallback"/>.
    /// Скин с тонкими руками — когда эти участки полностью прозрачны (или целиком чёрные).
    /// </summary>
    public static bool IsSlim(byte[] bgra, int width, int height, bool fallback)
    {
        if (width != 64 || height != 64 || bgra.Length < width * height * 4) return fallback;
        return AllMatch(bgra, width, p => bgra[p + 3] == 0) || AllMatch(bgra, width, p => bgra[p + 3] == 255 && bgra[p] == 0 && bgra[p + 1] == 0 && bgra[p + 2] == 0);
    }

    private static bool AllMatch(byte[] bgra, int width, Func<int, bool> isEmpty)
    {
        foreach (var (x0, y0, w, h) in Areas)
            for (var y = y0; y < y0 + h; y++)
                for (var x = x0; x < x0 + w; x++)
                    if (!isEmpty((y * width + x) * 4)) return false;
        return true;
    }
}
