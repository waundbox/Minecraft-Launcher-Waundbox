using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Core.Skins;
using Launcher.UI.Services;

namespace Launcher.UI.Controls;

/// <summary>
/// Трёхмерная модель игрока со скином. Вращается мышью (перетаскивание) и медленно сама, если не включён экономный режим.
/// Показывает стандартную модель (Steve/Alex) или модель из Blockbench (<see cref="Model"/>).
/// <para>
/// Рисуется программно (без Viewport3D): каждый непрозрачный пиксель текстуры — маленький квадратик в пространстве, который
/// проецируется и закрашивается одним цветом. Так пиксель скина всегда лежит ровно там, где ему положено: нет буфера глубины WPF
/// (путал основной и второй слои), нет фильтрации текстур (мыло, светлые края), прозрачные пиксели просто не рисуются.
/// </para>
/// </summary>
internal sealed class SkinViewer : UserControl
{
    public static readonly DependencyProperty SkinPngProperty = DependencyProperty.Register(
        nameof(SkinPng), typeof(byte[]), typeof(SkinViewer), new PropertyMetadata(null, (d, _) => ((SkinViewer)d).Rebuild()));

    public static readonly DependencyProperty SlimProperty = DependencyProperty.Register(
        nameof(Slim), typeof(bool), typeof(SkinViewer), new PropertyMetadata(false, (d, _) => ((SkinViewer)d).Rebuild()));

    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(SkinModel), typeof(SkinViewer), new PropertyMetadata(null, (d, _) => ((SkinViewer)d).Rebuild()));

    public static readonly DependencyProperty PoseIdProperty = DependencyProperty.Register(
        nameof(PoseId), typeof(string), typeof(SkinViewer), new PropertyMetadata(null, (d, _) => ((SkinViewer)d).Rebuild()));

    public static readonly DependencyProperty ShowOverlayProperty = DependencyProperty.Register(
        nameof(ShowOverlay), typeof(bool), typeof(SkinViewer), new PropertyMetadata(true, (d, _) => ((SkinViewer)d).Rebuild()));

    public static readonly DependencyProperty AutoSpinProperty = DependencyProperty.Register(
        nameof(AutoSpin), typeof(bool), typeof(SkinViewer), new PropertyMetadata(true, (d, _) => ((SkinViewer)d).UpdateSpin()));

    /// <summary>Один пиксель текстуры в пространстве: угол, два ребра, нормаль и цвет.</summary>
    private struct Texel
    {
        public float X, Y, Z;
        public float Rx, Ry, Rz;
        public float Dx, Dy, Dz;
        public float Nx, Ny, Nz;
        public int R, G, B;
    }

    private const double FieldOfView = 32;   // по горизонтали, как раньше у камеры WPF
    private const double VisibleHeight = 44; // сколько высоты модели (с запасом на наклон) должно помещаться
    private const double VisibleWidth = 40;  // и ширины (с запасом на разведённые руки)
    private const double CameraDistance = 70;
    private const int MaxTexels = 300_000;
    private const double SpinDegreesPerSecond = 15;
    private const int MaxMovingSide = 600;  // пока модель крутится или её тянут мышью: дёшево для процессора
    private const int MaxStillSide = 1400; // когда стоит: один чёткий кадр в полном размере окна

    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, SnapsToDevicePixels = true };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Texel[] _texels = [];
    private WriteableBitmap? _bitmap;
    private int[] _pixels = [];
    private float[] _depth = [];
    private double _yaw = 25;
    private double _pitch = 8;
    private double _lastFrame;
    private Point _last;
    private bool _dragging;
    private bool _spinning;

    public SkinViewer()
    {
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);
        Content = _image;
        Background = Brushes.Transparent; // чтобы мышь ловилась и над пустым местом
        Cursor = Cursors.SizeWE;
        Focusable = false;
        ClipToBounds = false;

        SizeChanged += (_, _) => Render();
        Loaded += (_, _) => { Motion.EcoChanged += UpdateSpin; AppPower.Changed += OnPower; QualityChanged += OnQuality; UpdateSpin(); Render(); };
        Unloaded += (_, _) => { Motion.EcoChanged -= UpdateSpin; AppPower.Changed -= OnPower; QualityChanged -= OnQuality; StopSpin(); };
        IsVisibleChanged += (_, _) => UpdateSpin();
    }

    public byte[]? SkinPng { get => (byte[]?)GetValue(SkinPngProperty); set => SetValue(SkinPngProperty, value); }
    public bool Slim { get => (bool)GetValue(SlimProperty); set => SetValue(SlimProperty, value); }
    public string? PoseId { get => (string?)GetValue(PoseIdProperty); set => SetValue(PoseIdProperty, value); }
    public bool ShowOverlay { get => (bool)GetValue(ShowOverlayProperty); set => SetValue(ShowOverlayProperty, value); }
    public bool AutoSpin { get => (bool)GetValue(AutoSpinProperty); set => SetValue(AutoSpinProperty, value); }
    public SkinModel? Model { get => (SkinModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        _last = e.GetPosition(this);
        StopSpin();
        CaptureMouse();
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _yaw += (p.X - _last.X) * 0.8;
        _pitch = Math.Clamp(_pitch + (p.Y - _last.Y) * 0.4, -35, 35);
        _last = p;
        Render();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
        UpdateSpin();
        if (!_spinning) Sharpen();
        base.OnMouseLeftButtonUp(e);
    }

    /// <summary>Лаунчер свёрнут: останавливаем вращение и отдаём буферы картинки; при возврате перерисовываем.</summary>
    private void OnPower()
    {
        if (AppPower.Suspended)
        {
            StopSpin();
            _image.Source = null;
            _bitmap = null;
            _pixels = [];
            _depth = [];
        }
        else
        {
            UpdateSpin();
            Render();
        }
    }

    private void UpdateSpin()
    {
        var should = IsVisible && IsLoaded && !Motion.Eco && !_dragging && AutoSpin && !AppPower.Suspended;
        if (should == _spinning) return;
        if (should)
        {
            _spinning = true;
            RenderOptions.SetBitmapScalingMode(_image, MaxGraphics ? BitmapScalingMode.Fant : BitmapScalingMode.Linear);
            _lastFrame = _clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnFrame;
        }
        else StopSpin();
    }

    private void StopSpin()
    {
        if (!_spinning) return;
        _spinning = false;
        CompositionTarget.Rendering -= OnFrame;
        Sharpen();
    }

    /// <summary>Модель остановилась: перерисовываем в полном размере окна и сглаживаем при масштабировании (Fant).</summary>
    private void Sharpen()
    {
        if (_dragging || !IsLoaded || AppPower.Suspended) return;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Fant);
        Dispatcher.BeginInvoke(Render, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastFrame;
        if (dt < 1.0 / 30) return; // для плавного вращения хватает 30 кадров в секунду
        _lastFrame = now;
        _yaw += dt * SpinDegreesPerSecond;
        Render();
    }

    // ------------------------------------------------------------ построение

    private void Rebuild()
    {
        var model = Model ?? PlayerModel.Create(Slim, IsLegacy(SkinPng), PlayerPoses.Find(PoseId), ShowOverlay);
        var texture = SkinImages.PadTo(
            SkinImages.Decode(model.EmbeddedTexture ?? SkinPng) ?? SkinImages.DefaultSkin(),
            (int)model.TextureWidth, (int)model.TextureHeight);
        var (pixels, pw, ph) = SkinImages.ToBgra(texture);

        // Центр модели по высоте — в точке вращения, чтобы она вращалась «на месте»; высота приводится к 34 единицам.
        var minY = model.Cubes.Min(c => Math.Min(c.From.Y, c.To.Y));
        var maxY = model.Cubes.Max(c => Math.Max(c.From.Y, c.To.Y));
        var scale = 34.0 / Math.Max(1, maxY - minY);
        var centerY = (minY + maxY) / 2;
        var turn = model.FaceTowardsViewer == CubeFace.North ? -1.0 : 1.0; // север — лицом к зрителю: поворот на 180° вокруг Y

        var quads = ModelGeometry.Build(model);
        long total = 0;
        foreach (var q in quads) total += (long)CellsX(q, pw) * CellsY(q, ph);
        var stride = total > MaxTexels ? (int)Math.Ceiling(Math.Sqrt((double)total / MaxTexels)) : 1; // очень подробные текстуры — крупнее блоки

        var list = new List<Texel>((int)Math.Min(total, MaxTexels));
        foreach (var quad in quads)
        {
            var bx = Math.Max(1, (CellsX(quad, pw) + stride - 1) / stride);
            var by = Math.Max(1, (CellsY(quad, ph) + stride - 1) / stride);
            var (tl, bl, tr) = (quad.Positions[0], quad.Positions[1], quad.Positions[3]);
            var (u1, v1) = quad.Uvs[0];
            var (u2, v2) = quad.Uvs[2];

            // Точка модели → пространство просмотра: сдвиг по высоте, масштаб, разворот лицом к зрителю.
            (double X, double Y, double Z) ToView(Vec3 p) => (p.X * scale * turn, (p.Y - centerY) * scale, p.Z * scale * turn);
            (double X, double Y, double Z) ToViewVector(Vec3 a, Vec3 b, int parts) => ((b.X - a.X) * scale * turn / parts, (b.Y - a.Y) * scale / parts, (b.Z - a.Z) * scale * turn / parts);

            var origin = ToView(tl);
            var right = ToViewVector(tl, tr, bx);
            var down = ToViewVector(tl, bl, by);
            var normal = (X: quad.Normal.X * turn, Y: quad.Normal.Y, Z: quad.Normal.Z * turn);

            for (var j = 0; j < by; j++)
                for (var i = 0; i < bx; i++)
                {
                    var cu = u1 + (u2 - u1) * (i + 0.5) / bx;
                    var cv = v1 + (v2 - v1) * (j + 0.5) / by;
                    var px = Math.Clamp((int)Math.Floor(cu * pw), 0, pw - 1);
                    var py = Math.Clamp((int)Math.Floor(cv * ph), 0, ph - 1);
                    var at = (py * pw + px) * 4;
                    if (pixels[at + 3] < 128) continue; // прозрачный пиксель не рисуется

                    list.Add(new Texel
                    {
                        X = (float)(origin.X + right.X * i + down.X * j),
                        Y = (float)(origin.Y + right.Y * i + down.Y * j),
                        Z = (float)(origin.Z + right.Z * i + down.Z * j),
                        Rx = (float)right.X, Ry = (float)right.Y, Rz = (float)right.Z,
                        Dx = (float)down.X, Dy = (float)down.Y, Dz = (float)down.Z,
                        Nx = (float)normal.X, Ny = (float)normal.Y, Nz = (float)normal.Z,
                        B = pixels[at], G = pixels[at + 1], R = pixels[at + 2]
                    });
                }
        }

        _texels = list.ToArray();
        Render();
    }

    private static int CellsX(ModelQuad q, int pixelWidth) => Math.Max(1, (int)Math.Round(Math.Abs(q.Uvs[2].U - q.Uvs[0].U) * pixelWidth));
    private static int CellsY(ModelQuad q, int pixelHeight) => Math.Max(1, (int)Math.Round(Math.Abs(q.Uvs[2].V - q.Uvs[0].V) * pixelHeight));

    // ------------------------------------------------------------ отрисовка

    /// <summary>Время последней отрисовки модели, мс (для вкладки разработчика).</summary>
    public static double LastRenderMs { get; private set; }

    /// <summary>Максимальная графика: модель всегда рисуется в полном размере окна, со сглаживанием.</summary>
    public static bool MaxGraphics { get; private set; }

    public static event Action? QualityChanged;

    public static void SetMaxGraphics(bool on)
    {
        if (MaxGraphics == on) return;
        MaxGraphics = on;
        QualityChanged?.Invoke();
    }

    private void OnQuality()
    {
        if (!_spinning) RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Fant);
        else RenderOptions.SetBitmapScalingMode(_image, MaxGraphics ? BitmapScalingMode.Fant : BitmapScalingMode.Linear);
        Render();
    }

    private void Render()
    {
        var started = Stopwatch.GetTimestamp();
        RenderCore();
        LastRenderMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void RenderCore()
    {
        if (_texels.Length == 0 || !IsLoaded || AppPower.Suspended) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var rawW = ActualWidth * dpi.DpiScaleX;
        var rawH = ActualHeight * dpi.DpiScaleY;
        var shrink = Math.Min(1.0, (MaxGraphics ? 4096 : _spinning || _dragging ? MaxMovingSide : MaxStillSide) / Math.Max(rawW, rawH));
        var w = (int)Math.Round(rawW * shrink);
        var h = (int)Math.Round(rawH * shrink);
        if (w < 8 || h < 8) return;

        if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
            _pixels = new int[w * h];
            _depth = new float[w * h];
            _image.Source = _bitmap;
        }
        Array.Clear(_pixels);
        Array.Fill(_depth, float.MaxValue);

        // Поворот: сначала вокруг Y (yaw), потом вокруг X (pitch) — как у модели на сцене.
        var (sy, cy) = Math.SinCos(_yaw * Math.PI / 180);
        var (sp, cp) = Math.SinCos(_pitch * Math.PI / 180);
        var unit = Math.Min(w / VisibleWidth, h / VisibleHeight); // пикселей на единицу модели на расстоянии CameraDistance
        var focus = (float)(unit * CameraDistance);
        var (cx, cyScreen) = (w / 2f, h / 2f);

        // Свет закреплён за камерой: как в прежней сцене — рассеянный 0,78 и направленный 0,38 слева-сверху-спереди.
        var (lx, ly, lz) = (0.4f, 0.6f, 1f);
        var ll = MathF.Sqrt(lx * lx + ly * ly + lz * lz);
        (lx, ly, lz) = (lx / ll, ly / ll, lz / ll);

        Span<float> sx = stackalloc float[4];
        Span<float> sy4 = stackalloc float[4];
        Span<float> cxs = stackalloc float[4];
        Span<float> cys = stackalloc float[4];
        Span<float> czs = stackalloc float[4];
        foreach (ref readonly var t in _texels.AsSpan())
        {
            // нормаль и центр пикселя после поворота
            var n = Rotate(t.Nx, t.Ny, t.Nz, sy, cy, sp, cp);
            var c = Rotate(t.X + (t.Rx + t.Dx) / 2, t.Y + (t.Ry + t.Dy) / 2, t.Z + (t.Rz + t.Dz) / 2, sy, cy, sp, cp);
            var depthCenter = (float)CameraDistance - c.Z;
            if (depthCenter < 5) continue;
            // пиксель, повёрнутый к камере тыльной стороной, не рисуется
            if (n.X * -c.X + n.Y * -c.Y + n.Z * depthCenter <= 0) continue;

            var p0 = Rotate(t.X, t.Y, t.Z, sy, cy, sp, cp);
            var r = Rotate(t.Rx, t.Ry, t.Rz, sy, cy, sp, cp);
            var d = Rotate(t.Dx, t.Dy, t.Dz, sy, cy, sp, cp);
            cxs[0] = p0.X; cys[0] = p0.Y; czs[0] = p0.Z;
            cxs[1] = p0.X + r.X; cys[1] = p0.Y + r.Y; czs[1] = p0.Z + r.Z;
            cxs[2] = p0.X + r.X + d.X; cys[2] = p0.Y + r.Y + d.Y; czs[2] = p0.Z + r.Z + d.Z;
            cxs[3] = p0.X + d.X; cys[3] = p0.Y + d.Y; czs[3] = p0.Z + d.Z;

            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (var k = 0; k < 4; k++)
            {
                var depth = (float)CameraDistance - czs[k];
                if (depth < 5) { minX = float.MaxValue; break; }
                sx[k] = cx + cxs[k] * focus / depth;
                sy4[k] = cyScreen - cys[k] * focus / depth;
                minX = MathF.Min(minX, sx[k]); maxX = MathF.Max(maxX, sx[k]);
                minY = MathF.Min(minY, sy4[k]); maxY = MathF.Max(maxY, sy4[k]);
            }
            if (minX == float.MaxValue) continue;

            var area = (sx[1] - sx[0]) * (sy4[3] - sy4[0]) - (sy4[1] - sy4[0]) * (sx[3] - sx[0]);
            if (MathF.Abs(area) < 1e-4f) continue;
            var sign = area > 0 ? 1f : -1f;

            var light = MathF.Min(1f, 0.78f + 0.38f * MathF.Max(0f, n.X * lx + n.Y * ly + n.Z * lz));
            var color = unchecked((int)0xFF000000)
                        | ((int)(t.R * light) << 16) | ((int)(t.G * light) << 8) | (int)(t.B * light);

            var x0 = Math.Max(0, (int)MathF.Floor(minX));
            var x1 = Math.Min(w - 1, (int)MathF.Ceiling(maxX));
            var y0 = Math.Max(0, (int)MathF.Floor(minY));
            var y1 = Math.Min(h - 1, (int)MathF.Ceiling(maxY));
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var (px, py) = (x + 0.5f, y + 0.5f);
                    if (Edge(sx[0], sy4[0], sx[1], sy4[1], px, py) * sign < 0 ||
                        Edge(sx[1], sy4[1], sx[2], sy4[2], px, py) * sign < 0 ||
                        Edge(sx[2], sy4[2], sx[3], sy4[3], px, py) * sign < 0 ||
                        Edge(sx[3], sy4[3], sx[0], sy4[0], px, py) * sign < 0) continue;
                    var index = y * w + x;
                    if (depthCenter >= _depth[index]) continue;
                    _depth[index] = depthCenter;
                    _pixels[index] = color;
                }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _pixels, w * 4, 0);
    }

    private static float Edge(float ax, float ay, float bx, float by, float px, float py) =>
        (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    private static (float X, float Y, float Z) Rotate(float x, float y, float z, double sinYaw, double cosYaw, double sinPitch, double cosPitch)
    {
        var x1 = x * cosYaw + z * sinYaw;
        var z1 = -x * sinYaw + z * cosYaw;
        var y2 = y * cosPitch - z1 * sinPitch;
        var z2 = y * sinPitch + z1 * cosPitch;
        return ((float)x1, (float)y2, (float)z2);
    }

    private static bool IsLegacy(byte[]? png) =>
        png is { Length: >= 24 } && ((png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]) == 32;
}
