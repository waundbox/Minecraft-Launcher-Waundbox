using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Launcher.UI.Controls;

/// <summary>
/// Круглая палитра: диск «оттенок + насыщенность» и полоса яркости под ним.
/// Значение — строка #RRGGBB (двусторонняя привязка). Клик или протяжка по диску/полосе меняют цвет сразу.
/// </summary>
public sealed class ColorWheel : FrameworkElement
{
    private const int DiscPixels = 256;
    private const double BarHeight = 16;
    private const double Gap = 14;

    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorWheel),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((ColorWheel)d).OnHexChanged((string?)e.NewValue)));

    private static BitmapSource? _disc;

    private double _h;      // 0..360
    private double _s;      // 0..1
    private double _v = 1;  // 0..1
    private bool _changing;
    private Drag _drag;

    private enum Drag { None, Disc, Bar }

    public ColorWheel()
    {
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        Focusable = false;
        OnHexChanged("#FFFFFF");
    }

    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 220 : Math.Min(available.Width, 260);
        return new Size(width, width + Gap + BarHeight);
    }

    private double DiscSize => Math.Max(10, Math.Min(ActualWidth, ActualHeight - Gap - BarHeight));

    private Rect DiscRect => new((ActualWidth - DiscSize) / 2, 0, DiscSize, DiscSize);
    private Rect BarRect => new(DiscRect.X, DiscSize + Gap, DiscSize, BarHeight);

    // ---------- рисование ----------

    private static BitmapSource Disc()
    {
        if (_disc is not null) return _disc;
        var size = DiscPixels;
        var pixels = new byte[size * size * 4];
        var radius = size / 2.0;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x + 0.5 - radius;
            var dy = y + 0.5 - radius;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            var alpha = Math.Clamp(radius - dist + 0.5, 0, 1);
            if (alpha <= 0) continue;
            var hue = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (hue < 0) hue += 360;
            var (r, g, b) = FromHsv(hue, Math.Min(dist / radius, 1), 1);
            var i = (y * size + x) * 4;
            pixels[i] = (byte)(b * alpha);
            pixels[i + 1] = (byte)(g * alpha);
            pixels[i + 2] = (byte)(r * alpha);
            pixels[i + 3] = (byte)(alpha * 255);
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return _disc = bitmap;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var disc = DiscRect;
        dc.DrawImage(Disc(), disc);

        // затемнение диска по яркости
        if (_v < 1)
        {
            var shade = new SolidColorBrush(Color.FromArgb((byte)((1 - _v) * 255), 0, 0, 0));
            dc.DrawEllipse(shade, null, new Point(disc.X + disc.Width / 2, disc.Y + disc.Height / 2), disc.Width / 2, disc.Height / 2);
        }

        var ring = new Pen(new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)), 1);
        dc.DrawEllipse(null, ring, new Point(disc.X + disc.Width / 2, disc.Y + disc.Height / 2), disc.Width / 2, disc.Height / 2);

        // маркер выбранного цвета
        var radius = disc.Width / 2;
        var angle = _h * Math.PI / 180;
        var marker = new Point(disc.X + radius + Math.Cos(angle) * _s * radius, disc.Y + radius + Math.Sin(angle) * _s * radius);
        var current = CurrentColor();
        dc.DrawEllipse(new SolidColorBrush(current), new Pen(Brushes.Black, 3), marker, 8, 8);
        dc.DrawEllipse(null, new Pen(Brushes.White, 1.8), marker, 8, 8);

        // полоса яркости
        var bar = BarRect;
        var (fr, fg, fb) = FromHsv(_h, _s, 1);
        var gradient = new LinearGradientBrush(Colors.Black, Color.FromRgb(fr, fg, fb), 0);
        dc.DrawRoundedRectangle(gradient, ring, bar, BarHeight / 2, BarHeight / 2);
        var thumbX = bar.X + _v * bar.Width;
        var thumb = new Point(Math.Clamp(thumbX, bar.X + BarHeight / 2, bar.Right - BarHeight / 2), bar.Y + BarHeight / 2);
        dc.DrawEllipse(new SolidColorBrush(current), new Pen(Brushes.Black, 3), thumb, BarHeight / 2 + 2, BarHeight / 2 + 2);
        dc.DrawEllipse(null, new Pen(Brushes.White, 1.8), thumb, BarHeight / 2 + 2, BarHeight / 2 + 2);
    }

    // ---------- мышь ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        var discCenter = new Point(DiscRect.X + DiscRect.Width / 2, DiscRect.Y + DiscRect.Height / 2);
        if (p.Y >= DiscRect.Bottom + Gap / 2) _drag = Drag.Bar;
        else if ((p - discCenter).Length <= DiscRect.Width / 2 + 6) _drag = Drag.Disc;
        else return;
        CaptureMouse();
        Apply(p);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag != Drag.None && e.LeftButton == MouseButtonState.Pressed) Apply(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _drag = Drag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void Apply(Point p)
    {
        if (_drag == Drag.Disc)
        {
            var disc = DiscRect;
            var dx = p.X - (disc.X + disc.Width / 2);
            var dy = p.Y - (disc.Y + disc.Height / 2);
            var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            _h = angle < 0 ? angle + 360 : angle;
            _s = Math.Min(Math.Sqrt(dx * dx + dy * dy) / (disc.Width / 2), 1);
        }
        else if (_drag == Drag.Bar)
        {
            var bar = BarRect;
            _v = Math.Clamp((p.X - bar.X) / bar.Width, 0, 1);
        }
        Publish();
    }

    private void Publish()
    {
        var c = CurrentColor();
        _changing = true;
        try { Hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
        finally { _changing = false; }
        InvalidateVisual();
    }

    // ---------- цвет ----------

    private void OnHexChanged(string? hex)
    {
        if (_changing) return;
        if (!TryParse(hex, out var r, out var g, out var b)) return;
        // если запись уже означает то, что показано, оттенок не трогаем (иначе при чёрном/сером он «сбрасывался» бы)
        var shown = CurrentColor();
        if (shown.R == r && shown.G == g && shown.B == b) { InvalidateVisual(); return; }
        (_h, _s, _v) = ToHsv(r, g, b, _h);
        InvalidateVisual();
    }

    private Color CurrentColor()
    {
        var (r, g, b) = FromHsv(_h, _s, _v);
        return Color.FromRgb(r, g, b);
    }

    private static bool TryParse(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (hex is null) return false;
        var t = hex.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return false;
        r = (byte)(n >> 16); g = (byte)(n >> 8); b = (byte)n;
        return true;
    }

    private static (byte r, byte g, byte b) FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        double r, g, b;
        switch ((int)(h / 60) % 6)
        {
            case 0: (r, g, b) = (c, x, 0); break;
            case 1: (r, g, b) = (x, c, 0); break;
            case 2: (r, g, b) = (0, c, x); break;
            case 3: (r, g, b) = (0, x, c); break;
            case 4: (r, g, b) = (x, 0, c); break;
            default: (r, g, b) = (c, 0, x); break;
        }
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static (double h, double s, double v) ToHsv(byte rb, byte gb, byte bb, double previousHue)
    {
        double r = rb / 255.0, g = gb / 255.0, b = bb / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h;
        if (d < 1e-9) h = previousHue;
        else if (max == r) h = 60 * (((g - b) / d % 6 + 6) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        return (h, max < 1e-9 ? 0 : d / max, max);
    }
}
