using System.Text.Json;
using Launcher.Core.Errors;

namespace Launcher.Core.Skins;

/// <summary>
/// Чтение модели Blockbench (.bbmodel — JSON). Формат Blockbench официально не описан («no complete specification»),
/// поэтому поддерживается только проверенное подмножество: кубики (<c>elements</c>) с <c>from</c>, <c>to</c>, <c>origin</c>, <c>rotation</c>,
/// <c>inflate</c>, гранями <c>faces.{north,east,south,west,up,down}.uv</c> (или <c>box_uv</c> + <c>uv_offset</c>), встроенная PNG-текстура и размер текстуры
/// (<c>resolution</c> или <c>uv_width/uv_height</c>). Повороты групп (<c>outliner</c>: origin, rotation, children) применяются; анимации и не-кубические элементы не учитываются — об этом
/// сообщается в <see cref="SkinModel.Notes"/>.
/// </summary>
public static class BbModelReader
{
    public const long MaxFileBytes = 20L * 1024 * 1024;
    private const int MaxCubes = 2000;
    private const int MaxTextureBytes = 8 * 1024 * 1024;
    private const string PngPrefix = "data:image/png;base64,";

    /// <exception cref="LauncherException">Файл не читается или не похож на модель Blockbench.</exception>
    public static SkinModel ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new LauncherException("Файл модели не найден.");
            if (info.Length > MaxFileBytes) throw new LauncherException("Файл модели слишком большой (больше 20 МБ).");
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException("Не удалось прочитать файл модели: " + ex.Message, ex);
        }
    }

    public static SkinModel Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
                throw new LauncherException("Это не модель Blockbench (.bbmodel): в файле нет списка элементов.");

            var notes = new List<string>();
            var (texW, texH, texture) = ReadTextures(root);
            if (texW <= 0 || texH <= 0)
                throw new LauncherException("В модели не указан размер текстуры (resolution), показать её нельзя.");

            var parents = ReadGroupChains(root);
            var cubes = new List<ModelCube>();
            var skipped = 0;
            foreach (var el in elements.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                // скрытые элементы и элементы «не экспортировать» в Blockbench не видны — не показываем и здесь
                if ((el.TryGetProperty("visibility", out var vis) && vis.ValueKind == JsonValueKind.False) ||
                    (el.TryGetProperty("export", out var exp) && exp.ValueKind == JsonValueKind.False)) continue;
                var uuid = Str(el, "uuid");
                if (uuid is not null && parents.Hidden.Contains(uuid)) continue;
                if (Str(el, "type") is { } type && type != "cube") { skipped++; continue; }
                if (cubes.Count >= MaxCubes) throw new LauncherException("В модели слишком много элементов.");

                var from = Vec(el, "from") ?? throw new LauncherException("У одного из элементов нет координат from/to.");
                var to = Vec(el, "to") ?? throw new LauncherException("У одного из элементов нет координат from/to.");
                (from, to) = (new Vec3(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Min(from.Z, to.Z)),
                              new Vec3(Math.Max(from.X, to.X), Math.Max(from.Y, to.Y), Math.Max(from.Z, to.Z)));
                var inflate = el.TryGetProperty("inflate", out var inf) && inf.TryGetDouble(out var iv) ? iv : 0;
                var grow = new Vec3(inflate, inflate, inflate);
                var origin = Vec(el, "origin") ?? default;
                var rotation = Vec(el, "rotation") ?? default;

                var faces = ReadFaces(el, from, to);
                if (faces.Count == 0) { skipped++; continue; }
                cubes.Add(new ModelCube(Str(el, "name") ?? "cube", from - grow, to + grow, origin, rotation, faces,
                    uuid is not null && parents.Chains.TryGetValue(uuid, out var chain) ? chain : null));
            }

            if (cubes.Count == 0) throw new LauncherException("В модели нет ни одного кубика, который можно показать.");
            if (skipped > 0) notes.Add($"Пропущено элементов, которые лаунчер не умеет показывать: {skipped}.");
            if (texture is null) notes.Add("В файле нет встроенной текстуры: используется ваш скин.");

            // Лицом к зрителю ставится сторона «north» — так модель открывается в Blockbench по умолчанию (не проверено; можно покрутить мышью).
            return new SkinModel(cubes, texW, texH, texture, notes, CubeFace.North, Str(root, "name"));
        }
        catch (JsonException ex)
        {
            throw new LauncherException("Файл модели повреждён (не корректный JSON).", ex);
        }
    }

    private static (double W, double H, byte[]? Texture) ReadTextures(JsonElement root)
    {
        double w = 0, h = 0;
        if (root.TryGetProperty("resolution", out var res) && res.ValueKind == JsonValueKind.Object)
        {
            w = Num(res, "width");
            h = Num(res, "height");
        }

        byte[]? png = null;
        if (root.TryGetProperty("textures", out var textures) && textures.ValueKind == JsonValueKind.Array && textures.GetArrayLength() > 0)
        {
            var first = textures[0];
            if (first.ValueKind == JsonValueKind.Object)
            {
                if (w <= 0) w = Num(first, "uv_width");
                if (h <= 0) h = Num(first, "uv_height");
                var source = Str(first, "source");
                if (source is not null && source.StartsWith(PngPrefix, StringComparison.Ordinal))
                {
                    var b64 = source[PngPrefix.Length..];
                    if (b64.Length > MaxTextureBytes * 4 / 3 + 8) throw new LauncherException("Встроенная текстура модели слишком большая.");
                    try { png = Convert.FromBase64String(b64); }
                    catch (FormatException) { throw new LauncherException("Встроенная текстура модели повреждена."); }
                }
            }
        }
        return (w, h, png);
    }

    private static Dictionary<CubeFace, UvRect> ReadFaces(JsonElement el, Vec3 from, Vec3 to)
    {
        var result = new Dictionary<CubeFace, UvRect>();
        if (el.TryGetProperty("faces", out var faces) && faces.ValueKind == JsonValueKind.Object)
        {
            foreach (var (name, face) in new[]
            {
                ("north", CubeFace.North), ("east", CubeFace.East), ("south", CubeFace.South),
                ("west", CubeFace.West), ("up", CubeFace.Up), ("down", CubeFace.Down)
            })
            {
                if (!faces.TryGetProperty(name, out var f) || f.ValueKind != JsonValueKind.Object) continue;
                // у грани без текстуры (texture: null) картинки нет — не рисуем
                if (f.TryGetProperty("texture", out var t) && t.ValueKind == JsonValueKind.Null) continue;
                if (!f.TryGetProperty("uv", out var uv) || uv.ValueKind != JsonValueKind.Array || uv.GetArrayLength() != 4) continue;
                var n = uv.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
                if (n.Any(double.IsNaN)) continue;
                result[face] = new UvRect(n[0], n[1], n[2], n[3]);
            }
        }

        // Если у кубика нет граней, но включён box_uv — стандартная развёртка от uv_offset.
        if (result.Count == 0 && el.TryGetProperty("box_uv", out var box) && box.ValueKind == JsonValueKind.True &&
            Vec2(el, "uv_offset") is { } off)
        {
            var size = to - from;
            foreach (var kv in PlayerModel.BlockbenchBoxUv(size.X, size.Y, size.Z, off.X, off.Y))
                result[kv.Key] = kv.Value;
        }
        return result;
    }

    /// <summary>
    /// Иерархия групп (<c>outliner</c>): у каждой группы <c>origin</c>, <c>rotation</c> и <c>children</c> (uuid кубиков или вложенные группы).
    /// Для каждого кубика собирается цепочка поворотов от ближайшей группы к внешней; скрытые группы исключаются.
    /// </summary>
    private static (Dictionary<string, IReadOnlyList<GroupTransform>> Chains, HashSet<string> Hidden) ReadGroupChains(JsonElement root)
    {
        var chains = new Dictionary<string, IReadOnlyList<GroupTransform>>();
        var hidden = new HashSet<string>();

        void Walk(JsonElement nodes, List<GroupTransform> chain, bool isHidden, int depth)
        {
            if (nodes.ValueKind != JsonValueKind.Array || depth > 64) return;
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind == JsonValueKind.String)
                {
                    var id = node.GetString()!;
                    if (isHidden) hidden.Add(id);
                    else if (chain.Count > 0) chains[id] = chain.AsEnumerable().Reverse().ToList();
                }
                else if (node.ValueKind == JsonValueKind.Object)
                {
                    var groupHidden = isHidden ||
                        (node.TryGetProperty("visibility", out var v) && v.ValueKind == JsonValueKind.False) ||
                        (node.TryGetProperty("export", out var e) && e.ValueKind == JsonValueKind.False);
                    var next = new List<GroupTransform>(chain) { new(Vec(node, "origin") ?? default, Vec(node, "rotation") ?? default) };
                    if (node.TryGetProperty("children", out var children)) Walk(children, next, groupHidden, depth + 1);
                }
            }
        }

        if (root.TryGetProperty("outliner", out var outliner)) Walk(outliner, [], false, 0);
        return (chains, hidden);
    }

    private static Vec3? Vec(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 3) return null;
        var n = a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
        return n.Any(double.IsNaN) ? null : new Vec3(n[0], n[1], n[2]);
    }

    private static (double X, double Y)? Vec2(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 2) return null;
        var n = a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
        return n.Any(double.IsNaN) ? null : (n[0], n[1]);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
}
