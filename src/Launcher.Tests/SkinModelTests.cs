using Launcher.Core.Errors;
using Launcher.Core.Skins;

namespace Launcher.Tests;

public class SkinModelTests
{
    [Fact]
    public void Player_Classic64_HasAllLayers()
    {
        var model = PlayerModel.Create(slim: false);
        Assert.Equal(12, model.Cubes.Count);
        Assert.Equal(72, ModelGeometry.Build(model).Count);
        var arm = model.Cubes.Single(c => c.Name == "right_arm");
        Assert.Equal(4, arm.To.X - arm.From.X);
    }

    [Fact]
    public void Player_Slim_ArmsAreThreeWide_Legacy32_HasNoOverlaysExceptHat()
    {
        var slim = PlayerModel.Create(slim: true);
        var arm = slim.Cubes.Single(c => c.Name == "left_arm");
        Assert.Equal(3, arm.To.X - arm.From.X);

        var legacy = PlayerModel.Create(slim: false, legacy32: true);
        Assert.Equal(7, legacy.Cubes.Count);
        Assert.Equal(32, legacy.TextureHeight);
        Assert.DoesNotContain(legacy.Cubes, c => c.Name == "jacket");
    }

    [Fact]
    public void BoxUv_MatchesStandardSkinLayout()
    {
        // голова 8×8×8 с началом (0,0): лицо в (8,8)–(16,16), верх в (8,0)–(16,8)
        var uv = PlayerModel.BoxUv(8, 8, 8, 0, 0);
        Assert.Equal(new UvRect(8, 8, 16, 16), uv[CubeFace.South]);
        Assert.Equal(new UvRect(8, 0, 16, 8), uv[CubeFace.Up]);
        Assert.Equal(new UvRect(0, 8, 8, 16), uv[CubeFace.West]);
        Assert.Equal(new UvRect(16, 8, 24, 16), uv[CubeFace.East]);
        Assert.Equal(new UvRect(24, 8, 32, 16), uv[CubeFace.North]);
    }

    [Fact]
    public void Geometry_FrontFace_IsOnPlusZ_WithNormalisedUvAndOutwardNormal()
    {
        var head = PlayerModel.Create(false).Cubes.Single(c => c.Name == "head");
        var quads = ModelGeometry.Build(new SkinModel([head], 64, 64, null, []));
        var front = quads.Single(q => q.Normal == new Vec3(0, 0, 1));

        Assert.All(front.Positions, p => Assert.Equal(4, p.Z));
        Assert.Equal(new Vec3(-4, 32, 4), front.Positions[0]); // левый верхний угол картинки
        Assert.Equal(new Vec3(-4, 24, 4), front.Positions[1]);
        Assert.Equal(new Vec3(4, 24, 4), front.Positions[2]);
        Assert.Equal((8.0 / 64, 8.0 / 64), front.Uvs[0]);
        Assert.Equal((16.0 / 64, 16.0 / 64), front.Uvs[2]);
    }

    [Fact]
    public void Geometry_WindingIsCounterClockwiseSeenFromOutside()
    {
        var model = PlayerModel.Create(false);
        foreach (var quad in ModelGeometry.Build(model))
        {
            var p = quad.Positions;
            var (a, b) = (p[1] - p[0], p[2] - p[0]);
            var cross = new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
            var dot = cross.X * quad.Normal.X + cross.Y * quad.Normal.Y + cross.Z * quad.Normal.Z;
            Assert.True(dot > 0, "обход вершин должен совпадать с нормалью");
        }
    }

    [Fact]
    public void Rotate_QuarterTurnAroundY_MovesPointAroundOrigin()
    {
        var r = ModelGeometry.Rotate(new Vec3(1, 0, 0), default, new Vec3(0, 90, 0));
        Assert.Equal(0, r.X, 6);
        Assert.Equal(-1, r.Z, 6); // поворот вокруг Y: x → −z
        Assert.Equal(new Vec3(5, 5, 5), ModelGeometry.Rotate(new Vec3(5, 5, 5), new Vec3(5, 5, 5), new Vec3(10, 20, 30)));
    }

