using System.Windows;
using System.Windows.Controls;

namespace Launcher.UI.Controls;

/// <summary>Прикреплённые свойства, которые нужны стилям (скругление, «выбрано»).</summary>
public static class Ui
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached("CornerRadius", typeof(CornerRadius), typeof(Ui),
            new FrameworkPropertyMetadata(new CornerRadius(14)));

    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject d, CornerRadius v) => d.SetValue(CornerRadiusProperty, v);

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.RegisterAttached("IsSelected", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    public static bool GetIsSelected(DependencyObject d) => (bool)d.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject d, bool v) => d.SetValue(IsSelectedProperty, v);

    /// <summary>Поле ввода принимает только цифры (набор и вставка из буфера обмена).</summary>
    public static readonly DependencyProperty DigitsOnlyProperty =
        DependencyProperty.RegisterAttached("DigitsOnly", typeof(bool), typeof(Ui),
            new PropertyMetadata(false, OnDigitsOnlyChanged));

    public static bool GetDigitsOnly(DependencyObject d) => (bool)d.GetValue(DigitsOnlyProperty);
    public static void SetDigitsOnly(DependencyObject d, bool v) => d.SetValue(DigitsOnlyProperty, v);

    private static void OnDigitsOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewTextInput -= OnPreviewTextInput;
        DataObject.RemovePastingHandler(box, OnPaste);
        if (!(bool)e.NewValue) return;
        box.PreviewTextInput += OnPreviewTextInput;
        DataObject.AddPastingHandler(box, OnPaste);
    }

    private static void OnPreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsAsciiDigit);

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.All(char.IsAsciiDigit))
            e.CancelCommand();
    }
}
