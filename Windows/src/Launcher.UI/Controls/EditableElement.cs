using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Launcher.Core.Configuration;
using Launcher.UI.Services;

namespace Launcher.UI.Controls;

/// <summary>
/// Обёртка элемента главной страницы для редактора интерфейса: применяет сохранённые положение, масштаб,
/// прозрачность, видимость и скругление; в режиме правки элемент можно перетаскивать мышью.
/// Что делает кнопка внутри, обёртка не меняет.
/// </summary>
public sealed class EditableElement : Decorator
{
    public static readonly DependencyProperty IdProperty =
        DependencyProperty.Register(nameof(Id), typeof(string), typeof(EditableElement),
            new PropertyMetadata("", (d, _) => ((EditableElement)d).Apply()));

    public string Id
    {
        get => (string)GetValue(IdProperty);
        set => SetValue(IdProperty, value);
    }

    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _move = new();
    private readonly Dictionary<DependencyObject, CornerRadius> _corners = new();
    private Point _last;
    private bool _dragging;
    private double _rawX, _rawY;

    public EditableElement()
    {
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_move);
        RenderTransform = group;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Loaded += (_, _) =>
        {
            LayoutState.Changed += OnChanged;
            LayoutState.SelectionChanged += Apply;
            LayoutState.EditingChanged += Apply;
            Dispatcher.BeginInvoke(Apply, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        Unloaded += (_, _) =>
        {
            LayoutState.Changed -= OnChanged;
            LayoutState.SelectionChanged -= Apply;
            LayoutState.EditingChanged -= Apply;
        };
    }

    private void OnChanged(string? id)
    {
        if (id is null || string.Equals(id, Id, StringComparison.OrdinalIgnoreCase)) Apply();
    }

    private void Apply()
    {
        if (string.IsNullOrEmpty(Id)) return;
        var layout = LayoutState.Get(Id);
        var editing = LayoutState.Editing;

        _move.X = layout.X;
        _move.Y = layout.Y;
        _scale.ScaleX = layout.Scale * layout.StretchX;
        _scale.ScaleY = layout.Scale * layout.StretchY;
        Visibility = layout.Visible || editing ? Visibility.Visible : Visibility.Collapsed;
        // в режиме правки скрытый или прозрачный элемент остаётся виден бледным, чтобы его можно было найти
        Opacity = !layout.Visible ? 0.3 : Math.Max(layout.Opacity, editing ? 0.25 : 0);
        Panel.SetZIndex(this, layout.Z);
        ApplyCorner(layout.Corner);
        InvalidateVisual();
    }

    // ----- скругление -----

    private void ApplyCorner(double? corner)
    {
        if (corner is null)
        {
            foreach (var (d, original) in _corners)
            {
                if (d is Border border) border.CornerRadius = original;
                else if (d is Button button) Ui.SetCornerRadius(button, original);
            }
            _corners.Clear();
            return;
        }
        if (Child is not null) VisitCorners(Child, corner.Value);
    }

    private void VisitCorners(DependencyObject node, double corner)
    {
        if (node is EditableElement other && !ReferenceEquals(other, this)) return; // у вложенных своё скругление

        switch (node)
        {
            case Button button:
                if (!_corners.ContainsKey(button)) _corners[button] = Ui.GetCornerRadius(button);
                Ui.SetCornerRadius(button, new CornerRadius(corner));
                return;
            case Control:
                return; // списки, переключатели и т. п. не трогаем
            case Border border:
                if (border.CornerRadius != default || _corners.ContainsKey(border))
                {
                    if (!_corners.ContainsKey(border)) _corners[border] = border.CornerRadius;
                    border.CornerRadius = new CornerRadius(corner + (border.Name == "Glow" ? 6 : 0));
                }
                break;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            VisitCorners(VisualTreeHelper.GetChild(node, i), corner);
    }

    // ----- рамка в режиме правки -----

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!LayoutState.Editing || RenderSize.Width < 2 || RenderSize.Height < 2) return;

        var selected = string.Equals(LayoutState.Selected, Id, StringComparison.OrdinalIgnoreCase);
        var brush = Application.Current.TryFindResource("AccentBrush") as Brush ?? Brushes.DeepSkyBlue;
        var pen = new Pen(brush, selected ? 2 : 1) { DashStyle = selected ? DashStyles.Solid : DashStyles.Dash };
        Brush? fill = null;
        if (selected)
        {
            fill = brush.CloneCurrentValue();
            fill.Opacity = 0.12;
        }
        dc.DrawRoundedRectangle(fill, pen, new Rect(-4, -4, RenderSize.Width + 8, RenderSize.Height + 8), 8, 8);
        if (!selected) return;
        // ручки на серединах сторон: за каждую можно тянуть только свою сторону
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        var grip = new Pen(Brushes.White, 1.5);
        foreach (var c in new[] { new Point(-4, h / 2), new Point(w + 4, h / 2), new Point(w / 2, -4), new Point(w / 2, h + 4) })
            dc.DrawRoundedRectangle(brush, grip, new Rect(c.X - 5, c.Y - 5, 10, 10), 3, 3);
    }

