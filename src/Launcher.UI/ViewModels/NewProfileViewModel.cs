using System.Collections.ObjectModel;
using System.Diagnostics;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Profiles;
using Launcher.Core.Versions;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

public sealed record LoaderOption(LoaderKind Kind, string Title, string Hint);

/// <summary>Диалог «Новый профиль»: загрузчик, версия игры, память; установка с прогрессом и отменой.</summary>
internal sealed class NewProfileViewModel : ObservableObject
{
    private readonly UiContext _ctx;
    private readonly List<string> _releases = new();
    private readonly List<string> _snapshots = new();
    private HashSet<string>? _forgeVersions;
    private HashSet<string>? _neoForgeGames;
    private CancellationTokenSource? _cts;

    private string _name = "";
    private bool _nameEditedByUser;
    private bool _settingNameAutomatically;
    private LoaderOption _loader;
    private string _filter = "";
    private bool _showSnapshots;
    private string? _selectedVersion;
    private bool _isLoadingVersions = true;
    private string _versionsMessage = "";
    private bool _isBusy;
    private bool _isIndeterminate = true;
    private double _progress;
    private string _statusText = "";
    private string _error = "";
    private long _lastProgressTick;

    public NewProfileViewModel(UiContext ctx)
    {
        _ctx = ctx;
        Loaders = new[]
        {
            new LoaderOption(LoaderKind.Vanilla, "Minecraft", "Чистая игра"),
            new LoaderOption(LoaderKind.Fabric, "Fabric", "Лёгкие моды"),
            new LoaderOption(LoaderKind.Quilt, "Quilt", "Совместим с модами Fabric"),
            new LoaderOption(LoaderKind.Forge, "Forge", "Моды 1.13 и новее"),
            new LoaderOption(LoaderKind.NeoForge, "NeoForge", "Моды 1.20.1 и новее"),
            new LoaderOption(LoaderKind.Custom, "Свой jar", "Из файла .jar")
        };
        _loader = Loaders[0];

        var maxForSlider = Math.Max(2048, MemorySelection.DetectPhysicalMegabytes() / MemorySelection.Step * MemorySelection.Step);
        Memory = new MemorySelection(maxForSlider >= 8192 ? 4096 : 2048);

        CreateCommand = new AsyncCommand(CreateAsync, () => !_isBusy && _selectedVersion is not null);
        CancelCommand = new RelayCommand(Cancel);
        _ = LoadVersionsAsync();
    }

    public IReadOnlyList<LoaderOption> Loaders { get; }
    public ObservableCollection<string> Versions { get; } = new();
    public MemorySelection Memory { get; }
    public IconPickerViewModel Icons { get; } = new(null, randomIfEmpty: true);

