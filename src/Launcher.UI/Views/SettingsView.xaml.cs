using System.Windows.Controls;
using System.Windows.Input;
using Launcher.UI.ViewModels;

namespace Launcher.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnVersionClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.RegisterVersionClick();
    }
}
