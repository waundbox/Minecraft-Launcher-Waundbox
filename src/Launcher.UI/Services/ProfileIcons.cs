using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Launcher.UI.Services;

/// <summary>
/// Значки профилей: картинки из папки icons в папке лаунчера (кладутся вручную, подхватываются автоматически).
/// Новому профилю достаётся случайный значок. Размер декодирования — настройка «разрешение значков» (по умолчанию 32).
/// </summary>
internal static class ProfileIcons
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];
    private static readonly Dictionary<(string, int), ImageSource?> Cache = new();
    private static readonly Random Rng = new();
    private static string _directory = "";

    public static int Size { get; private set; } = 32;
    public static string Directory => _directory;

    public static void Configure(string directory, int size)
    {
        _directory = directory;
        var normalized = size is >= 8 and <= 512 ? size : 32;
        if (normalized != Size) Cache.Clear();
        Size = normalized;
        try { System.IO.Directory.CreateDirectory(directory); }
        catch (Exception) { /* папка недоступна — значков просто не будет */ }
    }

    /// <summary>Имена файлов значков (только имена, без путей).</summary>
    public static IReadOnlyList<string> Names()
    {
        try
        {
            if (_directory.Length == 0 || !System.IO.Directory.Exists(_directory)) return [];
            return System.IO.Directory.EnumerateFiles(_directory)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(f => Path.GetFileName(f))
                .OfType<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public static string? PickRandom()
    {
        var names = Names();
        return names.Count == 0 ? null : names[Rng.Next(names.Count)];
    }

    public static ImageSource? Load(string? name)
    {
        if (string.IsNullOrEmpty(name) || _directory.Length == 0 || Path.GetFileName(name) != name) return null;
        if (Cache.TryGetValue((name, Size), out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            var path = Path.Combine(_directory, name);
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = Size;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception)
        {
            image = null; // повреждённый файл — как будто значка нет
        }
        Cache[(name, Size)] = image;
        return image;
    }

    /// <summary>Сбрасывает кэш (после того как файлы в папке поменялись).</summary>
    public static void Refresh() => Cache.Clear();
}
