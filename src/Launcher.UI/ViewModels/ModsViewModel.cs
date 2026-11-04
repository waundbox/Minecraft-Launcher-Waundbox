using System.Collections.ObjectModel;
using System.Globalization;
using Launcher.Core.Errors;
using Launcher.Core.Mods;
using Launcher.Core.Versions;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

public sealed record ContentTypeChoice(ModContentType Type, string Title);

public sealed record ModsSectionChoice(ModsSection Section, string Title);

public sealed record SortChoice(ModSort Sort, string Title);

public enum ModsSection { Catalog, Recommended, Installed }

/// <summary>Карточка проекта в каталоге или в «рекомендуем».</summary>
internal sealed class ModCardItem : ObservableObject
{
    private bool _isInstalled;

    public ModCardItem(ModProject project, string? reason = null)
    {
        Project = project;
        Reason = reason;
    }

    public ModProject Project { get; }
    public string Title => Project.Title;
    public string Description => string.IsNullOrWhiteSpace(Reason) ? Project.Description : Reason;
    public string? Reason { get; }
    public string? IconUrl => Project.IconUrl;
    public string Meta => (Project.Author is { Length: > 0 } a ? $"{a} · " : "") + $"{ModsFormat.Downloads(Project.Downloads)} загрузок";

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (Set(ref _isInstalled, value)) OnPropertyChanged(nameof(ButtonText));
        }
    }

    public string ButtonText => _isInstalled ? "Установлено" : "Установить";

    public AsyncCommand InstallCommand { get; set; } = null!;
}

/// <summary>Строка списка «Установлено».</summary>
internal sealed class InstalledItem : ObservableObject
{
    private ModUpdate? _update;

    public InstalledItem(InstalledMod mod) => Mod = mod;

    public InstalledMod Mod { get; }
    public string Title => Mod.Title;
    public string Subtitle =>
        $"{Mod.FileName} · версия {Mod.VersionNumber}" + (Mod.AutoInstalled ? " · зависимость" : "");

    public ModUpdate? Update
    {
        get => _update;
        set
        {
            if (!Set(ref _update, value)) return;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateText));
        }
    }

    public bool HasUpdate => _update is not null;
    public string UpdateText => _update is null ? "" : $"Обновить до {_update.Available.VersionNumber}";

    public AsyncCommand RemoveCommand { get; set; } = null!;
    public AsyncCommand UpdateCommand { get; set; } = null!;
}

internal static class ModsFormat
{
    public static string Downloads(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " млн",
        >= 1_000 => (n / 1_000.0).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " тыс.",
        _ => n.ToString(CultureInfo.InvariantCulture)
    };

}

/// <summary>Окно «Моды» профиля: каталог Modrinth, рекомендуемое, установленное, обновления.</summary>
internal sealed class ModsViewModel : ObservableObject
{
    private const int PageSize = 20;

    private readonly UiContext _ctx;
    private readonly ProfileItem _item;
    private readonly VersionLabel _label;
    private CancellationTokenSource? _cts;
    private ContentTypeChoice _selectedType;
    private ModsSection _section = ModsSection.Catalog;
    private SortChoice _selectedSort;
    private string _query = "";
    private bool _isBusy;
    private string _status = "";
    private string _notice = "";
    private int _totalHits;
    private bool _installing;

