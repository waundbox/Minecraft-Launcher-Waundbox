using System.Windows;
using System.Windows.Controls;
using Launcher.UI.ViewModels;

namespace Launcher.UI.Views;

/// <summary>Панель редактора интерфейса: выбор элемента текущей страницы и его свойства (положение, размер, прозрачность…).</summary>
public partial class EditorPanel : UserControl
{
    public EditorPanel() => InitializeComponent();

    /// <summary>Отдельное окно сохранения темы поверх редактора.</summary>
    private void OnSaveThemeClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is HomeEditorViewModel editor) SaveThemeWindow.Show(Window.GetWindow(this), editor);
    }
}
