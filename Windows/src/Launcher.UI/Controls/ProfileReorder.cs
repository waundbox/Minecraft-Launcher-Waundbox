using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Launcher.UI.Services;
using Launcher.UI.ViewModels;

namespace Launcher.UI.Controls;

/// <summary>
/// Перетаскивание профилей в списке (главная и страница профилей) в режиме правки редактора.
/// Список один на обе страницы, поэтому порядок у них общий. Сам перенос выполняет <see cref="Move"/>.
/// </summary>
public static class ProfileReorder
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ProfileReorder), new PropertyMetadata(false, OnEnabled));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    /// <summary>Перенос профиля на позицию; задаётся при запуске приложения.</summary>
    internal static Action<ProfileItem, int>? Move { get; set; }

    private static readonly MouseButtonEventHandler DownHandler = OnDown;
    private static readonly MouseEventHandler MoveHandler = OnMove;
    private static readonly MouseButtonEventHandler UpHandler = OnUp;

    private static ProfileItem? _dragged;

    private static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items) return;
        if ((bool)e.NewValue)
        {
            // handledEventsToo: обёртка редактора помечает нажатие обработанным, список всё равно должен его увидеть
            items.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, DownHandler, true);
            items.AddHandler(UIElement.PreviewMouseMoveEvent, MoveHandler, true);
            items.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, UpHandler, true);
            items.LostMouseCapture += OnLost;
        }
        else
        {
            items.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, DownHandler);
            items.RemoveHandler(UIElement.PreviewMouseMoveEvent, MoveHandler);
            items.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, UpHandler);
            items.LostMouseCapture -= OnLost;
        }
    }

    private static ProfileItem? ItemAt(ItemsControl items, DependencyObject? node)
    {
        // OriginalSource может быть не визуальным элементом (текст): поднимаемся до визуального
        while (node is not null && node is not Visual and not System.Windows.Media.Media3D.Visual3D)
            node = node is FrameworkContentElement fce ? fce.Parent ?? fce.TemplatedParent : LogicalTreeHelper.GetParent(node);
        if (node is null) return null;
        var container = ItemsControl.ContainerFromElement(items, node);
        return container is null ? null : items.ItemContainerGenerator.ItemFromContainer(container) as ProfileItem;
    }

    /// <summary>Нажатие пришлось на профиль в списке с включённым перетаскиванием.</summary>
    internal static bool IsOnProfile(DependencyObject? node)
    {
        var start = node;
        while (node is not null)
        {
            if (node is ItemsControl items && GetEnabled(items)) return ItemAt(items, start) is not null;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragged = null;
        if (!LayoutState.Editing || sender is not ItemsControl items) return;
        _dragged = ItemAt(items, e.OriginalSource as DependencyObject);
        if (_dragged is not null) items.CaptureMouse(); // пока кнопка зажата, события идут списку, даже если курсор вышел за карточку
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragged is null || sender is not ItemsControl items) return;
        if (e.LeftButton != MouseButtonState.Pressed || !LayoutState.Editing) { End(items); return; }

        var target = ItemAt(items, items.InputHitTest(e.GetPosition(items)) as DependencyObject);
        if (target is null || ReferenceEquals(target, _dragged)) return;
        var index = items.Items.IndexOf(target);
        if (index >= 0) Move?.Invoke(_dragged, index);
    }

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ItemsControl items) End(items);
    }

    private static void OnLost(object sender, MouseEventArgs e) => _dragged = null;

    private static void End(ItemsControl items)
    {
        _dragged = null;
        if (items.IsMouseCaptured) items.ReleaseMouseCapture();
    }
}
