using Launcher.Core.Errors;
using Launcher.Core.GameLaunch;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

public sealed record GcChoice(string? Id, string Title, string Description);

/// <summary>Диалог «Настройки профиля»: память, аргументы, Java, удаление.</summary>
internal sealed class ProfileEditViewModel : ObservableObject
{
    private readonly UiContext _ctx;
    private readonly ProfileItem _item;
    private GcChoice _selectedGc;
    private string _jvmArguments;
    private string _gameArguments;
    private string _javaPath;
    private string _error = "";

    public ProfileEditViewModel(UiContext ctx, ProfileItem item)
    {
        _ctx = ctx;
        _item = item;
        _jvmArguments = item.Profile.JvmArguments ?? "";
        _gameArguments = item.Profile.GameArguments ?? "";
        _javaPath = item.Profile.JavaPath ?? "";

        Memory = new MemorySelection(item.Profile.MaxMemoryMb);
        Icons = new IconPickerViewModel(item.Profile.Icon, randomIfEmpty: false);

        var choices = new List<GcChoice> { new(null, "По умолчанию", "Лаунчер ничего не добавляет: Java выбирает сборщик сама (обычно G1).") };
        choices.AddRange(GarbageCollectors.All.Select(o => new GcChoice(o.Id, o.Title, $"{o.Description} Нужна Java {o.MinJava}+.")));
        GcChoices = choices;
        _selectedGc = choices.FirstOrDefault(c => string.Equals(c.Id, item.Profile.GarbageCollector, StringComparison.OrdinalIgnoreCase)) ?? choices[0];

        SaveCommand = new AsyncCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => _ctx.Dialogs.Close());
        DeleteCommand = new AsyncCommand(DeleteAsync);
        OpenFolderCommand = new RelayCommand(() =>
        {
            if (!ShellHelper.OpenFolder(_ctx.Services.Layout.InstanceDirectory(_item.Name)))
                _ctx.Toasts.Show("Не удалось открыть папку профиля.", ToastKind.Error);
        });
    }

    public string Name => _item.Name;
    public string VersionText => _item.VersionText;
    public MemorySelection Memory { get; }
    public IconPickerViewModel Icons { get; }

    public IReadOnlyList<GcChoice> GcChoices { get; }

    /// <summary>Сборщик мусора Java. Если выбранная Java его не поддерживает, при запуске будет стандартный и предупреждение.</summary>
    public GcChoice SelectedGc
    {
        get => _selectedGc;
        set
        {
            if (value is not null && Set(ref _selectedGc, value)) OnPropertyChanged(nameof(GcHint));
        }
    }

    public string GcHint => _selectedGc.Description;
    public string JvmArguments { get => _jvmArguments; set => Set(ref _jvmArguments, value); }
    public string GameArguments { get => _gameArguments; set => Set(ref _gameArguments, value); }
    public string JavaPath { get => _javaPath; set => Set(ref _javaPath, value); }
    public string Error { get => _error; private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    public AsyncCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task SaveAsync()
    {
        Error = "";
        var updated = _item.Profile with
        {
            MaxMemoryMb = Memory.Value,
            GarbageCollector = _selectedGc.Id,
            JvmArguments = NullIfEmpty(_jvmArguments),
            GameArguments = NullIfEmpty(_gameArguments),
            JavaPath = NullIfEmpty(_javaPath),
            Icon = Icons.Value
        };

        try
        {
            updated.Validate();
            await _ctx.Services.Profiles.SaveAsync(updated, CancellationToken.None);
        }
        catch (LauncherException ex)
        {
            Error = ex.Message;
            return;
        }

        await _ctx.Catalog.ReloadAsync(_item.Name);
        _ctx.Dialogs.Close();
        _ctx.Toasts.Show("Профиль сохранён.", ToastKind.Success);
    }

    private async Task DeleteAsync()
    {
        if (_item.IsRunning)
        {
            Error = "Профиль сейчас запущен. Закройте игру и повторите.";
            return;
        }

        var ok = await _ctx.Dialogs.ConfirmAsync(
            $"Удалить профиль «{_item.Name}»?",
            "Профиль исчезнет из списка, а его папка (миры, моды, настройки, скриншоты) будет удалена с диска без возможности восстановления.",
            "Удалить", danger: true);
        if (!ok)
        {
            _ctx.Dialogs.Show(this);
            return;
        }

        try
        {
            await _ctx.Services.Profiles.DeleteAsync(_item.Name, CancellationToken.None);
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Error);
            return;
        }

        // папка профиля удаляется вместе с ним, чтобы не копились мёртвые данные
        var name = _item.Name;
        var folderError = await Task.Run(() =>
        {
            try
            {
                var folder = _ctx.Services.Layout.InstanceDirectory(name);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LauncherException)
            {
                _ctx.Services.Logger.Warn($"Не удалось удалить папку профиля «{name}».", ex);
                return ex.Message;
            }
        });

        await _ctx.Catalog.ReloadAsync();
        if (folderError is null) _ctx.Toasts.Show($"Профиль «{name}» и его папка удалены.", ToastKind.Info);
        else _ctx.Toasts.Show($"Профиль «{name}» удалён, но папку удалить не удалось: {folderError}", ToastKind.Warning);
    }
}
