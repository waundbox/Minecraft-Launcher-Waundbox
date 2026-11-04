using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Launcher.UI.ViewModels;

namespace Launcher.UI.Views;

public partial class ConsoleView : UserControl
{
    private ConsoleViewModel? _vm;
    private ScrollViewer? _scroll;
    private bool _programmatic;
    private DateTime _popupClosedAt = DateTime.MinValue;

    public ConsoleView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) => Attach();
    }

    private void Attach()
    {
        if (DataContext is not ConsoleViewModel vm || ReferenceEquals(vm, _vm)) return;
        Detach();
        _vm = vm;
        vm.Flushed += ScrollToEnd;
    }

    private void Detach()
    {
        if (_vm is not null) _vm.Flushed -= ScrollToEnd;
        _vm = null;
    }

    private void OnLinesKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) return;
        if (e.Key == System.Windows.Input.Key.C) { CopySelected(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.A) { Lines.SelectAll(); e.Handled = true; }
    }

    /// <summary>Пока пользователь выделяет текст, автопрокрутка отключается: иначе список «убегает» из-под курсора.</summary>
    private void OnLinesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null && Lines.SelectedItems.Count > 0) _vm.AutoScroll = false;
    }

    private void OnCopySelected(object sender, RoutedEventArgs e) => CopySelected();
    private void OnSelectAll(object sender, RoutedEventArgs e) => Lines.SelectAll();
    private void OnCopyAll(object sender, RoutedEventArgs e) => _vm?.CopyCommand.Execute(null);

    private void CopySelected()
    {
        if (_vm is null || Lines.SelectedItems.Count == 0) return;
        // порядок строк — как в консоли, а не как выделяли
        var chosen = Lines.SelectedItems.Cast<ConsoleLine>().ToHashSet();
        _vm.CopyLines(_vm.Lines.Where(chosen.Contains));
    }

    // ---------- выбор профиля ----------

    private void OnSessionButtonClick(object sender, RoutedEventArgs e)
    {
        // клик по кнопке при открытом списке сначала закрывает его (StaysOpen=False), второй раз не открываем
        if ((DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 250) return;
        SessionPopup.IsOpen = true;
    }

    private void OnSessionPopupClosed(object? sender, EventArgs e) => _popupClosedAt = DateTime.UtcNow;
    private void OnSessionPicked(object sender, SelectionChangedEventArgs e) => SessionPopup.IsOpen = false;

    // ---------- копирование ----------

    private void OnCopyButton(object sender, RoutedEventArgs e)
    {
        if (Lines.SelectedItems.Count > 0) CopySelected();
        else _vm?.CopyCommand.Execute(null);
    }

    // ---------- автопрокрутка ----------

    private void OnLinesLoaded(object sender, RoutedEventArgs e)
    {
        if (_scroll is not null) return;
        _scroll = FindScrollViewer(Lines);
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Пользователь прокрутил вверх — автопрокрутка выключается; вернулся в самый низ — включается снова.</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm is null || _programmatic || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || e.VerticalChange == 0) return;
        var atBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 0.5;
        if (e.VerticalChange < 0 && !atBottom) _vm.AutoScroll = false;
        else if (atBottom) _vm.AutoScroll = true;
    }

    private void ScrollToEnd()
    {
        if (_vm is null || !_vm.AutoScroll || !IsVisible || _scroll is null) return;
        _programmatic = true;
        try
        {
            _scroll.ScrollToBottom();
            _scroll.UpdateLayout();
            _scroll.ScrollToBottom();
        }
        finally { _programmatic = false; }
    }
}
