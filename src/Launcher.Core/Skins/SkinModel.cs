using System.Text.Json;
using Launcher.Core.Errors;

namespace Launcher.Core.Skins;

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
}

/// <summary>Грань кубика. Оси: X — вправо от зрителя, Y — вверх, Z — к зрителю. North = −Z (спина игрока), South = +Z (лицо).</summary>
public enum CubeFace { North, East, South, West, Up, Down }

/// <summary>Прямоугольник текстуры в пикселях: (X1,Y1) — левый верхний угол картинки грани, (X2,Y2) — правый нижний. Если X1 &gt; X2, картинка отражена.</summary>
public sealed record UvRect(double X1, double Y1, double X2, double Y2);

/// <param name="From">Минимальный угол, в пикселях модели.</param>
/// <param name="To">Максимальный угол.</param>
/// <param name="Origin">Точка, вокруг которой применяется <paramref name="Rotation"/> (градусы по X, Y, Z).</param>
public sealed record ModelCube(
    string Name, Vec3 From, Vec3 To, Vec3 Origin, Vec3 Rotation, IReadOnlyDictionary<CubeFace, UvRect> Faces,
    IReadOnlyList<GroupTransform>? Parents = null);

/// <summary>Поворот группы Blockbench вокруг её точки; применяется к кубикам внутри (от ближайшей группы к внешней).</summary>
public sealed record GroupTransform(Vec3 Origin, Vec3 Rotation);

/// <summary>Модель из кубиков и размер текстуры, к которому относятся координаты UV.</summary>
/// <param name="EmbeddedTexture">PNG, встроенный в файл модели (если есть); иначе показывается скин.</param>
/// <param name="FaceTowardsViewer">Какой гранью модель повёрнута к зрителю в начальном положении.</param>
public sealed record SkinModel(
    IReadOnlyList<ModelCube> Cubes, double TextureWidth, double TextureHeight, byte[]? EmbeddedTexture,
    IReadOnlyList<string> Notes, CubeFace FaceTowardsViewer = CubeFace.South, string? Name = null);

/// <summary>Четырёхугольник для отрисовки: 4 вершины (по часовой от левого верхнего угла картинки грани — против часовой при взгляде снаружи) и UV 0..1.</summary>
public sealed record ModelQuad(Vec3[] Positions, (double U, double V)[] Uvs, Vec3 Normal);

/// <summary>Стандартная модель игрока (Steve / Alex) на основе раскладки скина 64×64 (или старого 64×32).</summary>
/// <summary>Поза стандартной модели: углы в градусах. По X: плюс — назад, минус — вперёд; по Z у рук: минус — правая рука (игрока) уходит вбок и вверх.</summary>
public sealed record PlayerPose(
    string Id, string Name,
    double RightArmX = 0, double RightArmZ = 0, double LeftArmX = 0, double LeftArmZ = 0,
    double RightLegX = 0, double LeftLegX = 0, double HeadYaw = 0);

/// <summary>Готовые позы, из которых выбирают в настройках.</summary>
/// <summary>Часть стандартной модели (см. <see cref="PlayerModel.Parts"/>).</summary>
public sealed record PlayerPart(string Name, Vec3 From, Vec3 To, int U, int V, double Inflate = 0);

public static class PlayerPoses
{
    public static IReadOnlyList<PlayerPose> All { get; } =
    [
        new("stand", "Стоит"),
        new("walk", "Идёт", RightArmX: 28, LeftArmX: -28, RightLegX: -24, LeftLegX: 24),
        new("wave", "Машет рукой", RightArmZ: -150, HeadYaw: 12),
        new("tpose", "Руки в стороны", RightArmZ: -90, LeftArmZ: 90),
        new("heroic", "Бодрая", RightArmX: -20, RightArmZ: -25, LeftArmZ: 25, RightLegX: -8, LeftLegX: 8, HeadYaw: -10)
    ];

    public static PlayerPose Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
}

