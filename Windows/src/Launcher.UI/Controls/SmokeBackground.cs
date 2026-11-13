using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Launcher.UI.Controls;

/// <summary>
/// Фон «дым»: несколько мягких радиальных пятен, которые медленно плывут. Каждое пятно — отдельный элемент с
/// кэшем в виде картинки, поэтому движение — это перемещение готовой текстуры видеокартой, без перерисовки.
/// Анимацию можно ставить на паузу (<see cref="IsAnimated"/>), пауза не расходует процессор.
/// </summary>
public sealed class SmokeBackground : Grid
{
    public static readonly DependencyProperty IsAnimatedProperty =
        DependencyProperty.Register(nameof(IsAnimated), typeof(bool), typeof(SmokeBackground),
            new PropertyMetadata(true, (d, _) => ((SmokeBackground)d).ApplyState()));

    public bool IsAnimated
    {
        get => (bool)GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    private sealed record BlobSpec(double X, double Y, double Size, byte Alpha, Color Tint, double DriftX, double DriftY, double Seconds);

    private BlobSpec[] _specs = CreateSpecs(isLight: false, fog: null);

    private static BlobSpec[] CreateSpecs(bool isLight, Color? fog)
    {
        if (fog is { } f)
        {
            // обычная тема: небольшой ровный туман заданного цвета (в центре пятна цвет виден полностью)
            var a = isLight ? (byte)0xB0 : (byte)0xFF;
            return new BlobSpec[]
            {
                new(0.18, 0.25, 0.80, a, f, 0.10, 0.07, 26),
                new(0.85, 0.15, 0.65, a, f, -0.09, 0.09, 33),
                new(0.55, 0.85, 0.90, a, f, -0.08, -0.06, 29)
            };
        }

        // основной цвет на фон не влияет: в тёмной теме «дым» светло-серый, в светлой — белый
        var soft = Color.FromRgb(0xFF, 0xFF, 0xFF);
        var cool = isLight ? soft : Color.FromRgb(0xD8, 0xDC, 0xE0);
        var grey = isLight ? Color.FromRgb(0xE6, 0xE8, 0xEE) : Color.FromRgb(0xC8, 0xC8, 0xD0);
        return new BlobSpec[]
        {
            new(0.15, 0.20, 0.75, 0x38, soft, 0.10, 0.07, 26),
            new(0.80, 0.10, 0.65, 0x2C, cool, -0.09, 0.09, 33),
            new(0.55, 0.75, 0.85, 0x30, soft, -0.08, -0.06, 29),
            new(0.05, 0.85, 0.60, 0x22, grey, 0.09, -0.08, 37),
            new(0.95, 0.70, 0.55, 0x26, grey, -0.07, 0.05, 31)
        };
    }

    private readonly Border _vignette = new() { IsHitTestVisible = false };

    /// <summary>Подбирает фон и цвет «дыма» под тему и основной цвет.</summary>
    public void ApplyTheme(Color background, bool isLight, Color accent, Color? fog = null)
    {
        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

        var top = isLight ? Mix(background, Colors.White, 0.5) : Mix(background, Colors.White, 0.04);
        var bottom = isLight ? Mix(background, Colors.Black, 0.05) : Mix(background, Colors.Black, 0.35);
        var gradient = new LinearGradientBrush(top, bottom, 90);
        gradient.Freeze();
        Background = gradient;

        var edge = isLight ? Mix(background, Colors.Black, 0.35) : Colors.Black;
        var vignette = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, edge.R, edge.G, edge.B), 0.55),
                new GradientStop(Color.FromArgb(isLight ? (byte)0x38 : (byte)0xA0, edge.R, edge.G, edge.B), 1.0)
            }
        };
        vignette.Freeze();
        _vignette.Background = vignette;

        // пересобираем пятна под новые цвета
        _specs = CreateSpecs(isLight, fog);
        StopClocks();
        _canvas.Children.Clear();
        _blobs.Clear();
        _built = false;
        Layout();
        ApplyState();
    }

    private readonly Canvas _canvas = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly List<(FrameworkElement Element, TranslateTransform Move, ScaleTransform Scale, BlobSpec Spec)> _blobs = new();
    private readonly List<AnimationClock> _clocks = new();
    private bool _built;

    public SmokeBackground()
    {
        IsHitTestVisible = false;
        Background = new LinearGradientBrush(Color.FromRgb(0x10, 0x10, 0x13), Color.FromRgb(0x07, 0x07, 0x09), 90);

        Children.Add(_canvas);
        _vignette.Background = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.55),
                new GradientStop(Color.FromArgb(0xA0, 0, 0, 0), 1.0)
            }
        };
        Children.Add(_vignette);

        SizeChanged += (_, _) => Layout();
        IsVisibleChanged += (_, _) => ApplyState();
        Loaded += (_, _) => ApplyState();
        AppPower.Changed += OnPower;
    }

    /// <summary>Лаунчер свёрнут: пятна дыма удаляются целиком (и их кэш-картинки), при возврате создаются заново.</summary>
    private void OnPower()
    {
        if (AppPower.Suspended)
        {
            StopClocks();
            _canvas.Children.Clear();
            _blobs.Clear();
            _built = false;
        }
        else Layout();
        ApplyState();
    }

    private void Build()
    {
        if (_built) return;
        _built = true;

        foreach (var spec in _specs)
        {
            var brush = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(spec.Alpha, spec.Tint.R, spec.Tint.G, spec.Tint.B), 0.0),
                    new GradientStop(Color.FromArgb((byte)(spec.Alpha / 3), spec.Tint.R, spec.Tint.G, spec.Tint.B), 0.55),
                    new GradientStop(Color.FromArgb(0, spec.Tint.R, spec.Tint.G, spec.Tint.B), 1.0)
                }
            };
            brush.Freeze();

            var move = new TranslateTransform();
            var scale = new ScaleTransform(1, 1);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(move);

            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Fill = brush,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = group,
                // мягкое пятно не требует высокого разрешения: половина масштаба экономит память видеокарты
                CacheMode = new BitmapCache(0.5)
            };
            _canvas.Children.Add(ellipse);
            _blobs.Add((ellipse, move, scale, spec));
        }
    }

    private void Layout()
    {
        if (ActualWidth < 1 || ActualHeight < 1 || AppPower.Suspended) return;
        Build();
        foreach (var (element, _, _, spec) in _blobs)
        {
            var size = Math.Max(ActualWidth, ActualHeight) * spec.Size;
            element.Width = size;
            element.Height = size;
            Canvas.SetLeft(element, ActualWidth * spec.X - size / 2);
            Canvas.SetTop(element, ActualHeight * spec.Y - size / 2);
        }

        // амплитуда движения зависит от размера окна
        if (_clocks.Count > 0)
        {
            StopClocks();
            ApplyState();
        }
    }

    private void ApplyState()
    {
        var run = IsAnimated && IsVisible && !Motion.Eco && ActualWidth > 1 && !AppPower.Suspended;
        if (!run)
        {
            foreach (var clock in _clocks) clock.Controller?.Pause();
            return;
        }

        if (_clocks.Count == 0) StartClocks();
        foreach (var clock in _clocks) clock.Controller?.Resume();
    }

    private void StartClocks()
    {
        Build();
        foreach (var (_, move, scale, spec) in _blobs)
        {
            var seconds = TimeSpan.FromSeconds(spec.Seconds);
            AnimationClock Clock(double to, TimeSpan duration)
            {
                var animation = new DoubleAnimation(0, to, duration)
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                return animation.CreateClock();
            }

            var cx = Clock(spec.DriftX * ActualWidth, seconds);
            var cy = Clock(spec.DriftY * ActualHeight, seconds * 1.17);
            var cs = new DoubleAnimation(0.9, 1.18, seconds * 0.8)
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            }.CreateClock();

            move.ApplyAnimationClock(TranslateTransform.XProperty, cx);
            move.ApplyAnimationClock(TranslateTransform.YProperty, cy);
            scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, cs);
            scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, cs);
            _clocks.Add(cx);
            _clocks.Add(cy);
            _clocks.Add(cs);
        }
    }

    private void StopClocks()
    {
        foreach (var (_, move, scale, _) in _blobs)
        {
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        _clocks.Clear();
    }
}
