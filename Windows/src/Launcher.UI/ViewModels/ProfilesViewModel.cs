using Launcher.Core.Errors;
using Launcher.Core.Modpacks;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Страница профилей: сетка карточек, создание, настройка, запуск (по образцу «экземпляров» Prism Launcher).</summary>
internal sealed class ProfilesViewModel : PageViewModel
{
    private readonly UiContext _ctx;

    public ProfilesViewModel(UiContext ctx) : base("Профили", "")
    {
        _ctx = ctx;
        AddCommand = new RelayCommand(() => _ctx.Dialogs.Show(new NewProfileViewModel(_ctx)));
        EditCommand = new RelayCommand(p =>
        {
            if (p is ProfileItem item) _ctx.Dialogs.Show(new ProfileEditViewModel(_ctx, item));
        });
        ModsCommand = new RelayCommand(p =>
        {
            if (p is ProfileItem item) _ctx.Dialogs.Show(new ModsViewModel(_ctx, item), wide: true);
        });
        ImportPackCommand = new AsyncCommand(ImportPackAsync);
        OpenFolderCommand = new RelayCommand(p =>
        {
            if (p is not ProfileItem item) return;
            if (!ShellHelper.OpenFolder(_ctx.Services.Layout.InstanceDirectory(item.Name)))
                _ctx.Toasts.Show("Не удалось открыть папку профиля.", ToastKind.Error);
        });
        SelectCommand = new RelayCommand(p =>
        {
            if (p is ProfileItem item) _ctx.Catalog.Selected = item;
        });
    }

    public ProfileCatalog Catalog => _ctx.Catalog;
    public LaunchController Launcher => _ctx.Launcher;

    public RelayCommand AddCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand ModsCommand { get; }
    public AsyncCommand ImportPackCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand SelectCommand { get; }

    /// <summary>Запуск конкретной карточки (команда лаунчера принимает ProfileItem).</summary>
    public RelayCommand PlayCommand => _ctx.Launcher.PlayCommand;

    /// <summary>Остановка игры конкретной карточки.</summary>
    public RelayCommand StopCommand => _ctx.Launcher.StopCommand;

    /// <summary>Выбор файла сборки (.mrpack или zip CurseForge), чтение описания и показ окна импорта.</summary>
    private async Task ImportPackAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите сборку (Modrinth или CurseForge)",
            Filter = "Сборки (*.mrpack;*.zip)|*.mrpack;*.zip|Сборка Modrinth (*.mrpack)|*.mrpack|Сборка CurseForge (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var path = dialog.FileName;
            var format = await Task.Run(() => CurseForgePackReader.Detect(path));
            if (format == ModpackFormat.Unknown)
                throw new ModException("В архиве нет ни modrinth.index.json, ни manifest.json — это не сборка Modrinth или CurseForge.");

            MrpackInfo info;
            if (format == ModpackFormat.CurseForge)
            {
                var manifest = await Task.Run(() => CurseForgePackReader.Read(path));
                _ctx.Toasts.Show("Запрашиваю адреса файлов у CurseForge…", ToastKind.Info);
                info = await CurseForgeResolver.ResolveAsync(manifest, _ctx.Services.CurseForge, CancellationToken.None);
            }
            else
            {
                info = await Task.Run(() => MrpackReader.Read(path));
            }
            var name = await ImportPackViewModel.SuggestNameAsync(_ctx, info.Name);
            _ctx.Dialogs.Show(new ImportPackViewModel(_ctx, path, info, name));
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Error);
        }
    }
}
