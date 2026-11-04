using Launcher.Core.Configuration;

namespace Launcher.UI.Services;

/// <summary>Настройки лаунчера в памяти. Записи последовательны (одна за другой), чтобы изменения разных страниц не затирали друг друга.</summary>
internal sealed class UiSettings
{
    private readonly ISettingsStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UiSettings(ISettingsStore store) => _store = store;

    public LauncherSettings Current { get; private set; } = new();

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Current = await _store.LoadAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    public async Task UpdateAsync(Func<LauncherSettings, LauncherSettings> change, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var latest = await _store.LoadAsync(ct);
            Current = change(latest);
            await _store.SaveAsync(Current, ct);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }
}
