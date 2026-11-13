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
        vm.Context.Settings.Changed += () => Dispatcher.BeginInvoke(() => { UpdateBackground(); ApplyNavLayout(); });
        Loaded += (_, _) => ApplyNavLayout();
        _sideTimer.Tick += (_, _) => { _sideTimer.Stop(); ExpandSideBar(SideBar.IsMouseOver); };
        // небольшая задержка: мышь на границе или между кнопками не заставляет панель дёргаться туда-сюда
        SideBar.MouseEnter += (_, _) => { _sideTimer.Interval = TimeSpan.FromMilliseconds(40); _sideTimer.Stop(); _sideTimer.Start(); };
        SideBar.MouseLeave += (_, _) => { _sideTimer.Interval = TimeSpan.FromMilliseconds(180); _sideTimer.Stop(); _sideTimer.Start(); };
        InitTabDrag();
        // настройки элемента — в отдельном окне, оно живёт ровно пока включён режим правки
        LayoutState.EditingChanged += () =>
        {
            if (LayoutState.Editing) Views.EditorWindow.Open(this, vm.Context.HomeEditor);
            else Views.EditorWindow.CloseIfOpen();
        };
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

    private string _navMode = "?";
    private readonly System.Windows.Threading.DispatcherTimer _sideTimer = new();
    private bool _sideOpen;

    /// <summary>Узкое меню слева раскрывается поверх страницы, когда мышь заезжает на него, и сворачивается, когда уходит.</summary>
    private void ExpandSideBar(bool expand)
    {
        if (_navMode != NavLayouts.Side || expand == _sideOpen) return;
        _sideOpen = expand;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(expand ? 156 : 58, TimeSpan.FromMilliseconds(Motion.Eco ? 0 : 160))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        SideBar.BeginAnimation(WidthProperty, animation);
    }

    /// <summary>Меню сверху или слева (значками / с названиями): переключает видимость верхних вкладок и боковой панели.</summary>
    private void ApplyNavLayout()
    {
        var mode = NavLayouts.Normalize(_vm.Context.Settings.Current.NavLayout);
        if (mode == _navMode) return;
        _navMode = mode;
        var side = mode != NavLayouts.Top;
        SideBar.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        SideCol.Width = new GridLength(!side ? 0 : 58);
        SideBar.BeginAnimation(WidthProperty, null);
        SideBar.Width = 58;
        _sideOpen = false;
        TabsHost.Visibility = AccountButton.Visibility = TopBar.Visibility = side ? Visibility.Collapsed : Visibility.Visible;
        if (!side) Dispatcher.BeginInvoke(() => PlaceIndicator(animate: false), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnThemeApplied(ThemePalette palette)
    {
        // обычная тема с чистым чёрным или белым фоном: туман #101010 на чёрном и светло-серый на белом
        Color? fog = null;
        if (!palette.Image && palette.Background == ThemeService.DarkBackground) fog = Color.FromRgb(0x10, 0x10, 0x10);
        else if (!palette.Image && palette.Background == ThemeService.LightBackground) fog = Color.FromRgb(0xC9, 0xCC, 0xD3);
        Smoke.ApplyTheme(palette.Background, palette.IsLight, palette.Accent, fog);
    }

    /// <summary>Верхняя панель (ник, вкладки, кнопки окна) растёт вместе с шириной окна, как и главная: от 1× до 1,4×.</summary>
    private void ApplyTopScale()
    {
        UpdateLabel.Visibility = ActualWidth >= 1300 ? Visibility.Visible : Visibility.Collapsed; // на узком окне кнопка обновления — только значок
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
        TopBar.Height = Math.Round(52 * scale); // у RowDefinition.ActualHeight нет уведомлений, поэтому высоту панели задаём тут же
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
        var settings = _vm.Context.Settings.Current;
        var hasImage = UpdateBackgroundImage(settings);
        var glow = settings.BackgroundGlow && !hasImage; // под изображением дым не виден, поэтому его не рисуем
        Smoke.Visibility = glow ? Visibility.Visible : Visibility.Collapsed;
        Smoke.IsAnimated = false; // тихий режим: фон не движется и не нагружает видеокарту
    }

    private string? _bgPath;
    private double _bgLuminance = 0.5;

    /// <summary>Показывает или убирает своё фоновое изображение; возвращает, есть ли оно сейчас.</summary>
    private bool UpdateBackgroundImage(Launcher.Core.Configuration.LauncherSettings settings)
    {
        var path = Services.BackgroundImages.PathFor(_vm.Context.Services.Paths.Root, settings.BackgroundImage);
        if (!string.Equals(path, _bgPath, StringComparison.OrdinalIgnoreCase))
        {
            _bgPath = path;
            _bgLuminance = 0.5;
            BgImage.Source = null;
            if (path is not null)
            {
                try
                {
                    BgImage.Source = Services.BackgroundImages.Load(path);
                    _bgLuminance = Services.BackgroundImages.Luminance(path);
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException
                                               or InvalidOperationException or ArgumentException)
                {
                    _bgPath = null; // файл не читается — остаётся обычный фон
                }
            }
        }

        var has = BgImage.Source is not null;
        BgImage.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        BgDim.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        TopBlurImage.Source = BgImage.Source;
        TopBlurImage.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        SideBlurImage.Source = BgImage.Source;
        SideBlurImage.Visibility = TopBlurImage.Visibility;
        if (has && settings.BackgroundImageAuto)
        {
            // автоподбор меняет цвета интерфейса (ThemeService), а сама картинка остаётся как есть
            BgDim.Visibility = Visibility.Collapsed;
            if (Math.Abs(_bgLuminance - settings.BackgroundImageLuminance) > 0.02)
                _ = _vm.Context.Settings.UpdateAsync(s => s with { BackgroundImageLuminance = _bgLuminance });
        }
        else
        {
            BgDim.Fill = Brushes.Black;
            BgDim.Opacity = Math.Clamp(settings.BackgroundImageDim, 0, 0.9);
        }
        return has;
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
        if (_vm.Context.HomeEditor.HandleKey(e)) e.Handled = true;
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