    public ModsViewModel(UiContext ctx, ProfileItem item)
    {
        _ctx = ctx;
        _item = item;
        _label = item.Label;

        TypeChoices =
        [
            new(ModContentType.Mod, "Моды"),
            new(ModContentType.ResourcePack, "Ресурспаки"),
            new(ModContentType.Shader, "Шейдеры")
        ];
        SectionChoices =
        [
            new(ModsSection.Catalog, "Каталог"),
            new(ModsSection.Recommended, "Рекомендуем"),
            new(ModsSection.Installed, "Установлено")
        ];
        SortChoices =
        [
            new(ModSort.Relevance, "Подходящие"),
            new(ModSort.Downloads, "Популярные"),
            new(ModSort.Updated, "Обновлённые"),
            new(ModSort.Newest, "Новые")
        ];
        _selectedType = TypeChoices[0];
        _selectedSort = SortChoices[1];

        SearchCommand = new AsyncCommand(() => SearchAsync(reset: true), () => !_isBusy);
        LoadMoreCommand = new AsyncCommand(() => SearchAsync(reset: false), () => !_isBusy && CanLoadMore);
        CheckUpdatesCommand = new AsyncCommand(CheckUpdatesAsync, () => !_isBusy);
        UpdateAllCommand = new AsyncCommand(UpdateAllAsync, () => !_isBusy && Installed.Any(i => i.HasUpdate));
        CloseCommand = new RelayCommand(Close);
        OpenFolderCommand = new RelayCommand(OpenFolder);

        Installed.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasInstalled));

        _ = ReloadSectionAsync();
    }

    public bool HasInstalled => Installed.Count > 0;

    public string ProfileName => _item.Name;
    public string ProfileText => $"{_item.Name} · {_item.VersionText}";

    public IReadOnlyList<ContentTypeChoice> TypeChoices { get; }
    public IReadOnlyList<ModsSectionChoice> SectionChoices { get; }
    public IReadOnlyList<SortChoice> SortChoices { get; }

    public ObservableCollection<ModCardItem> Results { get; } = new();
    public ObservableCollection<ModCardItem> Recommended { get; } = new();
    public ObservableCollection<InstalledItem> Installed { get; } = new();

    public AsyncCommand SearchCommand { get; }
    public AsyncCommand LoadMoreCommand { get; }
    public AsyncCommand CheckUpdatesCommand { get; }
    public AsyncCommand UpdateAllCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public ContentTypeChoice SelectedType
    {
        get => _selectedType;
        set
        {
            if (value is null || !Set(ref _selectedType, value)) return;
            OnPropertyChanged(nameof(ShopHint));
            _ = ReloadSectionAsync();
        }
    }

    public ModsSectionChoice SelectedSection
    {
        get => SectionChoices.First(s => s.Section == _section);
        set
        {
            if (value is null || value.Section == _section) return;
            _section = value.Section;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCatalog));
            OnPropertyChanged(nameof(ShowRecommended));
            OnPropertyChanged(nameof(ShowInstalled));
            _ = ReloadSectionAsync();
        }
    }

    public SortChoice SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value is null || !Set(ref _selectedSort, value)) return;
            _ = SearchAsync(reset: true);
        }
    }

    public bool ShowCatalog => _section == ModsSection.Catalog;
    public bool ShowRecommended => _section == ModsSection.Recommended;
    public bool ShowInstalled => _section == ModsSection.Installed;

    public string Query { get => _query; set => Set(ref _query, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Status { get => _status; private set { if (Set(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;

    /// <summary>Сообщение вместо списка: «ничего не найдено», «нужен загрузчик» и т.п.</summary>
    public string Notice { get => _notice; private set { if (Set(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool HasNotice => _notice.Length > 0;

    public bool CanLoadMore => Results.Count > 0 && Results.Count < _totalHits;

    /// <summary>Подсказка под строкой поиска: у шейдеров свои особенности.</summary>
    public string ShopHint => _selectedType.Type switch
    {
        ModContentType.Shader => "Шейдеры обычно работают только вместе с модом Iris (Fabric) или OptiFine — Iris есть в разделе «Рекомендуем».",
        ModContentType.ResourcePack => "Ресурспак включается в игре: Настройки → Ресурспаки.",
        _ => "Моды подбираются под версию и загрузчик этого профиля."
    };

    // ---- разделы

    private async Task ReloadSectionAsync()
    {
        Notice = "";
        Status = "";
        switch (_section)
        {
            case ModsSection.Catalog:
                await SearchAsync(reset: true);
                break;
            case ModsSection.Recommended:
                await LoadRecommendedAsync();
                break;
            default:
                RefreshInstalled();
                break;
        }
    }

    private CancellationToken NewOperation()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        // Прерванный поиск сам индикатор уже не погасит; установка же идёт независимо и свой индикатор держит сама.
        if (!_installing)
        {
            IsBusy = false;
            Status = "";
        }
        return _cts.Token;
    }

    /// <summary>false — в профиле без загрузчика моды ставить нельзя (сообщение выставлено).</summary>
    private bool CheckLoader()
    {
        if (_selectedType.Type == ModContentType.Mod && _label.Loader.ModrinthLoader() is null)
        {
            Notice = "Для модов нужен профиль с Fabric, Quilt, Forge или NeoForge. Создайте такой профиль кнопкой «Новый профиль». " +
                     "Ресурспаки и шейдеры можно ставить и в обычный профиль.";
            return false;
        }
        return true;
    }

    private async Task SearchAsync(bool reset)
    {
        if (_section != ModsSection.Catalog) return;
        var ct = NewOperation();
        if (reset)
        {
            Results.Clear();
            _totalHits = 0;
        }
        Notice = "";
        if (!CheckLoader())
        {
            OnPropertyChanged(nameof(CanLoadMore));
            return;
        }

        IsBusy = true;
        Status = "Ищу в каталоге Modrinth…";
        try
        {
            var query = new ModSearchQuery(_query.Trim(), _selectedType.Type, _label.GameVersion,
                _label.Loader.ModrinthLoader(), _selectedSort.Sort, Results.Count, PageSize);
            var result = await _ctx.Services.Modrinth.SearchAsync(query, ct);
            if (ct.IsCancellationRequested) return;

            _totalHits = result.TotalHits;
            var installed = InstalledProjectIds();
            foreach (var hit in result.Hits) Results.Add(MakeCard(hit, null, installed));

            if (Results.Count == 0)
                Notice = $"Ничего не найдено для Minecraft {_label.GameVersion}. Попробуйте другой запрос.";
        }
        catch (LauncherException ex)
        {
            if (!ct.IsCancellationRequested) Notice = ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsBusy = false;
                Status = "";
                OnPropertyChanged(nameof(CanLoadMore));
            }
        }
    }

    private async Task LoadRecommendedAsync()
    {
        var ct = NewOperation();
        Recommended.Clear();
        if (!CheckLoader()) return;

        var wanted = RecommendedContent.For(_selectedType.Type, _label.Loader);
        if (wanted.Count == 0)
        {
            Notice = "Для этого типа пока нет рекомендаций.";
            return;
        }

        IsBusy = true;
        Status = "Загружаю рекомендации…";
        try
        {
            var projects = await _ctx.Services.Modrinth.GetProjectsAsync(wanted.Select(w => w.Slug).ToList(), ct);
            if (ct.IsCancellationRequested) return;

            var installed = InstalledProjectIds();
            foreach (var item in wanted)
            {
                var project = projects.FirstOrDefault(p => string.Equals(p.Slug, item.Slug, StringComparison.OrdinalIgnoreCase));
                if (project is not null) Recommended.Add(MakeCard(project, item.Reason, installed));
            }
            if (Recommended.Count == 0) Notice = "Не удалось получить рекомендации.";
        }
        catch (LauncherException ex)
        {
            if (!ct.IsCancellationRequested) Notice = ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsBusy = false;
                Status = "";
            }
        }
    }

    private HashSet<string> InstalledProjectIds() =>
        _ctx.Services.Mods.GetInstalled(_item.Name).Select(i => i.ProjectId).ToHashSet();

    private ModCardItem MakeCard(ModProject project, string? reason, HashSet<string> installed)
    {
        var card = new ModCardItem(project, reason) { IsInstalled = installed.Contains(project.ProjectId) };
        card.InstallCommand = new AsyncCommand(() => InstallAsync(card), () => !_isBusy && !card.IsInstalled);
        return card;
    }

    private void RefreshInstalled()
    {
        Installed.Clear();
        Notice = "";
        try
        {
            foreach (var mod in _ctx.Services.Mods.GetInstalled(_item.Name).Where(m => m.Type == _selectedType.Type)
                         .OrderBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new InstalledItem(mod);
                row.RemoveCommand = new AsyncCommand(() => RemoveAsync(row), () => !_isBusy);
                row.UpdateCommand = new AsyncCommand(() => UpdateOneAsync(row), () => !_isBusy && row.HasUpdate);
                Installed.Add(row);
            }
        }
        catch (LauncherException ex)
        {
            Notice = ex.Message;
            return;
        }

        if (Installed.Count == 0)
            Notice = $"Здесь пока ничего нет. {_selectedType.Title}, установленные вручную в папку профиля, лаунчер не показывает — только поставленные через него.";
    }

    // ---- действия

    private async Task InstallAsync(ModCardItem card)
    {
        _installing = true;
        IsBusy = true;
        var status = new Progress<string>(text => Status = text);
        try
        {
            var result = await _ctx.Services.Mods.InstallAsync(_item.Name, _label, card.Project, _selectedType.Type, status, CancellationToken.None);
            card.IsInstalled = true;

            var extra = result.Installed.Count - 1;
            _ctx.Toasts.Show(extra > 0
                ? $"«{card.Title}» установлен, вместе с {extra} зависимост{(extra == 1 ? "ью" : "ями")}."
                : $"«{card.Title}» установлен.", ToastKind.Success);
            foreach (var warning in result.Warnings.Distinct()) _ctx.Toasts.Show(warning, ToastKind.Warning);
            if (_item.IsRunning) _ctx.Toasts.Show("Профиль сейчас запущен: изменения заработают после перезапуска игры.", ToastKind.Info);
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Error);
        }
        finally
        {
            _installing = false;
            IsBusy = false;
            Status = "";
        }
    }

    private Task RemoveAsync(InstalledItem row)
    {
        try
        {
            _ctx.Services.Mods.Remove(_item.Name, row.Mod);
            Installed.Remove(row);
            foreach (var card in Results.Concat(Recommended).Where(c => c.Project.ProjectId == row.Mod.ProjectId)) card.IsInstalled = false;
            _ctx.Toasts.Show($"«{row.Title}» удалён.", ToastKind.Info);
            if (Installed.Count == 0) RefreshInstalled();
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Error);
        }
        return Task.CompletedTask;
    }

    private async Task CheckUpdatesAsync()
    {
        var ct = NewOperation();
        IsBusy = true;
        Status = "Проверяю обновления…";
        try
        {
            var updates = await _ctx.Services.Mods.CheckUpdatesAsync(_item.Name, _label, ct);
            if (ct.IsCancellationRequested) return;
            foreach (var row in Installed)
                row.Update = updates.FirstOrDefault(u => u.Installed.ProjectId == row.Mod.ProjectId && u.Installed.Type == row.Mod.Type);

            _ctx.Toasts.Show(updates.Count == 0 ? "Всё актуально." : $"Доступно обновлений: {updates.Count}.", ToastKind.Info);
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Error);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsBusy = false;
                Status = "";
            }
        }
    }

    private async Task UpdateOneAsync(InstalledItem row)
    {
        if (row.Update is not { } update) return;
        _installing = true;
        IsBusy = true;
        var status = new Progress<string>(text => Status = text);
        try
        {
            await _ctx.Services.Mods.UpdateAsync(_item.Name, _label, update, status, CancellationToken.None);
            _ctx.Toasts.Show($"«{row.Title}» обновлён.", ToastKind.Success);
            RefreshInstalled();
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Error);
        }
        finally
        {
            _installing = false;
            IsBusy = false;
            Status = "";
        }
    }

    private async Task UpdateAllAsync()
    {
        _installing = true;
        IsBusy = true;
        var status = new Progress<string>(text => Status = text);
        var done = 0;
        try
        {
            foreach (var row in Installed.Where(i => i.HasUpdate).ToList())
            {
                await _ctx.Services.Mods.UpdateAsync(_item.Name, _label, row.Update!, status, CancellationToken.None);
                done++;
            }
            _ctx.Toasts.Show($"Обновлено: {done}.", ToastKind.Success);
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Error);
        }
        finally
        {
            _installing = false;
            IsBusy = false;
            Status = "";
            RefreshInstalled();
        }
    }

    private void OpenFolder()
    {
        var folder = Path.Combine(_ctx.Services.Layout.InstanceDirectory(_item.Name), _selectedType.Type.FolderName());
        Directory.CreateDirectory(folder);
        if (!ShellHelper.OpenFolder(folder)) _ctx.Toasts.Show("Не удалось открыть папку.", ToastKind.Error);
    }

    private void Close()
    {
        _cts?.Cancel();
        _ctx.Dialogs.Close();
    }
}
