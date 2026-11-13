using System.ComponentModel;
using System.Windows;
using Launcher.UI.Services;
using Launcher.UI.ViewModels;

namespace Launcher.UI.Views;

/// <summary>
/// Отдельное окно редактора интерфейса: настройки выбранного элемента не загораживают сам лаунчер.
/// Открывается вместе с режимом правки и закрывается, когда он заканчивается; закрытие окна завершает режим правки.
/// </summary>
internal sealed class EditorWindow : Window
{
    private readonly HomeEditorViewModel _editor;
    private bool _closingByState;

    private EditorWindow(Window owner, HomeEditorViewModel editor)
    {
        _editor = editor;
        Owner = owner;
        DataContext = editor;
        var content = new EditorPanel();
        Content = content;
        Title = "Редактор интерфейса — Waundbox";
        Width = 380;
        Height = Math.Min(720, SystemParameters.WorkArea.Height - 40);
        MinWidth = 340;
        MinHeight = 420;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "UiFont");
        PlaceBeside(owner);

        PreviewKeyDown += (_, e) => { if (_editor.HandleKey(e)) e.Handled = true; };
    }

    private static EditorWindow? _instance;

    /// <summary>Открывает окно редактора (одно на приложение).</summary>
    public static void Open(Window owner, HomeEditorViewModel editor)
    {
        if (_instance is not null) { _instance.Activate(); return; }
        _instance = new EditorWindow(owner, editor);
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
    }

    /// <summary>Закрывает окно, когда режим правки закончился.</summary>
    public static void CloseIfOpen()
    {
        if (_instance is null || _instance._closingByState) return; // уже закрывается — второй Close() даёт исключение
        _instance._closingByState = true;
        _instance.Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closingByState) return;
        // закрыли крестиком: режим правки выключается (раскладка при этом сохраняется), а Close() из обработчика повторно не зовём
        _closingByState = true;
        LayoutState.SetEditing(false);
    }

    /// <summary>Справа от лаунчера, если там есть место, иначе поверх его правого края.</summary>
    private void PlaceBeside(Window owner)
    {
        var area = SystemParameters.WorkArea;
        var left = owner.Left + owner.ActualWidth + 8;
        if (left + Width > area.Right) left = Math.Max(area.Left, area.Right - Width - 8);
        Left = left;
        Top = Math.Max(area.Top, Math.Min(owner.Top, area.Bottom - Height));
    }
}
