using System.Windows;
using System.Windows.Media;
using Launcher.UI.Services;

namespace Launcher.UI.Controls;

/// <summary>Сетка редактора интерфейса: видна в режиме правки, когда включена привязка к сетке.</summary>
public sealed class EditorGrid : FrameworkElement
{
    public EditorGrid()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            LayoutState.GridChanged += Refresh;
            LayoutState.EditingChanged += Refresh;
        };
        Unloaded += (_, _) =>
        {
            LayoutState.GridChanged -= Refresh;
            LayoutState.EditingChanged -= Refresh;
        };
    }

    private void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var step = LayoutState.SnapStep;
        if (!LayoutState.Editing || step <= 0) return;
        var cell = step < 20 ? step * 2 : step; // слишком частые линии не рисуем
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), 1);
        pen.Freeze();
        for (double x = cell; x < ActualWidth; x += cell) dc.DrawLine(pen, new Point(x, 0), new Point(x, ActualHeight));
        for (double y = cell; y < ActualHeight; y += cell) dc.DrawLine(pen, new Point(0, y), new Point(ActualWidth, y));
    }
}
