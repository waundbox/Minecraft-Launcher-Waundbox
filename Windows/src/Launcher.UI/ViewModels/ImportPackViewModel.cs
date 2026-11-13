using System.Text.RegularExpressions;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Modpacks;
using Launcher.Core.Profiles;
using Launcher.Core.Versions;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Диалог «Импорт сборки Modrinth (.mrpack)»: ставит игру и загрузчик, скачивает файлы сборки, создаёт профиль.</summary>
internal sealed class ImportPackViewModel : ObservableObject
{
    private readonly UiContext _ctx;
    private readonly string _path;
    private readonly MrpackInfo _info;
    private CancellationTokenSource? _cts;
    private string _name;
    private bool _isBusy;
    private double _progress;
    private string _statusText = "";
    private string _error = "";

    public ImportPackViewModel(UiContext ctx, string path, MrpackInfo info, string suggestedName)
    {
        _ctx = ctx;
        _path = path;
        _info = info;
        _name = suggestedName;

        // Сборки обычно тяжелее чистой игры: берём побольше памяти, если компьютер позволяет.
        Memory = new MemorySelection(MemorySelection.DetectPhysicalMegabytes() >= 12288 ? 6144 : MemorySelection.DetectPhysicalMegabytes() >= 8192 ? 4096 : 2048);

        ImportCommand = new AsyncCommand(ImportAsync, () => !_isBusy);
        CancelCommand = new RelayCommand(Cancel);
    }

    public string PackName => _info.Name;
    public string? Summary => _info.Summary;
    public bool HasSummary => !string.IsNullOrWhiteSpace(_info.Summary);

    public string Details
    {
        get
        {
            var loader = _info.Loader switch
            {
                LoaderKind.Fabric => $"Fabric {_info.LoaderVersion}",
                LoaderKind.Quilt => $"Quilt {_info.LoaderVersion}",
                LoaderKind.Forge => $"Forge {_info.LoaderVersion}",
                LoaderKind.NeoForge => $"NeoForge {_info.LoaderVersion}",
                _ => "без загрузчика"
            };
            return $"Minecraft {_info.GameVersion} · {loader} · файлов для скачивания: {_info.Files.Count}";
        }
    }

    public MemorySelection Memory { get; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsNotBusy => !_isBusy;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string Error { get => _error; private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    public AsyncCommand ImportCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>Имя профиля из названия сборки: только то, что разрешено в именах профилей, и без повторов.</summary>
    public static async Task<string> SuggestNameAsync(UiContext ctx, string packName)
    {
        var cleaned = Regex.Replace(packName, @"[^A-Za-z0-9 _.\-]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '.', '-', '_');
        if (cleaned.Length == 0) cleaned = "Modpack";
        if (cleaned.Length > 36) cleaned = cleaned[..36].TrimEnd(' ', '.');

        var existing = (await ctx.Services.Profiles.ListAsync(CancellationToken.None)).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = cleaned;
        for (var i = 2; existing.Contains(candidate); i++) candidate = $"{cleaned} {i}";
        return candidate;
    }

    private void Cancel()
    {
        if (_isBusy) _cts?.Cancel();
        else _ctx.Dialogs.Close();
    }

    private async Task ImportAsync()
    {
        Error = "";
        var name = _name.Trim();
        try
        {
            new GameProfile(name, _info.GameVersion, Memory.Value).Validate();
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
        Progress = 0;
        StatusText = "Ставлю игру и загрузчик…";

        try
        {
            // 1) игра и загрузчик (первые 40% полосы)
            var gameProgress = new Progress<InstallProgress>(p =>
            {
                if (p.Stage == InstallStage.Files)
                {
                    StatusText = $"Скачиваю файлы игры: {p.FilesDone} из {p.FilesTotal}";
                    if (p.BytesTotal > 0) Progress = Math.Clamp(p.BytesDone * 40.0 / p.BytesTotal, 0, 40);
                }
            });
            var status = new Progress<string>(text => StatusText = text);
            var options = new InstallOptions();

            string versionId;
            switch (_info.Loader)
            {
                case LoaderKind.Fabric:
                    versionId = (await _ctx.Services.Fabric.InstallAsync(_info.GameVersion, _info.LoaderVersion, options, gameProgress, ct)).VersionId;
                    break;
                case LoaderKind.Quilt:
                    versionId = (await _ctx.Services.Quilt.InstallAsync(_info.GameVersion, _info.LoaderVersion, options, gameProgress, ct)).VersionId;
                    break;
                case LoaderKind.Forge:
                    versionId = (await _ctx.Services.Forge.InstallAsync(_info.GameVersion, _info.LoaderVersion, options, status, ct)).VersionId;
                    break;
                case LoaderKind.NeoForge:
                    versionId = (await _ctx.Services.NeoForge.InstallAsync(_info.GameVersion, _info.LoaderVersion, options, status, ct)).VersionId;
                    break;
                default:
                    versionId = (await _ctx.Services.Installer.InstallAsync(_info.GameVersion, options, gameProgress, ct)).VersionId;
                    break;
            }
            Progress = 40;

            // 2) файлы сборки (остальные 60%)
            var packProgress = new Progress<MrpackProgress>(p =>
            {
                StatusText = p.Text;
                Progress = 40 + p.Fraction * 60;
            });
            var result = await _ctx.Services.Mrpack.ImportAsync(name, _path, _info, packProgress, ct);

            // 3) профиль записывается последним: при сбое «полупрофиля» в списке не появляется
            await _ctx.Services.Profiles.SaveAsync(new GameProfile(name, versionId, Memory.Value, Icon: ProfileIcons.PickRandom()), CancellationToken.None);
            await _ctx.Catalog.ReloadAsync(name);
            _ctx.Dialogs.Close();
            _ctx.Toasts.Show($"Сборка «{_info.Name}» установлена как профиль «{name}».", ToastKind.Success);
            foreach (var warning in result.Warnings.Distinct().Take(3)) _ctx.Toasts.Show(warning, ToastKind.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText = "";
            Error = "Импорт отменён. Уже скачанные файлы сохранены — повторный импорт пройдёт быстрее.";
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
}
