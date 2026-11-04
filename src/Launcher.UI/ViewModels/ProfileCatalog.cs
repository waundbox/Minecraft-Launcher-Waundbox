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

    public async Task ReloadAsync(string? select = null, CancellationToken ct = default)
    {
        var profiles = await _services.Profiles.ListAsync(ct);
        var wanted = select ?? _selected?.Name ?? _settings.Current.LastProfile;

        _loading = true;
        try
        {
            Items.Clear();
            foreach (var profile in profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
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
