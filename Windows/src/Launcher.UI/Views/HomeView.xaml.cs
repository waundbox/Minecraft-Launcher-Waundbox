using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Launcher.UI.Controls;

namespace Launcher.UI.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => FitPanel();
    }

    /// <summary>На широком и высоком окне панель запуска крупнее (до 1.8×), в обычном размере — как есть.</summary>
    private void FitPanel()
    {
        var scale = Math.Clamp(Math.Min(ActualWidth / 1500, ActualHeight / 830), 0.8, 1.15);
        PanelScale.ScaleX = PanelScale.ScaleY = scale;
        // выше окно — выше список профилей (остальное содержимое занимает около 540 px)
        ProfileList.MaxHeight = Math.Max(124, (ActualHeight - 48) / scale - 540);
    }
}
