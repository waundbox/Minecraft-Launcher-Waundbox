using Launcher.Core.Profiles;
using Launcher.Core.Versions;
using Launcher.UI.Mvvm;

namespace Launcher.UI.ViewModels;

/// <summary>Состояние запуска одного профиля.</summary>
public enum LaunchState { Idle, Preparing, Running }

/// <summary>Профиль для показа в списках: название, версия, загрузчик, память и состояние запуска.</summary>
public sealed class ProfileItem : ObservableObject
{
    private LaunchState _state = LaunchState.Idle;
    private string _statusText = "";

    public ProfileItem(GameProfile profile)
    {
        Profile = profile;
        Label = VersionLabel.Parse(profile.VersionId);
    }

    public GameProfile Profile { get; }
    public VersionLabel Label { get; }

    public string Name => Profile.Name;
    public string Initial => Profile.Name.Length > 0 ? Profile.Name[..1].ToUpperInvariant() : "?";
    /// <summary>Значок профиля (картинка из папки icons) или null.</summary>
    public System.Windows.Media.ImageSource? Icon => Launcher.UI.Services.ProfileIcons.Load(Profile.Icon);
    /// <summary>Значок как кисть: заполняет весь скруглённый квадрат (UniformToFill).</summary>
    public System.Windows.Media.Brush? IconBrush
    {
        get
        {
            if (Icon is not { } image) return null;
            var brush = new System.Windows.Media.ImageBrush(image) { Stretch = System.Windows.Media.Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
    }
    public bool HasIcon => Icon is not null;
    public bool NoIcon => !HasIcon;
    public string VersionText => Label.Display;
    public string Subtitle => $"{Label.Display} · {Format.Memory(Profile.MaxMemoryMb)}";

    public LaunchState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsPreparing));
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    public bool IsIdle => _state == LaunchState.Idle;
    public bool IsPreparing => _state == LaunchState.Preparing;
    /// <summary>Игра запущена и работает.</summary>
    public bool IsPlaying => _state == LaunchState.Running;
    /// <summary>Профиль занят: готовится к запуску или игра запущена (редактировать и удалять нельзя).</summary>
    public bool IsRunning => _state != LaunchState.Idle;

    /// <summary>Пояснение к текущему состоянию («Проверяю файлы игры…», «Игра запущена»).</summary>
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }
}

public static class Format
{
    public static string Memory(int megabytes) =>
        megabytes % 1024 == 0 ? $"{megabytes / 1024} ГБ" : $"{megabytes / 1024.0:0.0} ГБ".Replace('.', ',');
}
