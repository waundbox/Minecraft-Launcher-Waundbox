using System.Windows.Media;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Один вариант в выборе значка: файл (или «без значка») и его картинка.</summary>
public sealed record IconChoice(string? Name, ImageSource? Image)
{
    public bool HasImage => Image is not null;
    public bool IsNone => Name is null;
}

/// <summary>Выбор значка профиля из папки icons. Для нового профиля по умолчанию выбирается случайный.</summary>
internal sealed class IconPickerViewModel : ObservableObject
{
    private IconChoice _selected;

    public IconPickerViewModel(string? current, bool randomIfEmpty)
    {
        ProfileIcons.Refresh();
        var choices = new List<IconChoice> { new(null, null) };
        foreach (var name in ProfileIcons.Names())
            choices.Add(new IconChoice(name, ProfileIcons.Load(name)));
        Choices = choices;
        HasIcons = choices.Count > 1;

        var start = current;
        if (start is null && randomIfEmpty) start = ProfileIcons.PickRandom();
        _selected = choices.FirstOrDefault(c => c.Name is not null && string.Equals(c.Name, start, StringComparison.OrdinalIgnoreCase)) ?? choices[0];

        OpenFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(ProfileIcons.Directory));
    }

    public IReadOnlyList<IconChoice> Choices { get; }
    public bool HasIcons { get; }
    public bool HasNoIcons => !HasIcons;
    public RelayCommand OpenFolderCommand { get; }

    public IconChoice Selected
    {
        get => _selected;
        set { if (value is not null) Set(ref _selected, value); }
    }

    /// <summary>Имя выбранного файла или null.</summary>
    public string? Value => _selected.Name;
}
