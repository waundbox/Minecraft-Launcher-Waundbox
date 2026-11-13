using System.Text.Json;
using Launcher.Core.Errors;
using Launcher.Core.Libraries;

namespace Launcher.Core.Versions;

/// <summary>Разбор JSON конкретной версии (адрес берётся из манифеста версий).</summary>
public static class VersionDetailsParser
{
    /// <exception cref="VersionMetadataException">JSON повреждён или в нём нет обязательных данных.</exception>
    public static VersionDetails Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new VersionMetadataException("JSON версии: корневой элемент не является объектом.");

            var id = RequiredString(root, "id");
            var mainClass = RequiredString(root, "mainClass");

            var assetIndexEl = RequiredObject(root, "assetIndex");
            var assetIndex = new AssetIndexInfo(
                RequiredString(assetIndexEl, "id"),
                RequiredUri(assetIndexEl, "url"),
                OptionalString(assetIndexEl, "sha1"),
                OptionalLong(assetIndexEl, "size"));

            var downloadsEl = RequiredObject(root, "downloads");
            var client = ReadDownload(RequiredObject(downloadsEl, "client"), "downloads.client");
            if (client.Url is null)
                throw new VersionMetadataException("JSON версии: у клиента (downloads.client) нет адреса.");

            var javaMajor = ReadJavaMajor(root);
            var libraries = ReadLibraries(root);
            var logging = ReadLogging(root);

