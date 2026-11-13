using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Logging;
using Launcher.Core.Versions;

namespace Launcher.Core.Custom;

/// <summary>Результат проверки jar-файла.</summary>
/// <param name="DetectedVersion">Версия игры из version.json внутри jar (есть в современных клиентах) или null.</param>
/// <param name="LooksLikeClient">Внутри есть класс запуска клиента Minecraft.</param>
public sealed record JarInspection(int ClassCount, bool LooksLikeClient, string? DetectedVersion, bool HasMainClass);

/// <param name="JarPath">Путь к jar.</param>
/// <param name="Name">Имя версии (латиница, цифры, точка, дефис, подчёркивание).</param>
/// <param name="BaseVersion">Версия Minecraft, на которой основан jar (нужна без своего json; с json берётся из него, если не указана).</param>
/// <param name="VersionJsonPath">Готовый json версии рядом с jar или null.</param>
public sealed record CustomJarRequest(string JarPath, string Name, string? BaseVersion, string? VersionJsonPath);

public sealed record CustomJarResult(string VersionId, string GameVersion, bool UsedOwnJson);

/// <summary>Проверка jar и сборка идентификатора/описания своей версии. Всё, что не требует сети, вынесено сюда, чтобы тестироваться отдельно.</summary>
public static class CustomJarTools
{
    private static readonly Regex NameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,39}$", RegexOptions.CultureInvariant);
    private static readonly Regex GameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,29}$", RegexOptions.CultureInvariant);

    public const string OwnJarMarker = "waundboxCustomJar";
    public const string PlaceholderClientUrl = "https://waundbox.invalid/custom-client.jar";

    public static bool IsValidName(string? name) => name is not null && NameRegex.IsMatch(name);
    public static bool IsValidGameVersion(string? game) => game is not null && GameRegex.IsMatch(game);

    /// <summary>Идентификатор версии: «имя+mc1.21.1».</summary>
    public static string MakeVersionId(string name, string gameVersion)
    {
        if (!IsValidName(name))
            throw new CustomJarException("Имя версии: до 40 символов, латинские буквы, цифры, точка, дефис и подчёркивание; должно начинаться с буквы или цифры.");
        if (!IsValidGameVersion(gameVersion))
            throw new CustomJarException("Версия игры указана неверно: допустимы латинские буквы, цифры, точка, дефис и подчёркивание (например, 1.21.1).");
        return $"{name}+mc{gameVersion}";
    }

    /// <exception cref="CustomJarException">Файл не найден или это не jar/zip или в нём нет классов.</exception>
    public static JarInspection Inspect(string jarPath, string? mainClass = null)
    {
        if (!File.Exists(jarPath)) throw new CustomJarException($"Файл не найден: {jarPath}");
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var classes = 0;
            var looksLikeClient = false;
            var hasMain = mainClass is null;
            string? detected = null;
            var mainEntry = mainClass is null ? null : mainClass.Replace('.', '/') + ".class";

            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith(".class", StringComparison.Ordinal))
                {
                    classes++;
                    if (name is "net/minecraft/client/main/Main.class" or "net/minecraft/client/Minecraft.class"
                        or "net/minecraft/client/MinecraftClient.class" or "net/minecraft/client/main/GameConfig.class")
                        looksLikeClient = true;
                    if (mainEntry is not null && name == mainEntry) hasMain = true;
                }
                else if (name == "version.json" && entry.Length is > 0 and < 64 * 1024)
                {
                    using var reader = new StreamReader(entry.Open());
                    try
                    {
                        using var doc = JsonDocument.Parse(reader.ReadToEnd());
                        if (doc.RootElement.ValueKind == JsonValueKind.Object
                            && doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        {
                            var text = id.GetString();
                            if (IsValidGameVersion(text)) detected = text;
                        }
                    }
                    catch (JsonException) { /* version.json не по формату Mojang — просто не используем */ }
                }
            }

            if (classes == 0) throw new CustomJarException("В файле нет ни одного класса Java: это не клиент игры.");
            return new JarInspection(classes, looksLikeClient, detected, hasMain);
        }
        catch (InvalidDataException ex)
        {
            throw new CustomJarException("Файл повреждён или не является jar/zip-архивом.", ex);
        }
    }

    /// <summary>Описание своей версии без собственного json: наследуется от базовой, в classpath идёт ваш jar.</summary>
    public static string BuildOverlayJson(string versionId, string baseVersion, string mainClass)
    {
        var node = new JsonObject
        {
            ["id"] = versionId,
            ["inheritsFrom"] = baseVersion,
            ["type"] = "release",
            ["mainClass"] = mainClass,
            ["libraries"] = new JsonArray(),
            ["arguments"] = new JsonObject { ["jvm"] = new JsonArray(), ["game"] = new JsonArray() },
            [OwnJarMarker] = true
        };
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Приводит json пользователя к виду, который понимает лаунчер: ставит наш идентификатор, при наследовании
    /// отмечает «свой jar» и добавляет недостающие поля; у самостоятельной версии подменяет данные клиента (jar другой).
    /// </summary>
    /// <param name="baseMainClass">Главный класс базовой версии — на случай, если в json его нет.</param>
    public static string NormalizeUserJson(string json, string versionId, string? baseMainClass, out string? inheritsFrom, out string? assetsId)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new CustomJarException("Файл json версии повреждён: " + ex.Message, ex); }
        if (root is not JsonObject obj) throw new CustomJarException("Файл json версии должен быть JSON-объектом.");

        inheritsFrom = obj["inheritsFrom"]?.GetValue<string>();
        assetsId = obj["assetIndex"]?["id"]?.GetValue<string>() ?? obj["assets"]?.GetValue<string>();
        obj["id"] = versionId;

        if (obj["arguments"] is null && obj["minecraftArguments"] is null)
            obj["arguments"] = new JsonObject { ["jvm"] = new JsonArray(), ["game"] = new JsonArray() };

        if (!string.IsNullOrEmpty(inheritsFrom))
        {
            if (obj["mainClass"] is null)
            {
                if (baseMainClass is null) throw new CustomJarException("В json нет mainClass, а главный класс базовой версии неизвестен.");
                obj["mainClass"] = baseMainClass;
            }
            obj[OwnJarMarker] = true;
        }
        else
        {
            if (obj["mainClass"] is null) throw new CustomJarException("В json нет mainClass: непонятно, какой класс запускать.");
            if (obj["assetIndex"] is null) throw new CustomJarException("В json нет assetIndex: самостоятельной версии нужен индекс ресурсов. Если версия должна наследоваться от базовой, добавьте поле inheritsFrom.");
            if (obj["libraries"] is null) obj["libraries"] = new JsonArray();
            var downloads = obj["downloads"] as JsonObject ?? new JsonObject();
            obj["downloads"] = downloads;
            // jar свой: хеш и размер оригинала не подходят, адрес-заглушка нужен только для разбора
            downloads["client"] = new JsonObject { ["url"] = PlaceholderClientUrl };
        }

        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Читает mainClass у уже установленной версии (чтобы наследовать запуск).</summary>
    public static string? ReadMainClass(string versionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(versionJson);
            return doc.RootElement.TryGetProperty("mainClass", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Установка собственного jar как версии игры. Два случая: (1) только jar + базовая версия Minecraft;
/// (2) jar + готовый json версии (с наследованием от базовой или самостоятельный).
/// Версия записывается последней; при ошибке созданная папка версии удаляется.
/// </summary>
public sealed class CustomJarInstaller
{
    private readonly IMinecraftInstaller _vanilla;
    private readonly ILocalVersionInstaller _local;
    private readonly MinecraftLayout _layout;
    private readonly ILauncherLogger _logger;

    public CustomJarInstaller(IMinecraftInstaller vanilla, ILocalVersionInstaller local, MinecraftLayout layout, ILauncherLogger? logger = null)
    {
        _vanilla = vanilla;
        _local = local;
        _layout = layout;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    public async Task<CustomJarResult> InstallAsync(
        CustomJarRequest request, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (!CustomJarTools.IsValidName(request.Name))
            throw new CustomJarException("Имя версии: до 40 символов, латинские буквы, цифры, точка, дефис и подчёркивание; должно начинаться с буквы или цифры.");

        string? userJson = null;
        if (request.VersionJsonPath is { Length: > 0 } jsonPath)
        {
            if (!File.Exists(jsonPath)) throw new CustomJarException($"Файл json не найден: {jsonPath}");
            userJson = await File.ReadAllTextAsync(jsonPath, ct).ConfigureAwait(false);
        }

        var mainClassFromUser = userJson is null ? null : CustomJarTools.ReadMainClass(userJson);
        var inspection = CustomJarTools.Inspect(request.JarPath, mainClassFromUser);
        if (userJson is not null && !inspection.HasMainClass)
            _logger.Warn($"В jar нет главного класса {mainClassFromUser}: возможно, это не тот файл.");

        string versionId;
        string gameVersion;
        string? versionDirectoryToCleanup = null;

        try
        {
            if (userJson is null)
            {
                // Случай 1: jar + базовая версия.
                var baseVersion = request.BaseVersion ?? inspection.DetectedVersion
                    ?? throw new CustomJarException("Укажите версию Minecraft, на которой основан jar (например, 1.21.1): определить её по файлу не удалось.");
                if (!CustomJarTools.IsValidGameVersion(baseVersion))
                    throw new CustomJarException("Версия игры указана неверно (например, 1.21.1).");
                gameVersion = baseVersion;
                versionId = CustomJarTools.MakeVersionId(request.Name, gameVersion);
                EnsureNotInstalled(versionId);

                await EnsureBaseAsync(baseVersion, options, progress, ct).ConfigureAwait(false);
                var baseJson = await File.ReadAllTextAsync(_layout.VersionJsonPath(baseVersion), ct).ConfigureAwait(false);
                var mainClass = CustomJarTools.ReadMainClass(baseJson)
                    ?? throw new CustomJarException($"В описании версии {baseVersion} нет главного класса.");

                versionDirectoryToCleanup = _layout.VersionDirectory(versionId);
                Directory.CreateDirectory(versionDirectoryToCleanup);
                await CopyJarAsync(request.JarPath, versionId, ct).ConfigureAwait(false);
                await WriteJsonAsync(versionId, CustomJarTools.BuildOverlayJson(versionId, baseVersion, mainClass), ct).ConfigureAwait(false);
                return new CustomJarResult(versionId, gameVersion, false);
            }

            // Случай 2: jar + готовый json. Сначала узнаём, от чего он наследуется.
            var placeholderId = request.Name; // идентификатор пока неизвестен: нужна версия игры
            var probe = CustomJarTools.NormalizeUserJson(userJson, placeholderId, "placeholder", out var inherits, out var assets);
            _ = probe;

            if (!string.IsNullOrEmpty(inherits))
            {
                gameVersion = request.BaseVersion ?? VersionLabel.Parse(inherits).GameVersion;
                versionId = CustomJarTools.MakeVersionId(request.Name, gameVersion);
                EnsureNotInstalled(versionId);

                await EnsureBaseAsync(inherits, options, progress, ct).ConfigureAwait(false);
                var baseJson = await File.ReadAllTextAsync(_layout.VersionJsonPath(inherits), ct).ConfigureAwait(false);
                var json = CustomJarTools.NormalizeUserJson(userJson, versionId, CustomJarTools.ReadMainClass(baseJson), out _, out _);

                versionDirectoryToCleanup = _layout.VersionDirectory(versionId);
                Directory.CreateDirectory(versionDirectoryToCleanup);
                await CopyJarAsync(request.JarPath, versionId, ct).ConfigureAwait(false);
                await WriteJsonAsync(versionId, json, ct).ConfigureAwait(false);
                return new CustomJarResult(versionId, gameVersion, true);
            }
            else
            {
                gameVersion = request.BaseVersion ?? inspection.DetectedVersion ?? assets ?? "custom";
                if (!CustomJarTools.IsValidGameVersion(gameVersion)) gameVersion = "custom";
                versionId = CustomJarTools.MakeVersionId(request.Name, gameVersion);
                EnsureNotInstalled(versionId);

                var json = CustomJarTools.NormalizeUserJson(userJson, versionId, null, out _, out _);
                versionDirectoryToCleanup = _layout.VersionDirectory(versionId);
                Directory.CreateDirectory(versionDirectoryToCleanup);
                await CopyJarAsync(request.JarPath, versionId, ct).ConfigureAwait(false);
                await _local.InstallLocalAsync(versionId, json, options, progress, ct).ConfigureAwait(false);
                return new CustomJarResult(versionId, gameVersion, true);
            }
        }
        catch
        {
            // «полуустановленной» версии оставлять нельзя
            if (versionDirectoryToCleanup is not null)
            {
                try { Directory.Delete(versionDirectoryToCleanup, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* не удалось — ничего страшного */ }
            }
            throw;
        }
    }

    private void EnsureNotInstalled(string versionId)
    {
        if (File.Exists(_layout.VersionJsonPath(versionId)))
            throw new CustomJarException($"Версия «{versionId}» уже установлена. Выберите другое имя или удалите её папку в versions.");
    }

    private async Task EnsureBaseAsync(string baseVersion, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        // установщик проверяет файлы и докачивает только недостающее, поэтому вызываем его и для уже установленной версии
        try
        {
            await _vanilla.InstallAsync(baseVersion, options, progress, ct).ConfigureAwait(false);
        }
        catch (VersionNotFoundException) when (File.Exists(_layout.VersionJsonPath(baseVersion)))
        {
            // базовая версия не из списка Mojang, но уже стоит на диске — используем как есть
        }
    }

    private async Task CopyJarAsync(string source, string versionId, CancellationToken ct)
    {
        var destination = _layout.ClientJarPath(versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".part";
        await using (var input = File.OpenRead(source))
        await using (var output = File.Create(temp))
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        File.Move(temp, destination, overwrite: true);
    }

    private async Task WriteJsonAsync(string versionId, string json, CancellationToken ct)
    {
        var path = _layout.VersionJsonPath(versionId);
        var temp = path + ".part";
        await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        _logger.Info($"Своя версия {versionId} установлена.");
    }
}
