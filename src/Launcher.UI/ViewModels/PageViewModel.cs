using Launcher.UI.Mvvm;

namespace Launcher.UI.ViewModels;

/// <summary>Страница главного окна (вкладка верхней навигации).</summary>
public abstract class PageViewModel : ObservableObject
{
    private bool _isSelected;

    protected PageViewModel(string title, string glyph)
    {
        Title = title;
        Glyph = glyph;
    }

    public string Title { get; }

    /// <summary>Символ шрифта Segoe MDL2 Assets.</summary>
    public string Glyph { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
