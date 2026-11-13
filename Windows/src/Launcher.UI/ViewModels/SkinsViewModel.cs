using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using Launcher.Core.Errors;
using Launcher.Core.Skins;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Плитка скина в коллекции: двухмерный вид спереди, имя, модель.</summary>
internal sealed class SkinCard : ObservableObject
{
    private bool _isActive;

    public SkinCard(SkinEntry entry, ImageSource preview, byte[] png)
    {
        Entry = entry;
        Preview = preview;
        Png = png;
    }

    public SkinEntry Entry { get; }
    public ImageSource Preview { get; }
    public byte[] Png { get; }
    public string Name => Entry.Name;
    public string ModelText => Entry.Slim ? "Alex (тонкие руки)" : "Steve";
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
}

/// <summary>Вкладка «Скины»: коллекция скинов с плитками (как в официальном лаунчере). Выбор делает скин текущим; в аккаунт Microsoft он уходит отдельной кнопкой.</summary>
internal sealed class SkinsViewModel : ObservableObject
{
    private readonly UiContext _ctx;
    private bool _loaded;
    private bool _busy;

    public SkinsViewModel(UiContext ctx)
    {
        _ctx = ctx;
        AddCommand = new AsyncCommand(AddAsync, () => !_busy);
        UseCommand = new AsyncCommand(UseAsync, p => !_busy && p is SkinCard);
        ApplyToMicrosoftCommand = new AsyncCommand(ApplyToMicrosoftAsync, p => !_busy && p is SkinCard && _ctx.Account.IsSignedIn);
        DeleteCommand = new AsyncCommand(DeleteAsync, p => !_busy && p is SkinCard);
    }

    public ObservableCollection<SkinCard> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;

    public AsyncCommand AddCommand { get; }
    public AsyncCommand UseCommand { get; }
    public AsyncCommand ApplyToMicrosoftCommand { get; }
    public AsyncCommand DeleteCommand { get; }

    /// <summary>Читает коллекцию при первом показе вкладки.</summary>
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : ReloadAsync();

    public async Task ReloadAsync()
    {
        _loaded = true;
        var entries = await _ctx.Services.SkinLibrary.ListAsync(CancellationToken.None);
        var current = await _ctx.Services.Skins.ReadSkinAsync(CancellationToken.None);

        Items.Clear();
        foreach (var entry in entries)
        {
            var png = await _ctx.Services.SkinLibrary.ReadPngAsync(entry.Id, CancellationToken.None);
            if (png is null) continue;
            var shown = entry with { Slim = SkinImages.IsSlim(png, entry.Slim) };
            Items.Add(new SkinCard(shown, SkinImages.Front(png, shown.Slim), png) { IsActive = current is not null && current.AsSpan().SequenceEqual(png) });
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task AddAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Добавить скин в коллекцию",
            Filter = "Скин Minecraft (PNG, 64×64)|*.png",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        await RunAsync(async () =>
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            await _ctx.Services.SkinLibrary.AddAsync(Path.GetFileNameWithoutExtension(dialog.FileName), bytes, SkinImages.IsSlim(bytes, _ctx.Settings.Current.SkinSlim), CancellationToken.None);
            await ReloadAsync();
            _ctx.Toasts.Show("Скин добавлен в коллекцию.", ToastKind.Success);
        });
    }

    /// <summary>Делает скин текущим: его показывает модель на главной и он используется в одиночной игре (режим разработки).</summary>
    private async Task UseAsync(object? parameter)
    {
        if (parameter is not SkinCard card) return;
        await RunAsync(async () =>
        {
            await _ctx.Services.Skins.SetSkinAsync(card.Png, CancellationToken.None);
            await _ctx.Settings.UpdateAsync(s => s with { SkinSlim = card.Entry.Slim });
            await _ctx.Account.RefreshAsync();
            await ReloadAsync();
            _ctx.Toasts.Show($"Скин «{card.Name}» выбран.", ToastKind.Success);
        });
    }

    private async Task ApplyToMicrosoftAsync(object? parameter)
    {
        if (parameter is not SkinCard card) return;
        await RunAsync(async () =>
        {
            await _ctx.Services.Accounts.UploadSkinAsync(card.Png, card.Entry.Slim, CancellationToken.None);
            await _ctx.Services.Skins.SetSkinAsync(card.Png, CancellationToken.None);
            await _ctx.Settings.UpdateAsync(s => s with { SkinSlim = card.Entry.Slim });
            await _ctx.Account.RefreshAsync();
            await ReloadAsync();
            _ctx.Toasts.Show($"Скин «{card.Name}» загружен в аккаунт Microsoft.", ToastKind.Success);
        });
    }

    private async Task DeleteAsync(object? parameter)
    {
        if (parameter is not SkinCard card) return;
        await RunAsync(async () =>
        {
            await _ctx.Services.SkinLibrary.RemoveAsync(card.Entry.Id, CancellationToken.None);
            await ReloadAsync();
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            await action();
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Toasts.Show("Не удалось прочитать или записать файл: " + ex.Message, ToastKind.Error);
        }
        finally
        {
            _busy = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
}
