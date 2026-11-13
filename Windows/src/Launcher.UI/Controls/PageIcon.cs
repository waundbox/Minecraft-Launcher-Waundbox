using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Launcher.UI.Controls;

/// <summary>
/// Значок вкладки: свой рисунок (контуры SVG), а если его нет, символ шрифта Segoe MDL2 Assets.
/// Цвет берёт у кнопки вкладки, поэтому подстраивается под тему и выбранное состояние.
/// </summary>
public sealed class PageIcon : Grid
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(PageIcon), new PropertyMetadata("", (d, _) => ((PageIcon)d).Refresh()));

    public static readonly DependencyProperty IconDataProperty =
        DependencyProperty.Register(nameof(IconData), typeof(string), typeof(PageIcon), new PropertyMetadata(null, (d, _) => ((PageIcon)d).Refresh()));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(PageIcon), new PropertyMetadata(16.0, (d, _) => ((PageIcon)d).Refresh()));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string? IconData { get => (string?)GetValue(IconDataProperty); set => SetValue(IconDataProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private readonly TextBlock _text = new();
    private readonly System.Windows.Shapes.Path _path = new()
    {
        Stretch = Stretch.Uniform,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new ScaleTransform(1, -1), // контуры SVG нарисованы вверх ногами
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    public PageIcon()
    {
        _text.SetResourceReference(StyleProperty, "Glyph");
        _text.TextAlignment = TextAlignment.Center;
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        _path.SetBinding(Shape.FillProperty, new Binding
        {
            Path = new PropertyPath(System.Windows.Documents.TextElement.ForegroundProperty),
            RelativeSource = RelativeSource.Self
        });
        Children.Add(_text);
        Children.Add(_path);
        VerticalAlignment = VerticalAlignment.Center;
        Refresh();
    }

    private void Refresh()
    {
        var hasPath = false;
        if (!string.IsNullOrWhiteSpace(IconData))
        {
            try
            {
                var geometry = Geometry.Parse(IconData);
                geometry.Freeze();
                _path.Data = geometry;
                hasPath = true;
            }
            catch (FormatException)
            {
                // испорченные данные значка — запасной символ
            }
        }
        _path.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        _text.Visibility = hasPath ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = Glyph;
        _text.FontSize = Size;
        _path.Width = _path.Height = Size * 1.05;
    }
}