public static class PlayerModel
{
    public static SkinModel Create(bool slim, bool legacy32 = false, PlayerPose? pose = null, bool overlay = true)
    {
        pose ??= PlayerPoses.All[0];
        var cubes = new List<ModelCube>();
        foreach (var part in Parts(slim, legacy32, overlay))
        {
            var size = part.To - part.From;
            var faces = BoxUv((int)size.X, (int)size.Y, (int)size.Z, part.U, part.V);
            var grow = new Vec3(part.Inflate, part.Inflate, part.Inflate);
            var (from, to) = (part.From, part.To);
            // Поворот части тела вокруг сустава: плечо, бедро или шея (по неувеличенному кубику).
            var (origin, rotation) = part.Name switch
            {
                "head" or "hat" => (new Vec3(0, 24, 0), new Vec3(0, pose.HeadYaw, 0)),
                "right_arm" or "right_sleeve" => (new Vec3((from.X + to.X) / 2, 22, 0), new Vec3(pose.RightArmX, 0, pose.RightArmZ)),
                "left_arm" or "left_sleeve" => (new Vec3((from.X + to.X) / 2, 22, 0), new Vec3(pose.LeftArmX, 0, pose.LeftArmZ)),
                "right_leg" or "right_pants" => (new Vec3((from.X + to.X) / 2, 12, 0), new Vec3(pose.RightLegX, 0, 0)),
                "left_leg" or "left_pants" => (new Vec3((from.X + to.X) / 2, 12, 0), new Vec3(pose.LeftLegX, 0, 0)),
                _ => (default(Vec3), default(Vec3))
            };
            cubes.Add(new ModelCube(part.Name, from - grow, to + grow, origin, rotation, faces));
        }

        return new SkinModel(cubes, 64, legacy32 ? 32 : 64, null, []);
    }

    // В игре все части второго слоя «раздуты» на 0,25 и их грани лежат в одной плоскости (куртка и рукава, обе штанины, куртка и штаны).
    // В WPF такие грани мерцают друг через друга (светлые линии на стыках), поэтому у каждой части свой чуть-чуть другой размер: разница около полпикселя.
    private const double SleeveInflate = 0.31;
    private const double RightPantsInflate = 0.27;
    private const double LeftPantsInflate = 0.29;

    /// <summary>Части стандартной модели: размеры без увеличения, смещение в текстуре (U, V) и «раздутие» второго слоя.</summary>
    public static IReadOnlyList<PlayerPart> Parts(bool slim, bool legacy32 = false, bool overlay = true)
    {
        var armW = slim ? 3 : 4;
        var parts = new List<PlayerPart>
        {
            new("head", new(-4, 24, -4), new(4, 32, 4), 0, 0),
            new("body", new(-4, 12, -2), new(4, 24, 2), 16, 16),
            new("right_arm", new(-4 - armW, 12, -2), new(-4, 24, 2), 40, 16),
            new("right_leg", new(-4, 0, -2), new(0, 12, 2), 0, 16),
            // В старом формате 64×32 левые руки и ноги берут текстуру правых.
            new("left_arm", new(4, 12, -2), new(4 + armW, 24, 2), legacy32 ? 40 : 32, legacy32 ? 16 : 48),
            new("left_leg", new(0, 0, -2), new(4, 12, 2), legacy32 ? 0 : 16, legacy32 ? 16 : 48)
        };
        if (overlay) parts.Add(new("hat", new(-4, 24, -4), new(4, 32, 4), 32, 0, 0.5));
        if (overlay && !legacy32)
        {
            parts.Add(new("jacket", new(-4, 12, -2), new(4, 24, 2), 16, 32, 0.25));
            parts.Add(new("right_sleeve", new(-4 - armW, 12, -2), new(-4, 24, 2), 40, 32, SleeveInflate));
            parts.Add(new("left_sleeve", new(4, 12, -2), new(4 + armW, 24, 2), 48, 48, SleeveInflate));
            parts.Add(new("right_pants", new(-4, 0, -2), new(0, 12, 2), 0, 32, RightPantsInflate));
            parts.Add(new("left_pants", new(0, 0, -2), new(4, 12, 2), 0, 48, LeftPantsInflate));
        }
        return parts;
    }

