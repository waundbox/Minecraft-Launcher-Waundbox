using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Launcher.UI.Controls;

/// <summary>
/// Общие настройки анимаций. Все анимации — только прозрачность и смещение/масштаб (их выполняет видеокарта,
/// пересчёта разметки нет). В экономном режиме анимации отключаются.
/// </summary>
public static class Motion
{
    private static bool _ecoSetting;
    private static bool _idle;

    /// <summary>Экономный режим: включён в настройках или лаунчер простаивает (нет действий пользователя).</summary>
    public static bool Eco { get; private set; }

    public static event Action? EcoChanged;

    public static void SetEco(bool eco)
    {
        _ecoSetting = eco;
        Apply();
    }

    /// <summary>Лаунчер простаивает: анимации и вращение модели приостанавливаются, как в экономном режиме.</summary>
    public static void SetIdle(bool idle)
    {
        _idle = idle;
        Apply();
    }

    private static void Apply()
    {
        var eco = _ecoSetting || _idle;
        if (Eco == eco) return;
        Eco = eco;
        EcoChanged?.Invoke();
    }

    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseInOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction Back = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    public static DoubleAnimation To(double to, int ms, IEasingFunction? ease = null, int delayMs = 0) =>
        new(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? EaseOut,
            BeginTime = TimeSpan.FromMilliseconds(delayMs) // null означал бы «никогда не начинать»
        };

    // ----- Appear: плавное появление элемента при загрузке (с «лесенкой» для элементов списка) -----

    public static readonly DependencyProperty AppearProperty =
        DependencyProperty.RegisterAttached("Appear", typeof(bool), typeof(Motion),
            new PropertyMetadata(false, OnAppearChanged));

    public static bool GetAppear(DependencyObject d) => (bool)d.GetValue(AppearProperty);
    public static void SetAppear(DependencyObject d, bool v) => d.SetValue(AppearProperty, v);

    private static void OnAppearChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not true) return;
        if (Eco) return;
        fe.Opacity = 0;
        fe.Loaded += OnAppearLoaded;
        fe.IsVisibleChanged += OnAppearVisible;

        // Страховка: если по какой-то причине анимация не запустилась, элемент не должен остаться невидимым.
        var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            if (fe.Opacity == 0 && !fe.HasAnimatedProperties) fe.Opacity = 1;
        };
        guard.Start();
    }

    private static void OnAppearVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        if (e.NewValue is true && fe.IsLoaded) OnAppearLoaded(sender, new RoutedEventArgs());
    }

    private static void OnAppearLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        fe.Loaded -= OnAppearLoaded;
        fe.IsVisibleChanged -= OnAppearVisible;
        if (Eco)
        {
            fe.Opacity = 1;
            return;
        }

        var delay = Math.Min(IndexInItemsPanel(fe), 10) * 40;

        TranslateTransform? move = null;
        if (fe.RenderTransform is null || fe.RenderTransform == Transform.Identity)
        {
            move = new TranslateTransform(0, 14);
            fe.RenderTransform = move;
        }

        fe.BeginAnimation(UIElement.OpacityProperty, To(1, 320, EaseOut, delay));
        move?.BeginAnimation(TranslateTransform.YProperty, To(0, 380, EaseOut, delay));
    }

    private static int IndexInItemsPanel(DependencyObject element)
    {
        DependencyObject? child = element;
        while (child is not null)
        {
            var parent = VisualTreeHelper.GetParent(child);
            if (parent is Panel { IsItemsHost: true } panel && child is UIElement ui)
                return Math.Max(0, panel.Children.IndexOf(ui));
            child = parent;
        }
        return 0;
    }

    // ----- TabFade: плавная смена содержимого вкладки внутри страницы (панель появляется со сдвигом снизу) -----

    public static readonly DependencyProperty TabFadeProperty =
        DependencyProperty.RegisterAttached("TabFade", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnTabFadeChanged));

    public static bool GetTabFade(DependencyObject d) => (bool)d.GetValue(TabFadeProperty);
    public static void SetTabFade(DependencyObject d, bool v) => d.SetValue(TabFadeProperty, v);

    private static void OnTabFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.IsVisibleChanged -= OnTabVisible;
        if (e.NewValue is true) fe.IsVisibleChanged += OnTabVisible;
    }

    private static void OnTabVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not FrameworkElement fe || !fe.IsLoaded || Eco) return;
        var move = fe.RenderTransform as TranslateTransform;
        if (move is null || move.IsFrozen)
        {
            move = new TranslateTransform();
            fe.RenderTransform = move;
        }
        fe.Opacity = 0;
        fe.BeginAnimation(UIElement.OpacityProperty, To(1, 280, EaseOut, 0));
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = EaseOut });
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = EaseOut });
    }
}