            return new VersionDetails(
                id, OptionalString(root, "type"), mainClass, OptionalString(root, "inheritsFrom"),
                assetIndex, client, javaMajor, libraries, json, logging);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new VersionMetadataException("JSON версии повреждён и не может быть разобран.", ex);
        }
    }

    /// <summary>
    /// Разбор версии, дополняющей базовую (Fabric/Forge): assetIndex и downloads не нужны.
    /// </summary>
    /// <exception cref="VersionMetadataException">JSON повреждён или нет обязательных полей.</exception>
    public static VersionOverlay ParseOverlay(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new VersionMetadataException("JSON версии: корневой элемент не является объектом.");

            return new VersionOverlay(
                RequiredString(root, "id"), OptionalString(root, "type"), RequiredString(root, "mainClass"),
                OptionalString(root, "inheritsFrom"), ReadJavaMajor(root), ReadLibraries(root), json, ReadLogging(root),
                root.TryGetProperty("waundboxCustomJar", out var ownJar) && ownJar.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new VersionMetadataException("JSON версии повреждён и не может быть разобран.", ex);
        }
    }

    private static int? ReadJavaMajor(JsonElement root) =>
        root.TryGetProperty("javaVersion", out var java) && java.ValueKind == JsonValueKind.Object &&
        java.TryGetProperty("majorVersion", out var major) && major.TryGetInt32(out var majorValue)
            ? majorValue : null;

    private static List<Library> ReadLibraries(JsonElement root)
    {
        var libraries = new List<Library>();
        if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
            foreach (var lib in libs.EnumerateArray())
                libraries.Add(ParseLibrary(lib));
        return libraries;
    }

    private static LoggingConfigInfo? ReadLogging(JsonElement root)
    {
        if (root.TryGetProperty("logging", out var loggingEl) && loggingEl.ValueKind == JsonValueKind.Object &&
            loggingEl.TryGetProperty("client", out var logClient) && logClient.ValueKind == JsonValueKind.Object &&
            logClient.TryGetProperty("file", out var logFile) && logFile.ValueKind == JsonValueKind.Object)
        {
            var argument = OptionalString(logClient, "argument");
            var fileId = OptionalString(logFile, "id");
            if (argument is not null && fileId is not null)
                return new LoggingConfigInfo(argument, fileId, RequiredUri(logFile, "url", "logging"),
                    OptionalString(logFile, "sha1"), OptionalLong(logFile, "size"));
        }
        return null;
    }

    private static Library ParseLibrary(JsonElement lib)
    {
        var name = RequiredString(lib, "name");

        if (!lib.TryGetProperty("downloads", out var downloads) || downloads.ValueKind != JsonValueKind.Object)
        {
            // Формат Fabric: только координаты Maven и адрес репозитория (sha1 и size бывают не у всех).
            var repository = OptionalString(lib, "url");
            if (repository is null)
                throw new VersionMetadataException(
                    $"Библиотека {name}: нет блока downloads и адреса репозитория. Такой формат не поддерживается.");
            var mavenArtifact = MavenArtifact(name, repository, OptionalString(lib, "sha1"), OptionalLong(lib, "size"));
            return new Library(name, mavenArtifact, new Dictionary<string, DownloadInfo>(), new Dictionary<string, string>(),
                [], ReadRules(lib, name));
        }

        DownloadInfo? artifact = null;
        if (downloads.TryGetProperty("artifact", out var art) && art.ValueKind == JsonValueKind.Object)
            artifact = ReadDownload(art, $"библиотека {name}");

        var classifiers = new Dictionary<string, DownloadInfo>(StringComparer.Ordinal);
        if (downloads.TryGetProperty("classifiers", out var cls) && cls.ValueKind == JsonValueKind.Object)
            foreach (var prop in cls.EnumerateObject())
                classifiers[prop.Name] = ReadDownload(prop.Value, $"библиотека {name} ({prop.Name})");

        var natives = new Dictionary<string, string>(StringComparer.Ordinal);
        if (lib.TryGetProperty("natives", out var nat) && nat.ValueKind == JsonValueKind.Object)
            foreach (var prop in nat.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    natives[prop.Name] = prop.Value.GetString()!;

        var exclude = new List<string>();
        if (lib.TryGetProperty("extract", out var ext) && ext.ValueKind == JsonValueKind.Object &&
            ext.TryGetProperty("exclude", out var exc) && exc.ValueKind == JsonValueKind.Array)
            foreach (var item in exc.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) exclude.Add(item.GetString()!);

        return new Library(name, artifact, classifiers, natives, exclude, ReadRules(lib, name));
    }

    private static List<LibraryRule> ReadRules(JsonElement lib, string name)
    {
        var rules = new List<LibraryRule>();
        if (lib.TryGetProperty("rules", out var rl) && rl.ValueKind == JsonValueKind.Array)
            foreach (var r in rl.EnumerateArray())
                rules.Add(ParseRule(r, name));
        return rules;
    }

    private static DownloadInfo MavenArtifact(string name, string repository, string? sha1, long? size)
    {
        var path = MavenCoordinates.ToRelativePath(name);
        if (!Uri.TryCreate(repository, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            throw new VersionMetadataException($"Библиотека {name}: некорректный адрес репозитория.");
        if (!baseUri.AbsoluteUri.EndsWith('/')) baseUri = new Uri(baseUri.AbsoluteUri + "/");
        return new DownloadInfo(path, new Uri(baseUri, path), sha1, size);
    }

    private static LibraryRule ParseRule(JsonElement rule, string libraryName)
    {
        var action = OptionalString(rule, "action");
        if (action is not ("allow" or "disallow"))
            throw new VersionMetadataException($"Библиотека {libraryName}: неизвестное действие правила \"{action}\".");

        string? osName = null, osArch = null, osVersion = null;
        if (rule.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.Object)
        {
            osName = OptionalString(os, "name");
            osArch = OptionalString(os, "arch");
            osVersion = OptionalString(os, "version");
        }

        return new LibraryRule(action == "allow", osName, osArch, osVersion, rule.TryGetProperty("features", out _));
    }

    private static DownloadInfo ReadDownload(JsonElement el, string what) =>
        new(OptionalString(el, "path"), OptionalUrl(el, what), OptionalString(el, "sha1"), OptionalLong(el, "size"));

    /// <summary>Пустой или отсутствующий адрес допустим (файл создаёт установщик); непустой обязан быть корректным http(s).</summary>
    private static Uri? OptionalUrl(JsonElement el, string what) =>
        string.IsNullOrWhiteSpace(OptionalString(el, "url")) ? null : RequiredUri(el, "url", what);

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object) return v;
        throw new VersionMetadataException($"JSON версии: отсутствует объект \"{name}\".");
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var v = OptionalString(parent, name);
        if (string.IsNullOrWhiteSpace(v))
            throw new VersionMetadataException($"JSON версии: отсутствует поле \"{name}\".");
        return v;
    }

    private static Uri RequiredUri(JsonElement parent, string name, string? what = null)
    {
        var text = OptionalString(parent, name);
        if (text is null || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new VersionMetadataException($"JSON версии: некорректный адрес в поле \"{name}\"{(what is null ? "" : $" ({what})")}.");
        return uri;
    }

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? OptionalLong(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
