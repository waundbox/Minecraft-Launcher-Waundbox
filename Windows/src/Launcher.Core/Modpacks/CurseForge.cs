using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Core.Errors;
using Launcher.Core.Versions;

namespace Launcher.Core.Modpacks;

public enum ModpackFormat { Unknown, Modrinth, CurseForge }

/// <summary>Одна запись из manifest.json сборки CurseForge: проект и файл.</summary>
public sealed record CurseForgeFileRef(long ProjectId, long FileId, bool Required);

/// <summary>Содержимое manifest.json сборки CurseForge.</summary>
public sealed record CurseForgeManifest(
    string Name, string? Version, string GameVersion, LoaderKind Loader, string? LoaderVersion,
    string OverridesFolder, IReadOnlyList<CurseForgeFileRef> Files);

/// <summary>Файл, описанный API CurseForge. <paramref name="DownloadUrl"/> пуст, если автор запретил скачивание сторонними программами.</summary>
public sealed record CurseForgeFile(long Id, long ModId, string FileName, Uri? DownloadUrl, string? Sha1, long Length);

/// <summary>Доступ к API CurseForge (https://docs.curseforge.com/rest-api/) — нужен ключ API.</summary>
public interface ICurseForgeClient
{
    /// <summary>Сведения о файлах по их номерам (POST /v1/mods/files). Найденные файлы возвращаются в любом порядке.</summary>
    /// <exception cref="ModException">Нет ключа, ключ отклонён или сервис недоступен.</exception>
    Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(IReadOnlyList<long> fileIds, CancellationToken ct);
}

/// <summary>Определение формата сборки и чтение manifest.json (формат: https://gdlauncher.com/docs/modpack-manifest-format).</summary>
public static partial class CurseForgePackReader
{
    public const string ManifestName = "manifest.json";
    private const long MaxManifestBytes = 20 * 1024 * 1024;
    private const int MaxFiles = 5000;

    [GeneratedRegex(@"^[A-Za-z0-9 _.\-]{1,64}$")]
    private static partial Regex SafeFolder();

    /// <summary>Смотрит внутрь архива: modrinth.index.json — Modrinth, manifest.json — CurseForge.</summary>
    public static ModpackFormat Detect(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry(MrpackReader.IndexName) is not null) return ModpackFormat.Modrinth;
            if (zip.GetEntry(ManifestName) is not null) return ModpackFormat.CurseForge;
            return ModpackFormat.Unknown;
        }
        catch (InvalidDataException ex)
        {
            throw new ModException("Файл повреждён или не является архивом сборки.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModException("Не удалось прочитать файл сборки.", ex);
        }
    }

    /// <exception cref="ModException">Файл не похож на сборку CurseForge или рассчитан на неподдерживаемый загрузчик.</exception>
    public static CurseForgeManifest Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(ManifestName)
                ?? throw new ModException("В архиве нет файла manifest.json — это не сборка CurseForge.");
            if (entry.Length > MaxManifestBytes) throw new ModException("Описание сборки слишком большое. Файл отклонён.");

            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            return Parse(document.RootElement);
        }
        catch (InvalidDataException ex)
        {
            throw new ModException("Файл повреждён или не является архивом сборки.", ex);
        }
        catch (JsonException ex)
        {
            throw new ModException("Описание сборки (manifest.json) повреждено.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModException("Не удалось прочитать файл сборки.", ex);
        }
    }

    public static CurseForgeManifest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new ModException("Описание сборки имеет неверный формат.");
        if (Str(root, "manifestType") != "minecraftModpack") throw new ModException("manifest.json не описывает сборку Minecraft.");

        var name = Str(root, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new ModException("В сборке не указано название.");

        if (!root.TryGetProperty("minecraft", out var mc) || mc.ValueKind != JsonValueKind.Object)
            throw new ModException("В сборке не указана версия Minecraft.");
        var game = Str(mc, "version") ?? throw new ModException("В сборке не указана версия Minecraft.");

        var loader = LoaderKind.Vanilla;
        string? loaderVersion = null;
        if (mc.TryGetProperty("modLoaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array && loaders.GetArrayLength() > 0)
        {
            JsonElement chosen = loaders[0];
            foreach (var l in loaders.EnumerateArray())
                if (l.ValueKind == JsonValueKind.Object && l.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True) { chosen = l; break; }

            var id = chosen.ValueKind == JsonValueKind.Object ? Str(chosen, "id") : null;
            var dash = id?.IndexOf('-') ?? -1;
            if (id is null || dash <= 0 || dash == id.Length - 1)
                throw new ModException("В сборке неверно указан загрузчик модов.");
            loaderVersion = id[(dash + 1)..];
            loader = id[..dash].ToLowerInvariant() switch
            {
                "forge" => LoaderKind.Forge,
                "fabric" => LoaderKind.Fabric,
                "neoforge" => LoaderKind.NeoForge,
                "quilt" => LoaderKind.Quilt,
                var other => throw new ModException($"Сборка для загрузчика «{other}» не поддерживается (доступны Fabric, Quilt, Forge и NeoForge).")
            };
        }

        var overrides = Str(root, "overrides") ?? "overrides";
        if (!SafeFolder().IsMatch(overrides) || overrides is "." or "..")
            throw new ModException("В сборке указана небезопасная папка настроек. Файл отклонён.");

        var files = new List<CurseForgeFileRef>();
        if (root.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (files.Count >= MaxFiles) throw new ModException("В сборке слишком много файлов. Файл отклонён.");
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("projectID", out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetInt64(out var projectId) ||
                    !item.TryGetProperty("fileID", out var f) || f.ValueKind != JsonValueKind.Number || !f.TryGetInt64(out var fileId) || projectId <= 0 || fileId <= 0)
                    throw new ModException("В списке файлов сборки есть некорректная запись.");
                var required = !item.TryGetProperty("required", out var r) || r.ValueKind != JsonValueKind.False;
                files.Add(new CurseForgeFileRef(projectId, fileId, required));
            }
        }

        return new CurseForgeManifest(name.Trim(), Str(root, "version"), game, loader, loaderVersion, overrides, files);
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Разбор ответа POST /v1/mods/files.</summary>
public static partial class CurseForgeParser
{
    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex Sha1Pattern();

    public static IReadOnlyList<CurseForgeFile> ParseFiles(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new ModException("Ответ CurseForge имеет неожиданный формат.");

            var result = new List<CurseForgeFile>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var id) || !id.TryGetInt64(out var fileId) ||
                    !item.TryGetProperty("fileName", out var fn) || fn.ValueKind != JsonValueKind.String) continue;

                var modId = item.TryGetProperty("modId", out var m) && m.TryGetInt64(out var mid) ? mid : 0;
                Uri? url = null;
                if (item.TryGetProperty("downloadUrl", out var du) && du.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(du.GetString(), UriKind.Absolute, out var parsed)) url = parsed;

                // Алгоритм определяется по длине значения (SHA-1 — 40 hex-символов), а не по числовому коду алгоритма.
                string? sha1 = null;
                if (item.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Array)
                    foreach (var h in hashes.EnumerateArray())
                        if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("value", out var hv) && hv.ValueKind == JsonValueKind.String &&
                            Sha1Pattern().IsMatch(hv.GetString() ?? "")) { sha1 = hv.GetString()!.ToLowerInvariant(); break; }

                var length = item.TryGetProperty("fileLength", out var fl) && fl.TryGetInt64(out var len) ? len : 0;
                result.Add(new CurseForgeFile(fileId, modId, fn.GetString()!, url, sha1, length));
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new ModException("Ответ CurseForge не является корректным JSON.", ex);
        }
    }
}