    [Fact]
    public void DefaultSkin_Is64x64Bgra_WithOpaqueHeadAndTransparentHat()
    {
        var px = PlayerModel.DefaultSkinBgra();
        Assert.Equal(64 * 64 * 4, px.Length);
        Assert.Equal(255, px[(12 * 64 + 9) * 4 + 3]);  // глаз на лице
        Assert.Equal(0, px[(8 * 64 + 40) * 4 + 3]);    // шапка (32..64, 0..16) прозрачна
    }

    private const string Png = "data:image/png;base64,iVBORw0KGgo=";

    private static string Bb(string elements, string extra = "") =>
        $$"""{ "resolution": { "width": 16, "height": 16 }, "elements": [ {{elements}} ], "textures": [ { "source": "{{Png}}" } ] {{extra}} }""";

    [Fact]
    public void Bb_Parse_ReadsCubesFacesTextureAndResolution()
    {
        var model = BbModelReader.Parse(Bb("""
            { "type": "cube", "name": "a", "from": [0, 0, 0], "to": [4, 4, 4], "origin": [2, 2, 2], "rotation": [0, 45, 0], "inflate": 0.5,
              "faces": { "north": { "uv": [0, 0, 4, 4], "texture": 0 }, "south": { "uv": [4, 0, 0, 4], "texture": 0 }, "up": { "uv": [0,0,1,1], "texture": null } } }
            """));

        var cube = Assert.Single(model.Cubes);
        Assert.Equal(new Vec3(-0.5, -0.5, -0.5), cube.From);
        Assert.Equal(new Vec3(4.5, 4.5, 4.5), cube.To);
        Assert.Equal(new Vec3(0, 45, 0), cube.Rotation);
        Assert.Equal(2, cube.Faces.Count); // грань без текстуры пропущена
        Assert.Equal(new UvRect(4, 0, 0, 4), cube.Faces[CubeFace.South]); // отражение сохраняется
        Assert.Equal(16, model.TextureWidth);
        Assert.NotNull(model.EmbeddedTexture);
        Assert.Equal(CubeFace.North, model.FaceTowardsViewer);
    }

    [Fact]
    public void Bb_Parse_BoxUvFallbackUsesStandardLayout()
    {
        var model = BbModelReader.Parse(Bb("""{ "from": [0,0,0], "to": [8,8,8], "box_uv": true, "uv_offset": [0, 0] }"""));
        // в Blockbench лицо — «north» (u+d), спина — «south»; верх перевёрнут
        Assert.Equal(new UvRect(8, 8, 16, 16), model.Cubes[0].Faces[CubeFace.North]);
        Assert.Equal(new UvRect(24, 8, 32, 16), model.Cubes[0].Faces[CubeFace.South]);
        Assert.Equal(new UvRect(16, 8, 8, 0), model.Cubes[0].Faces[CubeFace.Up]);
    }

    [Fact]
    public void Bb_Parse_NotesUnsupportedParts()
    {
        var model = BbModelReader.Parse(Bb(
            """{ "from": [0,0,0], "to": [1,1,1], "faces": { "north": { "uv": [0,0,1,1], "texture": 0 } } }, { "type": "mesh", "name": "m" }""",
            ""));
        Assert.Contains(model.Notes, n => n.Contains("Пропущено"));
    }

    private const string TwoCubes = """
        { "uuid": "A", "from": [0,0,0], "to": [2,2,2], "faces": { "north": { "uv": [0,0,1,1], "texture": 0 } } },
        { "uuid": "B", "from": [0,0,0], "to": [2,2,2], "faces": { "north": { "uv": [0,0,1,1], "texture": 0 } } },
        { "uuid": "C", "visibility": false, "from": [0,0,0], "to": [2,2,2], "faces": { "north": { "uv": [0,0,1,1], "texture": 0 } } }
        """;

    [Fact]
    public void Bb_Parse_AppliesGroupChain_InnerToOuter_AndSkipsHidden()
    {
        var model = BbModelReader.Parse(Bb(TwoCubes, """
            , "outliner": [
                { "name": "outer", "origin": [0,0,0], "rotation": [0, 0, 90], "children": [
                    { "name": "inner", "origin": [1,1,1], "rotation": [0, 90, 0], "children": [ "A" ] }, "B" ] } ]"""));

        Assert.Equal(2, model.Cubes.Count); // «C» скрыт
        var a = model.Cubes[0].Parents!;
        Assert.Equal(new[] { new Vec3(1, 1, 1), new Vec3(0, 0, 0) }, a.Select(g => g.Origin).ToArray()); // сначала внутренняя группа
        Assert.Single(model.Cubes[1].Parents!);
    }