    public AsyncCommand CreateCommand { get; }
    public RelayCommand CancelCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            if (!_settingNameAutomatically) _nameEditedByUser = true;
        }
    }

    public LoaderOption SelectedLoader
    {
        get => _loader;
        set
        {
            if (value is null) return;
            if (value.Kind == LoaderKind.Custom)
            {
                // «Свой jar» — не загрузчик из списка версий, а отдельный диалог установки из файла
                if (!_isBusy) _ctx.Dialogs.Show(new CustomJarViewModel(_ctx));
                OnPropertyChanged(nameof(SelectedLoader)); // выбор остаётся на прежнем варианте
                return;
            }
            if (!Set(ref _loader, value)) return;
            _ = OnLoaderChangedAsync();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) RebuildVersions();
        }
    }

    public bool ShowSnapshots
    {
        get => _showSnapshots;
        set
        {
            if (Set(ref _showSnapshots, value)) RebuildVersions();
        }
    }

    public string? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!Set(ref _selectedVersion, value)) return;
            SuggestName();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsLoadingVersions { get => _isLoadingVersions; private set => Set(ref _isLoadingVersions, value); }
    public string VersionsMessage { get => _versionsMessage; private set { if (Set(ref _versionsMessage, value)) OnPropertyChanged(nameof(HasVersionsMessage)); } }
    public bool HasVersionsMessage => _versionsMessage.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsNotBusy));
        }
    }

    public bool IsNotBusy => !_isBusy;
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string Error { get => _error; private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    private async Task LoadVersionsAsync()
    {
        IsLoadingVersions = true;
        VersionsMessage = "";
        try
        {
            var result = await _ctx.Services.Manifest.GetManifestAsync(false, CancellationToken.None);
            _releases.Clear();
            _snapshots.Clear();
            foreach (var v in result.Manifest.Versions)
            {
                if (v.Type == VersionType.Release) _releases.Add(v.Id);
                else if (v.Type == VersionType.Snapshot) _snapshots.Add(v.Id);
            }

            if (result.Source == ManifestSource.StaleCache)
                VersionsMessage = "Нет связи с сервером Mojang — показан сохранённый список версий.";
        }
        catch (Exception ex)
        {
            // не только ошибки лаунчера: любая неожиданность не должна оставлять экран на вечной «Загрузке»
            _ctx.Services.Logger.Error("Не удалось загрузить список версий для мастера профиля.", ex);
            VersionsMessage = ex is LauncherException ? ex.Message : "Не удалось загрузить список версий: " + ex.Message;
        }

        IsLoadingVersions = false;
        RebuildVersions(preferLatest: true);
        if (VersionsMessage.Length == 0 && Versions.Count == 0)
            VersionsMessage = $"Список версий пуст (получено: релизов {_releases.Count}, снапшотов {_snapshots.Count}).";
    }

    private async Task OnLoaderChangedAsync()
    {
        if (_loader.Kind == LoaderKind.Forge && _forgeVersions is null)
        {
            try
            {
                var promos = await _ctx.Services.ForgeMeta.GetPromotionsAsync(CancellationToken.None);
                _forgeVersions = promos.Keys
                    .Select(k => k.LastIndexOf('-') is var i and > 0 ? k[..i] : k)
                    .ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _forgeVersions = null;
                _ctx.Services.Logger.Error("Не удалось получить список версий Forge.", ex);
                VersionsMessage = "Список версий Forge получить не удалось: " + ex.Message;
            }
        }
        if (_loader.Kind == LoaderKind.NeoForge && _neoForgeGames is null)
        {
            try
            {
                var all = await _ctx.Services.NeoForgeMeta.GetVersionsAsync(CancellationToken.None);
                _neoForgeGames = Core.NeoForge.NeoForgeVersions.SupportedGames(all).ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _neoForgeGames = null;
                _ctx.Services.Logger.Error("Не удалось получить список версий NeoForge.", ex);
                VersionsMessage = "Список версий NeoForge получить не удалось: " + ex.Message;
            }
        }
        RebuildVersions();
        SuggestName();
    }

    private bool IsSupported(string version)
    {
        switch (_loader.Kind)
        {
            case LoaderKind.Forge:
                if (_forgeVersions is not null) return _forgeVersions.Contains(version) && IsModernForgeVersion(version);
                return IsModernForgeVersion(version);
            case LoaderKind.NeoForge:
                if (_neoForgeGames is not null) return _neoForgeGames.Contains(version);
                return Core.NeoForge.NeoForgeVersions.Prefix(version) is not null;
            default:
                return true;
        }
    }

    /// <summary>Установщик Forge поддерживает версии игры 1.13 и новее.</summary>
    internal static bool IsModernForgeVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return false;
        return major > 1 || (major == 1 && minor >= 13);
    }

    private void RebuildVersions(bool preferLatest = false)
    {
        var source = _showSnapshots ? _snapshots.Concat(_releases).ToList() : _releases;
        var filter = _filter.Trim();
        var list = source
            .Where(IsSupported)
            .Where(v => filter.Length == 0 || v.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var keep = preferLatest ? null : _selectedVersion;
        Versions.Clear();
        foreach (var v in list) Versions.Add(v);

        SelectedVersion = keep is not null && list.Contains(keep) ? keep : list.FirstOrDefault();
    }

    private void SuggestName()
    {
        if (_nameEditedByUser || _selectedVersion is null) return;
        var prefix = _loader.Kind switch
        {
            LoaderKind.Fabric => "Fabric",
            LoaderKind.Quilt => "Quilt",
            LoaderKind.Forge => "Forge",
            LoaderKind.NeoForge => "NeoForge",
            _ => "Minecraft"
        };
        _settingNameAutomatically = true;
        try
        {
            Name = $"{prefix}-{_selectedVersion}";
        }
        finally
        {
            _settingNameAutomatically = false;
        }
    }

    private void Cancel()
    {
        if (_isBusy && _cts is not null)
        {
            StatusText = "Отменяю…";
            _cts.Cancel();
            return;
        }
        _ctx.Dialogs.Close();
    }

    private async Task CreateAsync()
    {
        Error = "";
        var version = _selectedVersion!;
        var name = _name.Trim();

        try
        {
            new GameProfile(name, version, Memory.Value).Validate();
            if (await _ctx.Services.Profiles.FindAsync(name, CancellationToken.None) is not null)
            {
                Error = $"Профиль «{name}» уже существует. Выберите другое имя.";
                return;
            }
        }
        catch (LauncherException ex)
        {
            Error = ex.Message;
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        IsIndeterminate = true;
        Progress = 0;
        StatusText = "Начинаю установку…";

        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<InstallProgress>(p =>
        {
            // не чаще ~12 раз в секунду, иначе интерфейс тратит время на перерисовку счётчиков
            if (p.Stage != InstallStage.Completed && stopwatch.ElapsedMilliseconds - _lastProgressTick < 80) return;
            _lastProgressTick = stopwatch.ElapsedMilliseconds;
            ApplyProgress(p);
        });
        var status = new Progress<string>(text => StatusText = text);

        try
        {
            var options = new InstallOptions();
            string versionId;
            switch (_loader.Kind)
            {
                case LoaderKind.Fabric:
                    versionId = (await _ctx.Services.Fabric.InstallAsync(version, null, options, progress, ct)).VersionId;
                    break;
                case LoaderKind.Quilt:
                    versionId = (await _ctx.Services.Quilt.InstallAsync(version, null, options, progress, ct)).VersionId;
                    break;
                case LoaderKind.Forge:
                    IsIndeterminate = true;
                    versionId = (await _ctx.Services.Forge.InstallAsync(version, null, options, status, ct)).VersionId;
                    break;
                case LoaderKind.NeoForge:
                    IsIndeterminate = true;
                    versionId = (await _ctx.Services.NeoForge.InstallAsync(version, null, options, status, ct)).VersionId;
                    break;
                default:
                    versionId = (await _ctx.Services.Installer.InstallAsync(version, options, progress, ct)).VersionId;
                    break;
            }

            await _ctx.Services.Profiles.SaveAsync(new GameProfile(name, versionId, Memory.Value, Icon: Icons.Value), CancellationToken.None);

            if (_loader.Kind != LoaderKind.Vanilla)
                Directory.CreateDirectory(Path.Combine(_ctx.Services.Layout.InstanceDirectory(name), "mods"));

            await _ctx.Catalog.ReloadAsync(name);
            _ctx.Dialogs.Close();
            _ctx.Toasts.Show($"Профиль «{name}» создан.", ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            StatusText = "";
            Error = "Установка отменена. Уже скачанные файлы сохранены — повторная установка пройдёт быстрее.";
        }
        catch (LauncherException ex)
        {
            StatusText = "";
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ApplyProgress(InstallProgress p)
    {
        switch (p.Stage)
        {
            case InstallStage.VersionList:
                StatusText = "Получаю список версий…";
                break;
            case InstallStage.VersionMetadata:
                StatusText = "Читаю данные версии…";
                break;
            case InstallStage.AssetIndex:
                StatusText = "Загружаю индекс ресурсов…";
                break;
            case InstallStage.Files:
                StatusText = $"Скачиваю файлы: {p.FilesDone} из {p.FilesTotal}";
                if (p.BytesTotal > 0)
                {
                    IsIndeterminate = false;
                    Progress = Math.Clamp(p.BytesDone * 100.0 / p.BytesTotal, 0, 100);
                }
                break;
            case InstallStage.Natives:
                StatusText = "Подготавливаю библиотеки…";
                break;
            case InstallStage.Completed:
                StatusText = "Готово";
                IsIndeterminate = false;
                Progress = 100;
                break;
        }
    }
}
