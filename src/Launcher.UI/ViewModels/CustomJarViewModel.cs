using System.Text.RegularExpressions;
using Launcher.Core.Custom;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Profiles;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Диалог «Свой jar»: выбор jar (и, при желании, json версии), базовая версия, имя; установка и создание профиля.</summary>
internal sealed class CustomJarViewModel : ObservableObject
{
    private readonly UiContext _ctx;
    private CancellationTokenSource? _cts;
    private string _jarPath = "";
    private string _jsonPath = "";
    private string _baseVersion = "";
    private string _name = "";
    private string _jarInfo = "";
    private bool _isBusy;
    private double _progress;
    private string _statusText = "";
    private string _error = "";

    public CustomJarViewModel(UiContext ctx)
    {
        _ctx = ctx;
        var physical = MemorySelection.DetectPhysicalMegabytes();
        Memory = new MemorySelection(physical >= 8192 ? 4096 : 2048);

        BrowseJarCommand = new RelayCommand(BrowseJar, () => !_isBusy);
        BrowseJsonCommand = new RelayCommand(BrowseJson, () => !_isBusy);
        ClearJsonCommand = new RelayCommand(() => JsonPath = "", () => !_isBusy && _jsonPath.Length > 0);
        InstallCommand = new AsyncCommand(InstallAsync, () => !_isBusy && _jarPath.Length > 0);
        CancelCommand = new RelayCommand(Cancel);
    }

    public MemorySelection Memory { get; }
    public IconPickerViewModel Icons { get; } = new(null, randomIfEmpty: true);

    public string JarPath { get => _jarPath; private set { if (Set(ref _jarPath, value)) OnPropertyChanged(nameof(HasJar)); } }
    public bool HasJar => _jarPath.Length > 0;
    public string JsonPath
    {
        get => _jsonPath;
        private set { if (Set(ref _jsonPath, value)) { OnPropertyChanged(nameof(HasJson)); OnPropertyChanged(nameof(NoJson)); } }
    }
    public bool HasJson => _jsonPath.Length > 0;
    public bool NoJson => !HasJson;
    public string JarInfo { get => _jarInfo; private set => Set(ref _jarInfo, value); }
    public string BaseVersion { get => _baseVersion; set => Set(ref _baseVersion, value); }
    public string Name { get => _name; set => Set(ref _name, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsNotBusy => !_isBusy;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string Error { get => _error; private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    public RelayCommand BrowseJarCommand { get; }
    public RelayCommand BrowseJsonCommand { get; }
    public RelayCommand ClearJsonCommand { get; }
    public AsyncCommand InstallCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void Cancel()
    {
        if (_isBusy) _cts?.Cancel();
        else _ctx.Dialogs.Close();
    }

    private void BrowseJar()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите jar-файл",
            Filter = "Java-архив (*.jar)|*.jar|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        Error = "";
        var path = dialog.FileName;
        try
        {
            var inspection = CustomJarTools.Inspect(path);
            JarPath = path;
            JarInfo = inspection.LooksLikeClient
                ? $"Похоже на клиент Minecraft (классов: {inspection.ClassCount})."
                : $"Классов: {inspection.ClassCount}. Файл не похож на клиент Minecraft — убедитесь, что это нужный jar.";
            if (inspection.DetectedVersion is { } detected) BaseVersion = detected;
        }
        catch (CustomJarException ex)
        {
            JarPath = "";
            JarInfo = "";
            Error = ex.Message;
            return;
        }

        if (_name.Length == 0) Name = SuggestName(path);

        // json лежит рядом: «клиент.json» или version.json
        if (_jsonPath.Length == 0)
        {
            var folder = Path.GetDirectoryName(path)!;
            var candidates = new[] { Path.ChangeExtension(path, ".json"), Path.Combine(folder, "version.json") };
            var found = candidates.FirstOrDefault(File.Exists);
            if (found is not null) JsonPath = found;
        }
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void BrowseJson()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите json версии",
            Filter = "Описание версии (*.json)|*.json|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true) JsonPath = dialog.FileName;
    }

    private static string SuggestName(string jarPath)
    {
        var cleaned = Regex.Replace(Path.GetFileNameWithoutExtension(jarPath), @"[^A-Za-z0-9_.\-]+", "-").Trim('-', '.', '_');
        if (cleaned.Length == 0) cleaned = "custom";
        return cleaned.Length > 36 ? cleaned[..36] : cleaned;
    }

    private async Task InstallAsync()
    {
        Error = "";
        var name = _name.Trim();
        var baseVersion = string.IsNullOrWhiteSpace(_baseVersion) ? null : _baseVersion.Trim();

        try
        {
            if (!CustomJarTools.IsValidName(name))
                throw new CustomJarException("Название: до 40 символов, латинские буквы, цифры, точка, дефис и подчёркивание, без пробелов.");
            new GameProfile(name, "x", Memory.Value).Validate();
            if (await _ctx.Services.Profiles.FindAsync(name, CancellationToken.None) is not null)
                throw new CustomJarException($"Профиль «{name}» уже существует. Выберите другое название.");
        }
        catch (LauncherException ex)
        {
            Error = ex.Message;
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        Progress = 0;
        StatusText = "Проверяю файлы…";

        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                if (p.Stage == InstallStage.Files)
                {
                    StatusText = $"Скачиваю файлы игры: {p.FilesDone} из {p.FilesTotal}";
                    if (p.BytesTotal > 0) Progress = Math.Clamp(p.BytesDone * 95.0 / p.BytesTotal, 0, 95);
                }
                else StatusText = "Готовлю версию…";
            });

            var result = await _ctx.Services.CustomJar.InstallAsync(
                new CustomJarRequest(_jarPath, name, baseVersion, _jsonPath.Length > 0 ? _jsonPath : null),
                new InstallOptions(), progress, ct);

            Progress = 98;
            await _ctx.Services.Profiles.SaveAsync(
                new GameProfile(name, result.VersionId, Memory.Value, Icon: Icons.Value), CancellationToken.None);
            await _ctx.Catalog.ReloadAsync(name);
            _ctx.Dialogs.Close();
            _ctx.Toasts.Show($"Версия «{result.VersionId}» установлена как профиль «{name}».", ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            StatusText = "";
            Error = "Установка отменена.";
        }
        catch (LauncherException ex)
        {
            StatusText = "";
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "";
            Error = "Не удалось записать файлы: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