    [Fact]
    public void Bb_Parse_HiddenGroup_ExcludesChildren()
    {
        var model = BbModelReader.Parse(Bb(TwoCubes, """
            , "outliner": [ { "name": "g", "visibility": false, "children": [ "A" ] }, "B" ]"""));
        Assert.Single(model.Cubes); // «A» в скрытой группе, остался только «B»
    }

    [Fact]
    public void Geometry_AppliesGroupRotation()
    {
        var cube = new ModelCube("c", new Vec3(1, 0, 0), new Vec3(2, 1, 1), default, default,
            PlayerModel.BoxUv(1, 1, 1, 0, 0), [new GroupTransform(default, new Vec3(0, 0, 180))]);
        var quads = ModelGeometry.Build(new SkinModel([cube], 16, 16, null, []));
        Assert.All(quads, q => Assert.All(q.Positions, p => Assert.True(p.X <= 1e-9))); // поворот на 180° вокруг Z отражает X
    }

    [Fact]
    public void Skin2D_Classic_ComposesFrontView()
    {
        var parts = Skin2D.FrontParts(slim: false, legacy32: false);
        Assert.Equal(12, parts.Count);
        var head = parts[0];
        Assert.Equal(new UvRect(8, 8, 16, 16), head.Source);
        Assert.Equal((4, 0, 8, 8), (head.X, head.Y, head.Width, head.Height));
        var hat = parts.Single(p => p.Source.X1 == 40 && p.Source.Y1 == 8);
        Assert.Equal((4, 0, 8, 8), (hat.X, hat.Y, hat.Width, hat.Height)); // шапка стоит на месте головы, без «раздувания»
        var rightArm = parts.Single(p => p.Source == new UvRect(44, 20, 48, 32));
        Assert.Equal((0, 8, 4, 12), (rightArm.X, rightArm.Y, rightArm.Width, rightArm.Height));
        Assert.Equal(16, Skin2D.Width);
    }

