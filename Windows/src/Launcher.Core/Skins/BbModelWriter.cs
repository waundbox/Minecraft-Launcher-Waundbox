using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Core.Skins;

/// <summary>
/// Записывает стандартную модель игрока в файл Blockbench (.bbmodel): его можно открыть в Blockbench, изменить
/// и подключить обратно в «Настройки» → «Модель персонажа на главной». Формат Blockbench официально не описан;
/// используются поля, которые читает и сам лаунчер: cube, from/to, inflate, origin, uv_offset, faces.*.uv, встроенная текстура.
/// В Blockbench лицо модели смотрит на «north», поэтому координаты X и Z переворачиваются.
/// </summary>
public static class BbModelWriter
{
    /// <summary>Имя модели в файле шаблона: по нему лаунчер понимает, что модель сделана из стандартной, и накладывает на неё выбранный скин.</summary>
    public const string TemplateName = "Waundbox player";

    public static string Write(bool slim, byte[]? texturePng, string name = TemplateName)
    {
        var parts = PlayerModel.Parts(slim);
        var elements = new JsonArray();
        var outliner = new JsonArray();
        foreach (var part in parts)
        {
            var size = part.To - part.From;
            // Поворот на 180° вокруг Y: (x, z) → (-x, -z)
            var from = new Vec3(-part.To.X, part.From.Y, -part.To.Z);
            var to = new Vec3(-part.From.X, part.To.Y, -part.From.Z);
            var uuid = Guid.NewGuid().ToString();
            var faces = new JsonObject();
            foreach (var (face, key) in new[]
            {
                (CubeFace.North, "north"), (CubeFace.East, "east"), (CubeFace.South, "south"),
                (CubeFace.West, "west"), (CubeFace.Up, "up"), (CubeFace.Down, "down")
            })
            {
                var r = PlayerModel.BlockbenchBoxUv(size.X, size.Y, size.Z, part.U, part.V)[face];
                var face_ = new JsonObject { ["uv"] = new JsonArray(r.X1, r.Y1, r.X2, r.Y2) };
                if (texturePng is not null) face_["texture"] = 0;
                faces[key] = face_;
            }

            elements.Add(new JsonObject
            {
                ["name"] = part.Name,
                ["type"] = "cube",
                ["box_uv"] = true,
                ["from"] = new JsonArray(from.X, from.Y, from.Z),
                ["to"] = new JsonArray(to.X, to.Y, to.Z),
                ["inflate"] = part.Inflate,
                ["origin"] = new JsonArray(0, 0, 0),
                ["rotation"] = new JsonArray(0, 0, 0),
                ["uv_offset"] = new JsonArray(part.U, part.V),
                ["faces"] = faces,
                ["uuid"] = uuid
            });
            outliner.Add(uuid);
        }

        var root = new JsonObject
        {
            ["meta"] = new JsonObject { ["format_version"] = "4.10", ["model_format"] = "free", ["box_uv"] = true },
            ["name"] = name,
            ["resolution"] = new JsonObject { ["width"] = 64, ["height"] = 64 },
            ["elements"] = elements,
            ["outliner"] = outliner,
            ["textures"] = new JsonArray()
        };
        if (texturePng is not null)
        {
            ((JsonArray)root["textures"]!).Add(new JsonObject
            {
                ["name"] = "skin.png",
                ["source"] = "data:image/png;base64," + Convert.ToBase64String(texturePng),
                ["uv_width"] = 64,
                ["uv_height"] = 64
            });
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
