using System.IO;
using System.Windows.Media.Imaging;

namespace Launcher.UI.Services;

/// <summary>Своё фоновое изображение: копия выбранной картинки лежит в папке backgrounds лаунчера, чтобы фон не пропал, если исходный файл переместят.</summary>
internal static class BackgroundImages
{
    public const long MaxBytes = 25 * 1024 * 1024;
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    public static string Folder(string root) => Path.Combine(root, "backgrounds");

    /// <summary>Полный путь к сохранённому изображению или null, если его нет.</summary>
    public static string? PathFor(string root, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)) return null;
        var path = Path.Combine(Folder(root), name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Проверяет картинку, копирует в папку лаунчера и возвращает имя нового файла.</summary>
    public static async Task<string> ImportAsync(string root, string source)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (Array.IndexOf(Extensions, ext) < 0)
            throw new InvalidOperationException("Подходят изображения PNG, JPG, BMP и GIF.");
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("Файл не найден.");
        if (info.Length > MaxBytes) throw new InvalidOperationException("Файл слишком большой: нужно не больше 25 МБ.");

        return await Task.Run(() =>
        {
            Load(source); // что файл действительно читается как картинка, проверяем до копирования
            Directory.CreateDirectory(Folder(root));
            var name = "background-" + Guid.NewGuid().ToString("N")[..8] + ext;
            File.Copy(source, Path.Combine(Folder(root), name));
            return name;
        });
    }

    /// <summary>Копирует картинку готовой темы из ресурсов лаунчера в папку backgrounds и возвращает имя файла.</summary>
    public static async Task<string> ImportResourceAsync(string root, string resourceName)
    {
        return await Task.Run(() =>
        {
            var info = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Resources/Themes/{resourceName}", UriKind.Absolute))
                       ?? throw new FileNotFoundException("Картинка темы не найдена в лаунчере.");
            Directory.CreateDirectory(Folder(root));
            var name = "background-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(resourceName);
            using var source = info.Stream;
            using var target = File.Create(Path.Combine(Folder(root), name));
            source.CopyTo(target);
            return name;
        });
    }

    public static void Delete(string root, string? name)
    {
        try
        {
            if (PathFor(root, name) is { } path) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не удалился — не страшно, лишний файл останется в папке
        }
    }

    /// <summary>
    /// Средняя яркость картинки 0–1 (смесь среднего и яркой части, чтобы светлые пятна тоже учитывались).
    /// По ней автоподбор решает, каким делать интерфейс: светлым или тёмным.
    /// </summary>
    public static double Luminance(string path)
    {
        var small = new BitmapImage();
        small.BeginInit();
        small.UriSource = new Uri(path, UriKind.Absolute);
        small.DecodePixelWidth = 64;
        small.CacheOption = BitmapCacheOption.OnLoad;
        small.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        small.EndInit();
        small.Freeze();

        var converted = new FormatConvertedBitmap(small, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);

        var values = new List<double>(w * h);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 16) continue; // прозрачные точки не считаем
            values.Add((0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i]) / 255.0);
        }
        if (values.Count == 0) return 0.5;
        values.Sort();
        var mean = values.Average();
        var bright = values[Math.Min(values.Count - 1, (int)(values.Count * 0.85))];
        return 0.7 * mean + 0.3 * bright;
    }

    /// <summary>Читает картинку с ограничением по ширине (экономит память на огромных изображениях).</summary>
    public static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.DecodePixelWidth = 2560;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