/// <summary>
/// Превращает manifest.json в описание для общего импортёра (<see cref="MrpackInfo"/>): спрашивает у CurseForge адреса файлов.
/// Файлы, которые автор запретил скачивать сторонним программам, НЕ обходятся — они попадают в список для ручной загрузки.
/// </summary>
public static class CurseForgeResolver
{
    private const int BatchSize = 50;
    private const string CdnSuffix = ".forgecdn.net";

    public static async Task<MrpackInfo> ResolveAsync(CurseForgeManifest manifest, ICurseForgeClient client, CancellationToken ct)
    {
        var found = new Dictionary<long, CurseForgeFile>();
        var ids = manifest.Files.Select(f => f.FileId).Distinct().ToList();
        for (var i = 0; i < ids.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in await client.GetFilesAsync(ids.Skip(i).Take(BatchSize).ToList(), ct).ConfigureAwait(false))
                found[file.Id] = file;
        }

        var files = new List<MrpackFile>();
        var notes = new List<string>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manual = 0;

        foreach (var reference in manifest.Files)
        {
            if (!found.TryGetValue(reference.FileId, out var file))
            {
                notes.Add($"Файл {reference.FileId} (проект {reference.ProjectId}) не найден на CurseForge — пропущен.");
                continue;
            }

            if (file.DownloadUrl is null)
            {
                manual++;
                notes.Add($"Скачайте вручную с CurseForge и положите в папку mods: {file.FileName} (проект {reference.ProjectId}) — автор запретил загрузку сторонними лаунчерами.");
                continue;
            }

            if (file.DownloadUrl.Scheme != Uri.UriSchemeHttps || !file.DownloadUrl.Host.EndsWith(CdnSuffix, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"Файл {file.FileName} предлагается не с серверов CurseForge — пропущен.");
                continue;
            }
            if (file.Sha1 is null)
            {
                notes.Add($"У файла {file.FileName} нет контрольной суммы — пропущен.");
                continue;
            }

            // .jar — моды; .zip — обычно ресурспаки (шейдеры CurseForge тоже zip, их придётся перенести вручную).
            var folder = file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "resourcepacks" : "mods";
            var relative = $"{folder}/{file.FileName}";
            if (SafePath.Resolve(Path.GetTempPath(), relative) is null)
                throw new ModException($"В ответе CurseForge небезопасное имя файла «{file.FileName}». Импорт остановлен.");
            if (!paths.Add(relative)) continue;

            hosts.Add(file.DownloadUrl.Host);
            files.Add(new MrpackFile(relative, file.Sha1, [file.DownloadUrl], file.Length));
        }

        if (manual > 0)
            notes.Insert(0, $"Часть модов ({manual}) нужно скачать вручную — список в файле waundbox-manual-downloads.txt в папке профиля.");

        return new MrpackInfo(manifest.Name, null, manifest.Version ?? "", manifest.GameVersion, manifest.Loader, manifest.LoaderVersion,
            files, 0, [manifest.OverridesFolder], hosts.ToList(), notes);
    }
}
