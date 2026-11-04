using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Launcher.UI.Controls;

/// <summary>
/// Показывает текущую страницу и анимирует переходы: скольжение + затухание в сторону переключения.
/// Страницы создаются один раз и переиспользуются.
/// Анимируются только смещение и прозрачность; на время перехода страницы кэшируются как картинка.
/// </summary>
public sealed class PageHost : Grid
{
    private const double SlideDistance = 56;

    public static readonly DependencyProperty PagesProperty =
        DependencyProperty.Register(nameof(Pages), typeof(IList), typeof(PageHost));

    public static readonly DependencyProperty CurrentProperty =
        DependencyProperty.Register(nameof(Current), typeof(object), typeof(PageHost),
            new PropertyMetadata(null, (d, e) => ((PageHost)d).OnCurrentChanged(e.OldValue, e.NewValue)));

    public IList? Pages
    {
        get => (IList?)GetValue(PagesProperty);
        set => SetValue(PagesProperty, value);
    }

    public object? Current
    {
        get => GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    private readonly Dictionary<object, ContentPresenter> _views = new(ReferenceEqualityComparer.Instance);
    private ContentPresenter? _active;

    public PageHost()
    {
        ClipToBounds = true;
    }

    private ContentPresenter GetView(object page)
    {
        if (_views.TryGetValue(page, out var view)) return view;

        view = new ContentPresenter
        {
            Content = page,
            Visibility = Visibility.Collapsed,
            RenderTransform = new TranslateTransform(),
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        _views[page] = view;
        Children.Add(view);
        return view;
    }

    private static TranslateTransform Move(UIElement view) => (TranslateTransform)view.RenderTransform;

    private int IndexOf(object? page) => page is null || Pages is null ? -1 : Pages.IndexOf(page);

    private void OnCurrentChanged(object? oldPage, object? newPage)
    {
        if (newPage is null) return;

        var next = GetView(newPage);
        var previous = _active;
        if (ReferenceEquals(previous, next)) return;
        _active = next;

        var oldIndex = IndexOf(oldPage);
        var newIndex = IndexOf(newPage);
        var direction = oldIndex < 0 || newIndex < 0 || newIndex >= oldIndex ? 1 : -1;

        Reset(next);

        if (previous is null || Motion.Eco || !IsLoaded)
        {
            if (previous is not null) Hide(previous);
            next.Visibility = Visibility.Visible;
            return;
        }

        Transition(previous, next, direction);
    }

    private static void Reset(ContentPresenter view)
    {
        view.BeginAnimation(OpacityProperty, null);
        Move(view).BeginAnimation(TranslateTransform.XProperty, null);
        Move(view).X = 0;
        view.Opacity = 1;
        view.CacheMode = null;
    }

    private static void Hide(ContentPresenter view)
    {
        Reset(view);
        view.Visibility = Visibility.Collapsed;
    }

    private void Transition(ContentPresenter previous, ContentPresenter next, int direction)
    {
        // кэш в масштабе экрана, иначе при 125–200 % текст на время перехода размывается
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        next.CacheMode = new BitmapCache(dpi);
        previous.CacheMode = new BitmapCache(dpi);

        next.Opacity = 0;
        Move(next).X = direction * SlideDistance;
        next.Visibility = Visibility.Visible;
        Panel.SetZIndex(next, 1);
        Panel.SetZIndex(previous, 0);

        var fadeIn = Motion.To(1, 320);
        fadeIn.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_active, next)) return;
            Reset(next);
        };
        next.BeginAnimation(OpacityProperty, fadeIn);
        Move(next).BeginAnimation(TranslateTransform.XProperty, Motion.To(0, 380));

        var fadeOut = Motion.To(0, 200, Motion.EaseInOut);
        fadeOut.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_active, previous)) Hide(previous);
        };
        previous.BeginAnimation(OpacityProperty, fadeOut);
        Move(previous).BeginAnimation(TranslateTransform.XProperty, Motion.To(-direction * SlideDistance * 0.6, 260, Motion.EaseInOut));
    }
}
