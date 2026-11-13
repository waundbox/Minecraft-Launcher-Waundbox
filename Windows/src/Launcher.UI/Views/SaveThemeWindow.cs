using System.Windows;
using Launcher.UI.ViewModels;

namespace Launcher.UI.Views;

/// <summary>Отдельное модальное окно «Сохранить тему»: название, описание, цвет текста. Закрывается, когда тема записана.</summary>
internal sealed class SaveThemeWindow : Window
{
    private SaveThemeWindow(Window? owner, HomeEditorViewModel editor)
    {
        Owner = owner;
        DataContext = editor;
        Content = new SaveThemeDialog();
        Title = "Сохранить тему — Waundbox";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        void Close_() => Close();
        editor.ThemeSaved += Close_;
        Closed += (_, _) => editor.ThemeSaved -= Close_;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); }
        };
    }

    public static void Show(Window? owner, HomeEditorViewModel editor)
    {
        editor.ResetThemeMessage();
        new SaveThemeWindow(owner, editor).ShowDialog();
    }
}