    private enum Edge { None, Left, Right, Top, Bottom }

    private Edge EdgeAt(Point p)
    {
        const double zone = 9;
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (Math.Abs(p.Y - h / 2) <= Math.Max(zone, h * 0.2))
        {
            if (p.X >= -zone - 4 && p.X <= zone) return Edge.Left;
            if (p.X >= w - zone && p.X <= w + zone + 4) return Edge.Right;
        }
        if (Math.Abs(p.X - w / 2) <= Math.Max(zone, w * 0.2))
        {
            if (p.Y >= -zone - 4 && p.Y <= zone) return Edge.Top;
            if (p.Y >= h - zone && p.Y <= h + zone + 4) return Edge.Bottom;
        }
        return Edge.None;
    }

    private Edge _resizing;
    private double _rawW, _rawH;

    // ----- перетаскивание -----

    private static EditableElement? Innermost(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is EditableElement element) return element;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (!LayoutState.Editing || string.IsNullOrEmpty(Id)) return;
        if (!ReferenceEquals(Innermost(e.OriginalSource as DependencyObject), this)) return;

        if (ProfileReorder.IsOnProfile(e.OriginalSource as DependencyObject))
        {
            e.Handled = true; // на профиле: кнопки не нажимаются и элемент не двигается, профиль таскает ProfileReorder
            return;
        }

        var wasSelected = string.Equals(LayoutState.Selected, Id, StringComparison.OrdinalIgnoreCase);
        var edge = wasSelected ? EdgeAt(e.GetPosition(this)) : Edge.None;
        LayoutState.Select(Id);
        LayoutState.Checkpoint(force: true);
        var start = LayoutState.Get(Id);
        if (edge != Edge.None)
        {
            _resizing = edge;
            _rawW = RenderSize.Width * start.Scale * start.StretchX; // видимые размеры в координатах родителя
            _rawH = RenderSize.Height * start.Scale * start.StretchY;
            _rawX = start.X;
            _rawY = start.Y;
            _dragging = true;
            _last = e.GetPosition(VisualParent as IInputElement ?? this);
            CaptureMouse();
            e.Handled = true;
            return;
        }
        _rawX = start.X;
        _rawY = start.Y;
        _dragging = true;
        _last = e.GetPosition(VisualParent as IInputElement ?? this);
        CaptureMouse();
        e.Handled = true; // в режиме правки кнопки внутри не нажимаются
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            var selected = LayoutState.Editing && string.Equals(LayoutState.Selected, Id, StringComparison.OrdinalIgnoreCase);
            var edge = selected ? EdgeAt(e.GetPosition(this)) : Edge.None;
            Cursor = edge is Edge.Left or Edge.Right ? Cursors.SizeWE : edge is Edge.Top or Edge.Bottom ? Cursors.SizeNS : null;
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }

        var now = e.GetPosition(VisualParent as IInputElement ?? this);
        var dx = now.X - _last.X;
        var dy = now.Y - _last.Y;
        _last = now;
        if (_resizing != Edge.None)
        {
            ResizeBy(dx, dy);
            return;
        }
        _rawX += dx;
        _rawY += dy;
        var step = LayoutState.SnapStep;
        double Snap(double v) => step > 0 ? Math.Round(v / step) * step : v;
        LayoutState.Set(Id, LayoutState.Get(Id) with { X = Snap(_rawX), Y = Snap(_rawY) });
    }

    /// <summary>Тянет одну сторону: противоположная остаётся на месте, поэтому центр сдвигается на половину изменения.</summary>
    private void ResizeBy(double dx, double dy)
    {
        var l = LayoutState.Get(Id);
        var step = LayoutState.SnapStep;
        double Snap(double v) => step > 0 ? Math.Round(v / step) * step : v;
        double baseW = Math.Max(1, RenderSize.Width * l.Scale), baseH = Math.Max(1, RenderSize.Height * l.Scale);
        switch (_resizing)
        {
            case Edge.Right: _rawW += dx; _rawX += dx / 2; break;
            case Edge.Left: _rawW -= dx; _rawX += dx / 2; break;
            case Edge.Bottom: _rawH += dy; _rawY += dy / 2; break;
            case Edge.Top: _rawH -= dy; _rawY += dy / 2; break;
        }
        var horizontal = _resizing is Edge.Left or Edge.Right;
        var next = horizontal
            ? l with { StretchX = Math.Clamp(_rawW / baseW, ElementLayout.MinScale, ElementLayout.MaxScale), X = Snap(_rawX) }
            : l with { StretchY = Math.Clamp(_rawH / baseH, ElementLayout.MinScale, ElementLayout.MaxScale), Y = Snap(_rawY) };
        LayoutState.Set(Id, next);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        EndDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        _resizing = Edge.None;
    }

    private void EndDrag()
    {
        _dragging = false;
        _resizing = Edge.None;
        ReleaseMouseCapture();
    }
}
