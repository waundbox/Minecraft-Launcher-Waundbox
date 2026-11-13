using System.IO.Compression;
using System.Text.Json;
using Launcher.Core.Errors;
using Launcher.Core.Versions;

namespace Launcher.Core.Modpacks;

/// <summary>Чтение формата .mrpack (https://docs.modrinth.com/modpacks/format/, formatVersion 1).</summary>
public static class MrpackReader
{
    public const string IndexName = "modrinth.index.json";
    private const long MaxIndexBytes = 20 * 1024 * 1024;
    private const int MaxFiles = 5000;

    /// <exception cref="ModException">Файл не похож на сборку Modrinth, повреждён или рассчитан на неподдерживаемый загрузчик.</exception>
    public static MrpackInfo Read(string mrpackPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(mrpackPath);
            var entry = zip.GetEntry(IndexName)
                ?? throw new ModException("В архиве нет файла modrinth.index.json — это не сборка Modrinth (.mrpack).");
            if (entry.Length > MaxIndexBytes)
                throw new ModException("Описание сборки слишком большое. Файл отклонён.");

            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            return Parse(document.RootElement);
        }
        catch (InvalidDataException ex)
        {
            throw new ModException("Файл повреждён или не является архивом .mrpack.", ex);
        }
        catch (JsonException ex)
        {
            throw new ModException("Описание сборки (modrinth.index.json) повреждено.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModException("Не удалось прочитать файл сборки.", ex);
        }
    }

    public static MrpackInfo Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new ModException("Описание сборки имеет неверный формат.");

        if (!root.TryGetProperty("formatVersion", out var fv) || !fv.TryGetInt32(out var format) || format != 1)
            throw new ModException("Версия формата сборки не поддерживается (нужна formatVersion 1).");
        if (Str(root, "game") != "minecraft")
            throw new ModException("Сборка предназначена не для Minecraft.");

        var name = Str(root, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new ModException("В сборке не указано название.");

        if (!root.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
            throw new ModException("В сборке не указаны зависимости (версия Minecraft и загрузчик).");

        var game = Str(deps, "minecraft") ?? throw new ModException("В сборке не указана версия Minecraft.");

        LoaderKind loader = LoaderKind.Vanilla;
        string? loaderVersion = null;
        if (Str(deps, "fabric-loader") is { } fabric) { loader = LoaderKind.Fabric; loaderVersion = fabric; }
        else if (Str(deps, "forge") is { } forge) { loader = LoaderKind.Forge; loaderVersion = forge; }
        else if (Str(deps, "neoforge") is { } neoforge) { loader = LoaderKind.NeoForge; loaderVersion = neoforge; }
        else if (Str(deps, "quilt-loader") is { } quilt) { loader = LoaderKind.Quilt; loaderVersion = quilt; }

        var files = new List<MrpackFile>();
        var skipped = 0;
        if (root.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (files.Count >= MaxFiles) throw new ModException("В сборке слишком много файлов. Файл отклонён.");
                if (item.ValueKind != JsonValueKind.Object) continue;

                // Файлы, которые клиенту не нужны (только для сервера), не скачиваются.
                if (item.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object && Str(env, "client") == "unsupported")
                {
                    skipped++;
                    continue;
                }

                var path = Str(item, "path");
                if (path is null) continue;
                if (SafePath.Resolve(Path.GetTempPath(), path) is null)
                    throw new ModException($"В сборке указан небезопасный путь «{path}». Файл отклонён.");

                string? sha1 = null;
                if (item.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object) sha1 = Str(hashes, "sha1");
                if (string.IsNullOrWhiteSpace(sha1))
                    throw new ModException($"У файла «{path}» в сборке нет контрольной суммы. Файл отклонён.");

                var urls = new List<Uri>();
                if (item.TryGetProperty("downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Array)
                    foreach (var d in downloads.EnumerateArray())
                        if (d.ValueKind == JsonValueKind.String && Uri.TryCreate(d.GetString(), UriKind.Absolute, out var uri)) urls.Add(uri);
                if (urls.Count == 0) throw new ModException($"Для файла «{path}» в сборке нет ссылок для скачивания.");

                var size = item.TryGetProperty("fileSize", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                files.Add(new MrpackFile(path.Replace('\\', '/'), sha1, urls, size));
            }
        }

        return new MrpackInfo(name.Trim(), Str(root, "summary"), Str(root, "versionId") ?? "", game, loader, loaderVersion, files, skipped);
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
