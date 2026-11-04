using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Launcher.UI.Controls;

namespace Launcher.UI.Views;

public partial class HomeView : UserControl
{
    private bool _running;

    public HomeView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Update();
        SizeChanged += (_, _) => FitPanel();
        Loaded += (_, _) =>
        {
            Motion.EcoChanged += Update;
            Update();
        };
        Unloaded += (_, _) => Motion.EcoChanged -= Update;
    }

    /// <summary>На широком и высоком окне панель запуска крупнее (до 1.8×), в обычном размере — как есть.</summary>
    private void FitPanel()
    {
        var scale = Math.Clamp(Math.Min(ActualWidth / 1500, ActualHeight / 830), 0.8, 1.15);
        PanelScale.ScaleX = PanelScale.ScaleY = scale;
        // выше окно — выше список профилей (остальное содержимое занимает около 540 px)
        ProfileList.MaxHeight = Math.Max(124, (ActualHeight - 48) / scale - 540);
    }

    /// <summary>Бесконечные анимации идут только пока страница видна и экономный режим выключен.</summary>
    private void Update()
    {
        var shouldRun = IsVisible && !Motion.Eco;
        if (shouldRun == _running) return;
        _running = shouldRun;
        if (shouldRun) Start();
        else Stop();
    }

    private void Start()
    {
        var shimmer = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        shimmer.KeyFrames.Add(new LinearDoubleKeyFrame(-1.1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        shimmer.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        shimmer.KeyFrames.Add(new LinearDoubleKeyFrame(1.1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8))));
        ShimmerShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, shimmer);

        var pulse = new DoubleAnimation(0.12, 0.42, TimeSpan.FromSeconds(2.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Glow.BeginAnimation(OpacityProperty, pulse);

        var grow = new DoubleAnimation(1.0, 1.035, TimeSpan.FromSeconds(2.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        GlowScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        GlowScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
    }

    private void Stop()
    {
        ShimmerShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        ShimmerShift.X = 0;
        Glow.BeginAnimation(OpacityProperty, null);
        Glow.Opacity = 0.15;
        GlowScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        GlowScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
    }
}
