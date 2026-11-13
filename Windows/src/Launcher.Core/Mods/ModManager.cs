using Launcher.Core.Downloads;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Logging;
using Launcher.Core.Versions;

namespace Launcher.Core.Mods;

/// <summary>Результат установки: что поставлено (включая зависимости) и о чём стоит предупредить.</summary>
public sealed record ModInstallResult(IReadOnlyList<InstalledMod> Installed, IReadOnlyList<string> Warnings);

/// <summary>Установка, удаление и обновление модов, ресурспаков и шейдеров профиля через каталог Modrinth.</summary>
public sealed class ModManager
{
    private const int MaxDependencyDepth = 8;
    private const int MaxPlanSize = 40;

    private readonly IModrinthClient _client;
    private readonly IFileDownloader _downloader;
    private readonly MinecraftLayout _layout;
    private readonly ILauncherLogger _logger;

    public ModManager(IModrinthClient client, IFileDownloader downloader, MinecraftLayout layout, ILauncherLogger? logger = null)
    {
        _client = client;
        _downloader = downloader;
        _layout = layout;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    /// <summary>Загрузчик для запросов к Modrinth. Моды без Fabric, Quilt, Forge или NeoForge поставить нельзя.</summary>
    /// <exception cref="ModException">В профиле без загрузчика нельзя ставить моды.</exception>
    public static string? LoaderFor(VersionLabel label, ModContentType type)
    {
        if (type != ModContentType.Mod) return null;
        return label.Loader.ModrinthLoader()
            ?? throw new ModException("Моды работают только с Fabric, Quilt, Forge или NeoForge. Создайте профиль с загрузчиком (кнопка «Новый профиль»).");
    }

    /// <summary>Установленное в профиле. Записи о файлах, которых уже нет на диске (пользователь удалил вручную), убираются.</summary>
    public IReadOnlyList<InstalledMod> GetInstalled(string profileName)
    {
        var instance = _layout.InstanceDirectory(profileName);
        var registry = new ModRegistry(instance);
        var items = registry.Load();
        var existing = items.Where(i => File.Exists(FilePath(instance, i))).ToList();
        if (existing.Count != items.Count) registry.Save(existing);
        return existing;
    }

    public async Task<ModInstallResult> InstallAsync(
        string profileName, VersionLabel label, ModProject project, ModContentType type,
        IProgress<string>? status, CancellationToken ct)
    {
        var loader = LoaderFor(label, type);
        status?.Report($"Подбираю версию «{project.Title}»…");
        var versions = await _client.GetVersionsAsync(project.ProjectId, label.GameVersion, loader, ct).ConfigureAwait(false);
        var best = ModVersionSelector.PickBest(versions)
            ?? throw new ModException(
                $"Для «{project.Title}» нет версии под Minecraft {label.GameVersion}" + (loader is null ? "." : $" и {loader}."));

        return await InstallVersionAsync(profileName, label, best, project.Title, type, autoInstalled: false, status, ct).ConfigureAwait(false);
    }

    public Task<ModInstallResult> UpdateAsync(
        string profileName, VersionLabel label, ModUpdate update, IProgress<string>? status, CancellationToken ct) =>
        InstallVersionAsync(profileName, label, update.Available, update.Installed.Title, update.Installed.Type,
            update.Installed.AutoInstalled, status, ct);

    /// <summary>Для каждого установленного ищет более новую версию, подходящую профилю.</summary>
    public async Task<IReadOnlyList<ModUpdate>> CheckUpdatesAsync(string profileName, VersionLabel label, CancellationToken ct)
    {
        var updates = new List<ModUpdate>();
        foreach (var item in GetInstalled(profileName))
        {
            ct.ThrowIfCancellationRequested();
            string? loader;
            try { loader = LoaderFor(label, item.Type); }
            catch (ModException) { continue; }

            var versions = await _client.GetVersionsAsync(item.ProjectId, label.GameVersion, loader, ct).ConfigureAwait(false);
            var best = ModVersionSelector.PickBest(versions);
            if (best is null || best.Id == item.VersionId) continue;

            // Более новой считается только версия, опубликованная позже установленной; если установленной в списке нет — берём лучшую.
            var current = versions.FirstOrDefault(v => v.Id == item.VersionId);
            if (current is not null && best.Published <= current.Published) continue;

            updates.Add(new ModUpdate(item, best));
        }
        return updates;
    }

    public void Remove(string profileName, InstalledMod item)
    {
        var instance = _layout.InstanceDirectory(profileName);
        var registry = new ModRegistry(instance);
        try
        {
            var path = FilePath(instance, item);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModException($"Не удалось удалить файл «{item.FileName}». Закройте игру, если она запущена, и повторите.", ex);
        }

        registry.Save(registry.Load().Where(i => !(i.ProjectId == item.ProjectId && i.Type == item.Type)).ToList());
    }

    // ---- установка версии со всеми зависимостями

    private sealed record PlanItem(ModVersion Version, string Title, ModContentType Type, bool Auto);

    private async Task<ModInstallResult> InstallVersionAsync(
        string profileName, VersionLabel label, ModVersion root, string rootTitle, ModContentType type, bool autoInstalled,
        IProgress<string>? status, CancellationToken ct)
    {
        var loader = LoaderFor(label, type);
        var instance = _layout.InstanceDirectory(profileName);
        var registry = new ModRegistry(instance);
        var installed = registry.Load();
        var warnings = new List<string>();

        var plan = new List<PlanItem> { new(root, rootTitle, type, autoInstalled) };
        await ResolveDependenciesAsync(plan, installed, label, loader, type, warnings, status, ct).ConfigureAwait(false);
        await FillTitlesAsync(plan, ct).ConfigureAwait(false);

        var done = new List<InstalledMod>();
        for (var i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = plan[i];
            status?.Report($"Скачиваю {item.Title} ({i + 1} из {plan.Count})…");

            var entry = await DownloadAsync(instance, item, installed, ct).ConfigureAwait(false);

            // Новая запись заменяет прежнюю того же проекта; если файл назывался иначе, старый удаляется.
            var previous = installed.FirstOrDefault(m => m.ProjectId == entry.ProjectId && m.Type == entry.Type);
            if (previous is not null)
            {
                installed.Remove(previous);
                if (!string.Equals(previous.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase))
                    TryDelete(FilePath(instance, previous));
            }
            installed.Add(entry);
            registry.Save(installed); // после каждого файла: при сбое на середине записано всё, что успело встать
            done.Add(entry);
        }

        return new ModInstallResult(done, warnings);
    }

    private async Task ResolveDependenciesAsync(
        List<PlanItem> plan, List<InstalledMod> installed, VersionLabel label, string? loader, ModContentType type,
        List<string> warnings, IProgress<string>? status, CancellationToken ct)
    {
        var queue = new Queue<(ModVersion Version, int Depth)>();
        queue.Enqueue((plan[0].Version, 0));

        while (queue.Count > 0)
        {
            var (version, depth) = queue.Dequeue();
            foreach (var dep in version.Dependencies)
            {
                if (dep.Kind == ModDependencyKind.Incompatible)
                {
                    if (dep.ProjectId is not null && installed.Any(m => m.ProjectId == dep.ProjectId))
                        warnings.Add("Один из установленных модов объявлен несовместимым с добавляемым. Возможны ошибки при запуске.");
                    continue;
                }
                if (dep.Kind != ModDependencyKind.Required) continue;

                if (depth >= MaxDependencyDepth || plan.Count >= MaxPlanSize)
                    throw new ModException("У мода слишком длинная цепочка зависимостей. Установка остановлена.");

                ct.ThrowIfCancellationRequested();
                var resolved = await ResolveAsync(dep, label, loader, ct).ConfigureAwait(false);
                if (resolved is null)
                {
                    warnings.Add("Не найдена подходящая версия одной из обязательных зависимостей: мод может не запуститься.");
                    continue;
                }
                if (installed.Any(m => m.ProjectId == resolved.ProjectId) || plan.Any(p => p.Version.ProjectId == resolved.ProjectId))
                    continue;

                status?.Report("Нужна зависимость, добавляю…");
                plan.Add(new PlanItem(resolved, resolved.Name, type, Auto: true));
                queue.Enqueue((resolved, depth + 1));
            }
        }
    }

    private async Task<ModVersion?> ResolveAsync(ModDependency dep, VersionLabel label, string? loader, CancellationToken ct)
    {
        if (dep.VersionId is not null)
        {
            var exact = await _client.GetVersionAsync(dep.VersionId, ct).ConfigureAwait(false);
            if (exact is not null) return exact;
        }
        if (dep.ProjectId is null) return null;

        var versions = await _client.GetVersionsAsync(dep.ProjectId, label.GameVersion, loader, ct).ConfigureAwait(false);
        return ModVersionSelector.PickBest(versions);
    }

    /// <summary>Подписи зависимостей — названия проектов. Если узнать не удалось, остаются названия версий: установке это не мешает.</summary>
    private async Task FillTitlesAsync(List<PlanItem> plan, CancellationToken ct)
    {
        var ids = plan.Where(p => p.Auto).Select(p => p.Version.ProjectId).Distinct().ToList();
        if (ids.Count == 0) return;

        try
        {
            var projects = await _client.GetProjectsAsync(ids, ct).ConfigureAwait(false);
            for (var i = 0; i < plan.Count; i++)
                if (plan[i].Auto && projects.FirstOrDefault(p => p.ProjectId == plan[i].Version.ProjectId) is { } project)
                    plan[i] = plan[i] with { Title = project.Title };
        }
        catch (ModException ex)
        {
            _logger.Warn("Не удалось получить названия зависимостей.", ex);
        }
    }

    private async Task<InstalledMod> DownloadAsync(string instance, PlanItem item, List<InstalledMod> installed, CancellationToken ct)
    {
        var file = item.Version.File;
        ValidateFile(file);

        var folder = Path.Combine(instance, item.Type.FolderName());
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, file.FileName);

        // Файл с таким именем уже есть, но поставил его не лаунчер (или другая версия того же проекта): чужое не затираем.
        var ours = installed.Any(m => m.Type == item.Type && string.Equals(m.FileName, file.FileName, StringComparison.OrdinalIgnoreCase));
        if (File.Exists(target) && !ours && !await FileIntegrity.MatchesAsync(target, file.Sha1, file.Size > 0 ? file.Size : null, ct).ConfigureAwait(false))
            throw new ModException(
                $"В папке «{item.Type.FolderName()}» уже лежит другой файл «{file.FileName}». Переименуйте или удалите его и повторите.");

        try
        {
            await _downloader.DownloadAsync(
                new DownloadRequest(file.Url, target, file.Sha1, file.Size > 0 ? file.Size : null), null, ct).ConfigureAwait(false);
        }
        catch (LauncherException ex) when (ex is not ModException)
        {
            throw new ModException($"Не удалось скачать «{item.Title}»: {ex.Message}", ex);
        }

        return new InstalledMod(item.Version.ProjectId, item.Version.Id, item.Title, item.Type, file.FileName, file.Sha1,
            item.Version.VersionNumber, item.Auto, DateTimeOffset.UtcNow);
    }

    /// <summary>Файлы берутся только из каталога Modrinth, по https и с контрольной суммой; имя файла не должно выходить из папки.</summary>
    private static void ValidateFile(ModFile file)
    {
        var host = file.Url.Host;
        var trusted = file.Url.Scheme == Uri.UriSchemeHttps &&
                      (host.Equals("modrinth.com", StringComparison.OrdinalIgnoreCase) ||
                       host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase));
        if (!trusted)
            throw new ModException("Файл предлагается не с серверов Modrinth. Из соображений безопасности он не будет скачан.");

        if (string.IsNullOrWhiteSpace(file.Sha1))
            throw new ModException("У файла нет контрольной суммы, проверить его целостность нельзя. Установка отменена.");

        if (file.FileName != Path.GetFileName(file.FileName) || file.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            file.FileName.Contains('\\') || file.FileName.Contains('/') ||
            file.FileName is "." or "..")
            throw new ModException("Недопустимое имя файла в каталоге. Установка отменена.");
    }

    private static string FilePath(string instance, InstalledMod item) =>
        Path.Combine(instance, item.Type.FolderName(), Path.GetFileName(item.FileName));

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Не удалось удалить старый файл {Path.GetFileName(path)}.", ex);
        }
    }
}
