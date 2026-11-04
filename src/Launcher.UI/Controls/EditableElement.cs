using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        _scale.ScaleX = _scale.ScaleY = layout.Scale;
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
    }

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

        LayoutState.Select(Id);
        LayoutState.Checkpoint(force: true);
        var start = LayoutState.Get(Id);
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
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }

        var now = e.GetPosition(VisualParent as IInputElement ?? this);
        var dx = now.X - _last.X;
        var dy = now.Y - _last.Y;
        _last = now;
        _rawX += dx;
        _rawY += dy;
        var step = LayoutState.SnapStep;
        double Snap(double v) => step > 0 ? Math.Round(v / step) * step : v;
        LayoutState.Set(Id, LayoutState.Get(Id) with { X = Snap(_rawX), Y = Snap(_rawY) });
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
    }

    private void EndDrag()
    {
        _dragging = false;
        ReleaseMouseCapture();
    }
}
