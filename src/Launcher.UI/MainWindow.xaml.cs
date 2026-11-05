using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Launcher.UI.Controls;
using Launcher.UI.Interop;
using Launcher.UI.Services;
using Launcher.UI.ViewModels;

namespace Launcher.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _forceClose;
    private bool _indicatorPlaced;

    internal MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        SourceInitialized += (_, _) => WindowInterop.Apply(this);
        Loaded += (_, _) => Dispatcher.InvokeAsync(() => PlaceIndicator(animate: false), System.Windows.Threading.DispatcherPriority.Loaded);
        TabsHost.SizeChanged += (_, _) => PlaceIndicator(animate: false);
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            if (WindowState == WindowState.Minimized) MemoryTrimmer.Trim();
        };
        StateChanged += (_, _) => { OnStateChanged(); UpdatePower(); };
        vm.Context.Settings.Changed += () => Dispatcher.BeginInvoke(UpdateBackground);
        InitTabDrag();
        InitIdle();
        SizeChanged += (_, _) => ApplyTopScale();
        Activated += (_, _) => UpdateBackground();
        Deactivated += (_, _) => UpdateBackground();
        Motion.EcoChanged += UpdateBackground;
        ThemeService.Applied += OnThemeApplied;
        Loaded += (_, _) => OnThemeApplied(ThemeService.Current);

        // порядок или набор вкладок изменился (редактор интерфейса): индикатор ставится после того, как вкладки перерисуются
        vm.Pages.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => PlaceIndicator(animate: false), System.Windows.Threading.DispatcherPriority.Loaded);

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentIndex)) PlaceIndicator(animate: true);
        };
        vm.Launcher.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LaunchController.IsRunning)) UpdateBackground();
        };
        vm.Dialogs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DialogService.IsOpen)) OnDialogChanged();
        };
    }

    private void OnThemeApplied(ThemePalette palette) =>
        Smoke.ApplyTheme(palette.Background, palette.IsLight, palette.Accent);

    /// <summary>Верхняя панель (ник, вкладки, кнопки окна) растёт вместе с шириной окна, как и главная: от 1× до 1,4×.</summary>
    private void ApplyTopScale()
    {
        var scale = Math.Clamp(ActualWidth / 1200, 1, 1.4);
        scale = Math.Round(scale * 8) / 8; // шаги по 1/8: размеры целые, текст чёткий
        if (Math.Abs(_topScale - scale) < 0.001) return;
        _topScale = scale;
        Resources["TopFont"] = Math.Round(13 * scale * 2) / 2;
        Resources["TopHead"] = Math.Round(24 * scale);
        Resources["TabHeight"] = Math.Round(38 * scale);
        Resources["TabFont"] = Math.Round(13.5 * scale * 2) / 2;
        Resources["TabGlyph"] = Math.Round(15 * scale);
        Resources["TabPadding"] = new Thickness(Math.Round(16 * scale), Math.Round(9 * scale), Math.Round(16 * scale), Math.Round(9 * scale));
        TopRow.Height = new GridLength(Math.Round(52 * scale));
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = Math.Round(52 * scale);
        Dispatcher.BeginInvoke(() => PlaceIndicator(animate: false), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private double _topScale = 1;

    // ----- кнопки окна -----

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged()
    {
        var maximized = WindowState == WindowState.Maximized;
        // развёрнутое окно с WindowChrome выходит за край экрана на толщину рамки
        Root.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxButton.Content = maximized ? "" : "";
        MaxButton.ToolTip = maximized ? "Восстановить" : "Развернуть";
        UpdateBackground();
    }

    // ----- фон: не тратим ресурсы, когда его не видно или он не нужен -----

    private void UpdateBackground()
    {
        var glow = _vm.Context.Settings.Current.BackgroundGlow;
        Smoke.Visibility = glow ? Visibility.Visible : Visibility.Collapsed;
        Smoke.IsAnimated = glow && !Motion.Eco && IsActive && WindowState != WindowState.Minimized && !_vm.Launcher.IsRunning;
    }

    // ----- простой: 10 секунд без действий в окне → экономный режим -----

    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(10);
    private readonly System.Windows.Threading.DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _lastInput = Environment.TickCount64;
    private bool _idle;

    private void InitIdle()
    {
        System.Windows.Input.InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is System.Windows.Input.MouseEventArgs or System.Windows.Input.KeyboardEventArgs)
            {
                _lastInput = Environment.TickCount64;
                if (_idle) SetIdle(false);
            }
        };
        _idleTimer.Tick += (_, _) =>
        {
            var enabled = _vm.Context.Settings.Current.IdleEco;
            var idle = enabled && !_idle && Environment.TickCount64 - _lastInput >= IdleAfter.TotalMilliseconds;
            if (idle) SetIdle(true);
            else if (!enabled && _idle) SetIdle(false);
        };
        _idleTimer.Start();
    }

    private void SetIdle(bool idle)
    {
        _idle = idle;
        Motion.SetIdle(idle);
        if (idle) GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true); // чуть освобождаем память
    }

    // ----- свёрнутый лаунчер почти не занимает память -----

    private readonly System.Windows.Threading.DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    private void UpdatePower()
    {
        var minimized = WindowState == WindowState.Minimized;
        AppPower.Set(minimized);
        _trimTimer.Stop();
        if (minimized) _trimTimer.Start(); // чуть позже: пусть сначала освободятся картинки
    }

    // ----- клавиши редактора: стрелки двигают выбранный элемент, Ctrl+Z / Ctrl+Y — отмена и повтор -----

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!LayoutState.Editing || e.Handled || System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;

        var editor = _vm.Context.HomeEditor;
        var ctrl = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
        var step = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)
            ? 10 : Math.Max(1, LayoutState.SnapStep);
        switch (e.Key)
        {
            case System.Windows.Input.Key.Z when ctrl: LayoutState.Undo(); e.Handled = true; break;
            case System.Windows.Input.Key.Y when ctrl: LayoutState.Redo(); e.Handled = true; break;
            case System.Windows.Input.Key.Escape: LayoutState.SetEditing(false); e.Handled = true; break;
            case System.Windows.Input.Key.Left: e.Handled = editor.Nudge(-step, 0); break;
            case System.Windows.Input.Key.Right: e.Handled = editor.Nudge(step, 0); break;
            case System.Windows.Input.Key.Up: e.Handled = editor.Nudge(0, -step); break;
            case System.Windows.Input.Key.Down: e.Handled = editor.Nudge(0, step); break;
        }
    }

    // ----- перетаскивание вкладок (в режиме правки главной) -----

    private PageViewModel? _dragPage;
    private Point _dragStart;
    private bool _dragMoved;

    private static PageViewModel? PageOf(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: PageViewModel page }) return page;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void InitTabDrag()
    {
        Tabs.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!LayoutState.Editing) return;
            _dragPage = PageOf(e.OriginalSource as DependencyObject);
            _dragStart = e.GetPosition(Tabs);
            _dragMoved = false;
        };
        Tabs.PreviewMouseMove += (_, e) =>
        {
            if (_dragPage is null) return;
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { _dragPage = null; return; }
            var point = e.GetPosition(Tabs);
            if (!_dragMoved && Math.Abs(point.X - _dragStart.X) < 8) return;
            _dragMoved = true;
            var target = PageOf(Tabs.InputHitTest(point) as DependencyObject);
            if (target is not null && !ReferenceEquals(target, _dragPage))
                _vm.MoveTab(_dragPage, _vm.Pages.IndexOf(target));
        };
        Tabs.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_dragMoved) e.Handled = true; // перетаскивание не должно считаться нажатием на вкладку
            _dragPage = null;
            _dragMoved = false;
        };
    }

    // ----- подвижный индикатор вкладки -----

    private void PlaceIndicator(bool animate)
    {
        if (Tabs.ItemContainerGenerator.ContainerFromIndex(_vm.CurrentIndex) is not FrameworkElement tab || !tab.IsLoaded || tab.ActualWidth < 1)
            return;

        var x = tab.TransformToAncestor(TabsHost).Transform(new Point(0, 0)).X;
        var width = tab.ActualWidth;

        if (!animate || Motion.Eco || !_indicatorPlaced)
        {
            IndicatorMove.BeginAnimation(TranslateTransform.XProperty, null);
            NavIndicator.BeginAnimation(WidthProperty, null);
            IndicatorMove.X = x;
            NavIndicator.Width = width;
            _indicatorPlaced = true;
            return;
        }

        IndicatorMove.BeginAnimation(TranslateTransform.XProperty, Motion.To(x, 360));
        NavIndicator.BeginAnimation(WidthProperty, Motion.To(width, 360));
    }

    // ----- диалоги -----

    private void OnDialogChanged()
    {
        if (_vm.Dialogs.IsOpen)
        {
            DialogLayer.Visibility = Visibility.Visible;
            if (Motion.Eco) return;

            Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            DialogCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.EaseOut });
            var grow = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = Motion.Back };
            DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        else
        {
            DialogLayer.Visibility = Visibility.Collapsed;
        }
    }

    // ----- закрытие -----

    /// <summary>Закрывает лаунчер без вопроса «Закрыть лаунчер?» (после запуска установщика обновления и по кнопке «Выйти»).</summary>
    internal void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_forceClose || !_vm.Launcher.IsRunning || _vm.Dialogs.IsLocked) // при обязательном обновлении выйти можно без вопросов
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        var ok = await _vm.Dialogs.ConfirmAsync(
            "Закрыть лаунчер?",
            _vm.Launcher.RunningCount > 1
                ? $"Сейчас запущено игр: {_vm.Launcher.RunningCount}. Если закрыть лаунчер, они продолжат работать, но вывод в консоли и журналы игр вестись больше не будут."
                : "Игра сейчас запущена. Если закрыть лаунчер, игра продолжит работать, но вывод в консоли и журнал игры вестись больше не будут.",
            "Закрыть лаунчер", danger: true);
        if (!ok) return;

        _forceClose = true;
        Close();
    }
}