    [Fact]
    public void Skin2D_Slim_And_Legacy()
    {
        var slim = Skin2D.FrontParts(true, false).Single(p => p.Source == new UvRect(44, 20, 47, 32));
        Assert.Equal((1, 3), (slim.X, slim.Width));

        var legacy = Skin2D.FrontParts(false, true);
        Assert.Equal(7, legacy.Count);
        Assert.Contains(legacy, p => p.Mirror && p.Source == new UvRect(44, 20, 48, 32)); // левая рука = правая, отражённая
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "elements": [] , "resolution": { "width": 16, "height": 16 } }""")]
    [InlineData("""{ "elements": [ { "from": [0,0,0], "to": [1,1,1], "faces": { "north": { "uv": [0,0,1,1] } } } ] }""")]
    public void Bb_Parse_BadInput_Throws(string json) =>
        Assert.Throws<LauncherException>(() => BbModelReader.Parse(json));

    [Fact]
    public void Bb_Parse_BrokenEmbeddedTexture_Throws() =>
        Assert.Throws<LauncherException>(() => BbModelReader.Parse(
            """{ "resolution": { "width": 16, "height": 16 }, "elements": [ { "from": [0,0,0], "to": [1,1,1], "box_uv": true, "uv_offset": [0,0] } ], "textures": [ { "source": "data:image/png;base64,@@@" } ] }"""));

    [Fact]
    public void Pose_Wave_RaisesRightArmSideways()
    {
        var model = PlayerModel.Create(false, false, PlayerPoses.Find("wave"));
        var arm = model.Cubes.Single(c => c.Name == "right_arm");
        Assert.Equal(-150, arm.Rotation.Z);
        // нижняя точка руки (кисть) после поворота должна оказаться выше плеча и снаружи от тела (по -X)
        var hand = ModelGeometry.Rotate(new Vec3(-6, 12, 0), arm.Origin, arm.Rotation);
        Assert.True(hand.Y > 22);
        Assert.True(hand.X < -4);
    }

    [Fact]
    public void Pose_UnknownId_FallsBackToStanding() =>
        Assert.Equal("stand", PlayerPoses.Find("nope").Id);

    [Fact]
    public void Player_WithoutOverlay_HasOnlyBaseParts()
    {
        var model = PlayerModel.Create(false, false, null, overlay: false);
        Assert.Equal(6, model.Cubes.Count);
        Assert.DoesNotContain(model.Cubes, c => c.Name is "hat" or "jacket");
    }

    [Fact]
    public void Pose_Walk_SwingsLegsOppositely()
    {
        var model = PlayerModel.Create(false, false, PlayerPoses.Find("walk"));
        var right = model.Cubes.Single(c => c.Name == "right_leg").Rotation.X;
        var left = model.Cubes.Single(c => c.Name == "left_leg").Rotation.X;
        Assert.Equal(-left, right);
    }

    [Fact]
    public void BbWriter_RoundTrip_FrontOfHeadUsesFaceTexture()
    {
        var json = BbModelWriter.Write(slim: false, texturePng: null);
        var model = BbModelReader.Parse(json);

        Assert.Equal(12, model.Cubes.Count);
        Assert.Equal(CubeFace.North, model.FaceTowardsViewer);
        var head = model.Cubes.Single(c => c.Name == "head");
        Assert.Equal(new UvRect(8, 8, 16, 16), head.Faces[CubeFace.North]); // лицо скина
        // отражённые координаты: голова по-прежнему 8×8×8 вокруг оси
        Assert.Equal(new Vec3(-4, 24, -4), head.From);
        Assert.Equal(new Vec3(4, 32, 4), head.To);
        var hat = model.Cubes.Single(c => c.Name == "hat");
        Assert.Equal(new Vec3(-4.5, 23.5, -4.5), hat.From); // inflate 0.5 учтён
    }

    [Fact]
    public void BbWriter_RightArmIsOnTheViewersLeftWhenFrontIsNorth()
    {
        // В Blockbench лицо смотрит на север (−Z): правая рука игрока — на +X.
        var model = BbModelReader.Parse(BbModelWriter.Write(slim: true, texturePng: null));
        var arm = model.Cubes.Single(c => c.Name == "right_arm");
        Assert.Equal(new Vec3(4, 12, -2), arm.From);
        Assert.Equal(new Vec3(7, 24, 2), arm.To); // тонкая рука: 3 пикселя
    }

    [Fact]
    public void Overlay_OverlappingParts_HaveDifferentInflate()
    {
        // грани в одной плоскости мерцают в WPF (z-fighting): куртка, рукава и обе штанины, которые перекрываются, должны отличаться по размеру
        var parts = PlayerModel.Parts(false);
        var names = new[] { "jacket", "right_sleeve", "right_pants", "left_pants" };
        var inflates = names.Select(n => parts.Single(p => p.Name == n).Inflate).ToList();
        Assert.Equal(names.Length, inflates.Distinct().Count());
        Assert.All(inflates, i => Assert.InRange(i, 0.2, 0.4)); // заметно не толще, чем в игре
    }

    [Fact]
    public void Bb_Parse_ReversedFromTo_IsNormalised()
    {
        var model = BbModelReader.Parse(Bb("""{ "from": [8,8,8], "to": [0,0,0], "faces": { "north": { "uv": [0,0,8,8], "texture": 0 } } }"""));
        Assert.Equal(new Vec3(0, 0, 0), model.Cubes[0].From);
        Assert.Equal(new Vec3(8, 8, 8), model.Cubes[0].To);
    }

    [Fact]
    public void BbReader_ReadsModelName_AndWriterUsesTemplateName()
    {
        var model = BbModelReader.Parse(BbModelWriter.Write(slim: false, texturePng: null));
        Assert.Equal(BbModelWriter.TemplateName, model.Name);
        Assert.Null(BbModelReader.Parse(Bb("""{ "from": [0,0,0], "to": [1,1,1], "faces": { "north": { "uv": [0,0,1,1], "texture": 0 } } }""")).Name);
    }
}
