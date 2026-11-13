using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Core.Skins;

namespace Launcher.UI.Services;

/// <summary>Картинки из скинов: вид спереди, голова, увеличение без размытия. Все результаты заморожены (можно использовать из любого потока интерфейса).</summary>
internal static class SkinImages
{
    public static BitmapSource? Decode(byte[]? png)
    {
        if (png is null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static BitmapSource DefaultSkin()
    {
        // скин по умолчанию — картинка из ресурсов лаунчера; нарисованный в коде остаётся запасным
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/Resources/DefaultSkin.png", UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UriFormatException or InvalidOperationException)
        {
            var source = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, PlayerModel.DefaultSkinBgra(), 64 * 4);
            source.Freeze();
            return source;
        }
    }

    /// <summary>
    /// Целочисленное увеличение «по пикселям» для 3D-текстуры: WPF в 3D сглаживает текстуры, поэтому крупные квадраты делаем сами.
    /// Формат с предумноженной прозрачностью: иначе на границе прозрачных и цветных пикселей появлялась светлая кайма.
    /// </summary>
    public static BitmapSource Upscale(BitmapSource source, int maxSide = 2048)
    {
        var factor = Math.Clamp(maxSide / Math.Max(source.PixelWidth, source.PixelHeight), 1, 16);
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var (w, h) = (converted.PixelWidth, converted.PixelHeight);
        var src = new byte[w * h * 4];
        converted.CopyPixels(src, w * 4, 0);
        return Replicate(src, w, h, factor, PixelFormats.Pbgra32);
    }

    /// <summary>Персонаж спереди (16×32 пикселя скина, увеличено в <paramref name="scale"/> раз). Картинка собирается по пикселям, без сглаживания.</summary>
    public static BitmapSource Front(byte[]? png, bool slim, int scale = 10)
    {
        var (skin, sw, sh) = Pixels(Decode(png) ?? DefaultSkin());
        var dest = new byte[Skin2D.Width * Skin2D.Height * 4];
        foreach (var part in Skin2D.FrontParts(slim, sh == 32))
            Draw(dest, Skin2D.Width, Skin2D.Height, skin, sw, sh, part.Source, part.X, part.Y, part.Width, part.Height, part.Mirror);
        return Replicate(dest, Skin2D.Width, Skin2D.Height, scale, PixelFormats.Bgra32);
    }

    /// <summary>Лицо с шапкой (8×8 пикселей, увеличено в <paramref name="scale"/> раз) — для значка аккаунта.</summary>
    public static BitmapSource Head(byte[]? png, int scale = 16)
    {
        var (skin, sw, sh) = Pixels(Decode(png) ?? DefaultSkin());
        var dest = new byte[8 * 8 * 4];
        foreach (var source in Skin2D.HeadSources)
            Draw(dest, 8, 8, skin, sw, sh, source, 0, 0, 8, 8, false);
        return Replicate(dest, 8, 8, scale, PixelFormats.Bgra32);
    }

    /// <summary>
    /// Старый скин 64×32 на модели, рассчитанной на 64×64 (например, шаблон из лаунчера), кладётся в верхнюю половину: иначе координаты UV
    /// съезжают вдвое. Если размеры уже подходят или не кратны — картинка возвращается без изменений.
    /// </summary>
    public static BitmapSource PadTo(BitmapSource source, int modelWidth, int modelHeight)
    {
        var (w, h) = (source.PixelWidth, source.PixelHeight);
        if (modelWidth <= 0 || modelHeight <= 0 || w * modelHeight == h * modelWidth) return source; // пропорции уже подходят
        if (w * modelHeight != 2 * h * modelWidth) return source; // дополняем только «вдвое ниже, чем надо»
        var (pixels, _, _) = Pixels(source);
        var padded = new byte[w * h * 2 * 4]; // нижняя половина прозрачная
        Buffer.BlockCopy(pixels, 0, padded, 0, pixels.Length);
        var result = BitmapSource.Create(w, h * 2, 96, 96, PixelFormats.Bgra32, null, padded, w * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Тонкие ли руки у скина (определяется по пикселям); если определить нельзя — <paramref name="fallback"/>.</summary>
    public static bool IsSlim(byte[]? png, bool fallback)
    {
        if (Decode(png) is not { } image) return fallback;
        var (pixels, w, h) = Pixels(image);
        return SkinDetect.IsSlim(pixels, w, h, fallback);
    }

    /// <summary>Пиксели картинки по 4 байта (B, G, R, A).</summary>
    public static (byte[] Pixels, int Width, int Height) ToBgra(BitmapSource source) => Pixels(source);

    private static (byte[] Pixels, int Width, int Height) Pixels(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var data = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(data, bgra.PixelWidth * 4, 0);
        return (data, bgra.PixelWidth, bgra.PixelHeight);
    }

    /// <summary>Рисует кусок скина на картинку 1:1 (прозрачность накладывается как в Minecraft: второй слой поверх первого).</summary>
    private static void Draw(byte[] dest, int dw, int dh, byte[] skin, int sw, int sh, UvRect r, int dx, int dy, int w, int h, bool mirror)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var sx = (int)r.X1 + (mirror ? w - 1 - x : x);
                var sy = (int)r.Y1 + y;
                var (tx, ty) = (dx + x, dy + y);
                if (sx < 0 || sy < 0 || sx >= sw || sy >= sh || tx < 0 || ty < 0 || tx >= dw || ty >= dh) continue;
                var si = (sy * sw + sx) * 4;
                var di = (ty * dw + tx) * 4;
                int sa = skin[si + 3];
                if (sa == 0) continue;
                if (sa == 255 || dest[di + 3] == 0)
                {
                    Buffer.BlockCopy(skin, si, dest, di, 4);
                    continue;
                }
                int da = dest[di + 3];
                var outA = sa + da * (255 - sa) / 255;
                for (var k = 0; k < 3; k++)
                    dest[di + k] = (byte)((skin[si + k] * sa + dest[di + k] * da * (255 - sa) / 255) / Math.Max(1, outA));
                dest[di + 3] = (byte)outA;
            }
    }

    private static BitmapSource Replicate(byte[] src, int w, int h, int factor, PixelFormat format)
    {
        var (nw, nh) = (w * factor, h * factor);
        var dst = new byte[nw * nh * 4];
        for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
                Buffer.BlockCopy(src, ((y / factor) * w + x / factor) * 4, dst, (y * nw + x) * 4, 4);
        var result = BitmapSource.Create(nw, nh, 96, 96, format, null, dst, nw * 4);
        result.Freeze();
        return result;
    }
}
