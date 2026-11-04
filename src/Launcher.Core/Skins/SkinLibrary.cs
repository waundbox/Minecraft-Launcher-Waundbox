using System.Text.Json;
using Launcher.Core.Errors;

namespace Launcher.Core.Skins;

public sealed record SkinEntry(string Id, string Name, bool Slim);

/// <summary>
/// Коллекция сохранённых скинов (как «Скины» в официальном лаунчере): PNG-файлы в папке и список <c>library.json</c>.
/// Скины лежат у пользователя на диске и никуда не отправляются, пока он сам не применит один из них к аккаунту.
/// </summary>
public sealed class SkinLibrary
{
    public const int MaxSkins = 100;
    private const int MaxNameLength = 32;

    private readonly string _directory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SkinLibrary(string directory) => _directory = directory;

    private string IndexPath => Path.Combine(_directory, "library.json");

    private string PngPath(string id) => Path.Combine(_directory, id + ".png");

    public async Task<IReadOnlyList<SkinEntry>> ListAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadAsync(ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <exception cref="LauncherException">Файл не подходит как скин или коллекция переполнена.</exception>
    public async Task<SkinEntry> AddAsync(string name, byte[] png, bool slim, CancellationToken ct)
    {
        SkinImage.Validate(png);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = (await LoadAsync(ct).ConfigureAwait(false)).ToList();
            if (list.Count >= MaxSkins) throw new LauncherException($"В коллекции уже {MaxSkins} скинов — удалите ненужные.");

            var entry = new SkinEntry(Guid.NewGuid().ToString("N"), CleanName(name), slim);
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(PngPath(entry.Id), png, ct).ConfigureAwait(false);
            list.Add(entry);
            await SaveAsync(list, ct).ConfigureAwait(false);
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException("Не удалось сохранить скин в коллекцию: " + ex.Message, ex);
        }
        finally { _lock.Release(); }
    }

    public async Task<byte[]?> ReadPngAsync(string id, CancellationToken ct)
    {
        if (!IsSafeId(id)) return null;
        var path = PngPath(id);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        if (!IsSafeId(id)) return;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = (await LoadAsync(ct).ConfigureAwait(false)).Where(e => e.Id != id).ToList();
            await SaveAsync(list, ct).ConfigureAwait(false);
            var path = PngPath(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException("Не удалось удалить скин: " + ex.Message, ex);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Имя без служебных символов, не длиннее 32 знаков; пустое заменяется на «Скин».</summary>
    public static string CleanName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxNameLength) cleaned = cleaned[..MaxNameLength].TrimEnd();
        return cleaned.Length == 0 ? "Скин" : cleaned;
    }

    /// <summary>Идентификатор — 32 шестнадцатеричных знака: так по нему нельзя выйти за пределы папки.</summary>
    private static bool IsSafeId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);

    private async Task<IReadOnlyList<SkinEntry>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(IndexPath)) return [];
        try
        {
            await using var stream = File.OpenRead(IndexPath);
            var list = await JsonSerializer.DeserializeAsync<List<SkinEntry>>(stream, cancellationToken: ct).ConfigureAwait(false);
            // записи с некорректным id и без файла пропускаются
            return (list ?? []).Where(e => e is not null && IsSafeId(e.Id) && File.Exists(PngPath(e.Id))).ToList();
        }
        catch (JsonException)
        {
            return []; // повреждённый список: начинаем с пустого, PNG-файлы остаются на диске
        }
    }

    private async Task SaveAsync(List<SkinEntry> list, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var temp = IndexPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(list), ct).ConfigureAwait(false);
        File.Move(temp, IndexPath, overwrite: true);
    }
}

/// <summary>Один фрагмент двухмерного вида спереди: откуда взять из скина и куда поставить на картинке 16×32.</summary>
public sealed record SkinPart2D(UvRect Source, int X, int Y, int Width, int Height, bool Mirror);

/// <summary>Вид персонажа спереди (как на плитках скинов в официальном лаунчере), собранный из частей скина.</summary>
public static class Skin2D
{
    public const int Width = 16;
    public const int Height = 32;

    private static readonly Dictionary<string, string> BaseOf = new()
    {
        ["hat"] = "head", ["jacket"] = "body", ["right_sleeve"] = "right_arm",
        ["left_sleeve"] = "left_arm", ["right_pants"] = "right_leg", ["left_pants"] = "left_leg"
    };

    /// <summary>Части в порядке рисования: сначала тело, затем второй слой.</summary>
    public static IReadOnlyList<SkinPart2D> FrontParts(bool slim, bool legacy32)
    {
        var model = PlayerModel.Create(slim, legacy32);
        var parts = new List<SkinPart2D>();
        foreach (var cube in model.Cubes)
        {
            var baseCube = BaseOf.TryGetValue(cube.Name, out var b) ? model.Cubes.First(c => c.Name == b) : cube;
            var x = (int)Math.Round(baseCube.From.X + 8);
            var y = (int)Math.Round(32 - baseCube.To.Y);
            var w = (int)Math.Round(baseCube.To.X - baseCube.From.X);
            var h = (int)Math.Round(baseCube.To.Y - baseCube.From.Y);
            var mirror = legacy32 && cube.Name is "left_arm" or "left_leg";
            parts.Add(new SkinPart2D(cube.Faces[CubeFace.South], x, y, w, h, mirror));
        }
        return parts;
    }

    /// <summary>Лицо и шапка (8×8 пикселей) для маленького значка.</summary>
    public static IReadOnlyList<UvRect> HeadSources { get; } = [new UvRect(8, 8, 16, 16), new UvRect(40, 8, 48, 16)];
}