    /// <summary>
    /// Развёртка <c>box_uv</c> в Blockbench (сверено с исходным кодом Blockbench, <c>cube.js</c>, <c>updateUV</c>): лицо — «north», спина — «south»,
    /// «east» — левый край развёртки; верх и низ перевёрнуты. У нашей стандартной модели лицо — «south», поэтому раскладки разные.
    /// </summary>
    public static IReadOnlyDictionary<CubeFace, UvRect> BlockbenchBoxUv(double w, double h, double d, double u, double v) => new Dictionary<CubeFace, UvRect>
    {
        [CubeFace.East] = new(u, v + d, u + d, v + d + h),
        [CubeFace.North] = new(u + d, v + d, u + d + w, v + d + h),
        [CubeFace.West] = new(u + d + w, v + d, u + 2 * d + w, v + d + h),
        [CubeFace.South] = new(u + 2 * d + w, v + d, u + 2 * d + 2 * w, v + d + h),
        [CubeFace.Up] = new(u + d + w, v + d, u + d, v),
        [CubeFace.Down] = new(u + d + 2 * w, v, u + d + w, v + d)
    };

    /// <summary>Стандартная раскладка развёртки кубика w×h×d в текстуре, начиная с (u, v) (как в скинах и Blockbench box UV).</summary>
    public static IReadOnlyDictionary<CubeFace, UvRect> BoxUv(int w, int h, int d, int u, int v) => new Dictionary<CubeFace, UvRect>
    {
        [CubeFace.West] = new(u, v + d, u + d, v + d + h),
        [CubeFace.South] = new(u + d, v + d, u + d + w, v + d + h),
        [CubeFace.East] = new(u + d + w, v + d, u + 2 * d + w, v + d + h),
        [CubeFace.North] = new(u + 2 * d + w, v + d, u + 2 * d + 2 * w, v + d + h),
        [CubeFace.Up] = new(u + d, v, u + d + w, v + d),
        [CubeFace.Down] = new(u + d + w, v + d, u + d + 2 * w, v) // низ в развёртке перевёрнут по вертикали
    };

    /// <summary>Картинка 64×64 (BGRA) для случая, когда своего скина нет: простой «человечек» с лицом.</summary>
    public static byte[] DefaultSkinBgra()
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        void Fill(double x1, double y1, double x2, double y2, uint rgb)
        {
            for (var y = (int)Math.Min(y1, y2); y < (int)Math.Max(y1, y2); y++)
                for (var x = (int)Math.Min(x1, x2); x < (int)Math.Max(x1, x2); x++)
                {
                    var i = (y * size + x) * 4;
                    pixels[i] = (byte)rgb; pixels[i + 1] = (byte)(rgb >> 8); pixels[i + 2] = (byte)(rgb >> 16); pixels[i + 3] = 255;
                }
        }

        foreach (var cube in Create(false).Cubes)
        {
            var color = cube.Name switch
            {
                "head" => 0xC69C6Du,
                "body" => 0x14A5A5u,
                "right_arm" or "left_arm" => 0xC69C6Du,
                "right_leg" or "left_leg" => 0x3C44AAu,
                _ => 0u
            };
            if (color == 0) continue; // слои (шапка, куртка…) остаются прозрачными
            foreach (var r in cube.Faces.Values) Fill(r.X1, r.Y1, r.X2, r.Y2, color);
        }
        Fill(8, 0, 16, 2, 0x3B2A1Eu);   // волосы
        Fill(9, 12, 11, 13, 0x2F2F7Fu); // глаза
        Fill(13, 12, 15, 13, 0x2F2F7Fu);
        Fill(11, 14, 13, 15, 0x7A4B3Au); // рот
        return pixels;
    }
}

