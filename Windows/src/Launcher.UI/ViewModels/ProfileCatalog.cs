using System.Collections.ObjectModel;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Список профилей и выбранный профиль (общий для главной страницы и страницы профилей).</summary>
internal sealed class ProfileCatalog : ObservableObject
{
    private readonly AppServices _services;
    private readonly UiSettings _settings;
    private ProfileItem? _selected;
    private bool _loading;

    public ProfileCatalog(AppServices services, UiSettings settings)
    {
        _services = services;
        _settings = settings;
    }

    public ObservableCollection<ProfileItem> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => Items.Count == 0;

    public ProfileItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (!_loading && value is not null)
            {
                var name = value.Name;
                _ = _settings.UpdateAsync(s => s with { LastProfile = name });
            }
        }
    }

    /// <summary>Порядок из настроек (имена через запятую), остальные профили — после них по алфавиту.</summary>
    public static IEnumerable<Launcher.Core.Profiles.GameProfile> Ordered(IEnumerable<Launcher.Core.Profiles.GameProfile> profiles, string? order)
    {
        var saved = (order ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        int Rank(string name)
        {
            var i = saved.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }
        return profiles.OrderBy(p => Rank(p.Name)).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>Переносит профиль на новое место и запоминает порядок; главная и страница профилей показывают один список, поэтому порядок у них общий.</summary>
    public void Move(ProfileItem item, int newIndex)
    {
        var from = Items.IndexOf(item);
        if (from < 0 || newIndex < 0 || newIndex >= Items.Count || from == newIndex) return;
        var keep = _selected;
        _loading = true;
        try
        {
            Items.Move(from, newIndex);
            Selected = keep;
        }
        finally
        {
            _loading = false;
        }
        var order = string.Join(',', Items.Select(i => i.Name));
        _ = _settings.UpdateAsync(s => s with { ProfileOrder = order });
    }

    public async Task ReloadAsync(string? select = null, CancellationToken ct = default)
    {
        var profiles = await _services.Profiles.ListAsync(ct);
        var wanted = select ?? _selected?.Name ?? _settings.Current.LastProfile;

        _loading = true;
        try
        {
            Items.Clear();
            foreach (var profile in Ordered(profiles, _settings.Current.ProfileOrder))
                Items.Add(new ProfileItem(profile));

            Selected = Items.FirstOrDefault(i => string.Equals(i.Name, wanted, StringComparison.OrdinalIgnoreCase))
                       ?? Items.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