/// <summary>Построение четырёхугольников из кубиков модели (без привязки к WPF — проверяется тестами).</summary>
public static class ModelGeometry
{
    public static IReadOnlyList<ModelQuad> Build(SkinModel model)
    {
        var quads = new List<ModelQuad>();
        foreach (var cube in model.Cubes)
        {
            var (a, b) = (cube.From, cube.To);
            foreach (var (face, rect) in cube.Faces)
            {
                // левый верхний угол картинки грани, направление «вправо» и «вниз» в пространстве
                var (tl, right, down) = face switch
                {
                    CubeFace.South => (new Vec3(a.X, b.Y, b.Z), new Vec3(1, 0, 0), new Vec3(0, -1, 0)),
                    CubeFace.North => (new Vec3(b.X, b.Y, a.Z), new Vec3(-1, 0, 0), new Vec3(0, -1, 0)),
                    CubeFace.West => (new Vec3(a.X, b.Y, a.Z), new Vec3(0, 0, 1), new Vec3(0, -1, 0)),
                    CubeFace.East => (new Vec3(b.X, b.Y, b.Z), new Vec3(0, 0, -1), new Vec3(0, -1, 0)),
                    CubeFace.Up => (new Vec3(a.X, b.Y, a.Z), new Vec3(1, 0, 0), new Vec3(0, 0, 1)),
                    _ => (new Vec3(a.X, a.Y, b.Z), new Vec3(1, 0, 0), new Vec3(0, 0, -1))
                };
                var (w, h) = face switch
                {
                    CubeFace.South or CubeFace.North => (b.X - a.X, b.Y - a.Y),
                    CubeFace.West or CubeFace.East => (b.Z - a.Z, b.Y - a.Y),
                    _ => (b.X - a.X, b.Z - a.Z)
                };

                var corners = new[] { tl, tl + down * h, tl + right * w + down * h, tl + right * w };
                var uvs = new[]
                {
                    (rect.X1 / model.TextureWidth, rect.Y1 / model.TextureHeight),
                    (rect.X1 / model.TextureWidth, rect.Y2 / model.TextureHeight),
                    (rect.X2 / model.TextureWidth, rect.Y2 / model.TextureHeight),
                    (rect.X2 / model.TextureWidth, rect.Y1 / model.TextureHeight)
                };

                var normal = face switch
                {
                    CubeFace.South => new Vec3(0, 0, 1),
                    CubeFace.North => new Vec3(0, 0, -1),
                    CubeFace.West => new Vec3(-1, 0, 0),
                    CubeFace.East => new Vec3(1, 0, 0),
                    CubeFace.Up => new Vec3(0, 1, 0),
                    _ => new Vec3(0, -1, 0)
                };

                if (cube.Rotation != default)
                {
                    for (var i = 0; i < 4; i++) corners[i] = Rotate(corners[i], cube.Origin, cube.Rotation);
                    normal = Rotate(normal, default, cube.Rotation);
                }
                if (cube.Parents is not null)
                {
                    foreach (var group in cube.Parents)
                    {
                        if (group.Rotation == default) continue;
                        for (var i = 0; i < 4; i++) corners[i] = Rotate(corners[i], group.Origin, group.Rotation);
                        normal = Rotate(normal, default, group.Rotation);
                    }
                }
                quads.Add(new ModelQuad(corners, uvs, normal));
            }
        }
        return quads;
    }

    /// <summary>Поворот вокруг точки: сначала по X, затем по Y, затем по Z (градусы).</summary>
    public static Vec3 Rotate(Vec3 point, Vec3 origin, Vec3 degrees)
    {
        var p = point - origin;
        double x = p.X, y = p.Y, z = p.Z;
        (y, z) = Turn(y, z, degrees.X);
        (z, x) = Turn(z, x, degrees.Y);
        (x, y) = Turn(x, y, degrees.Z);
        return new Vec3(x, y, z) + origin;
    }

    private static (double, double) Turn(double a, double b, double deg)
    {
        var r = deg * Math.PI / 180;
        var (s, c) = (Math.Sin(r), Math.Cos(r));
        return (a * c - b * s, a * s + b * c);
    }
}
