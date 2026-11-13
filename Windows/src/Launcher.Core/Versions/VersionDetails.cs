namespace Launcher.Core.Versions;

/// <summary>Файл для скачивания из JSON версии (client, artifact, classifier).</summary>
/// <param name="Path">Относительный путь (есть у библиотек), иначе null.</param>
/// <param name="Url">Адрес файла. null у файлов, которые создаёт установщик (например, основной JAR Forge): скачивать их нельзя.</param>
public sealed record DownloadInfo(string? Path, Uri? Url, string? Sha1, long? Size);

/// <summary>Индекс ресурсов (assets) версии.</summary>
public sealed record AssetIndexInfo(string Id, Uri Url, string? Sha1, long? Size);

/// <summary>Одно правило применимости библиотеки.</summary>
/// <param name="HasFeatures">Правило зависит от «features» (для библиотек не применяется — считаем несовпадающим).</param>
public sealed record LibraryRule(bool Allow, string? OsName, string? OsArch, string? OsVersion, bool HasFeatures);

/// <summary>Библиотека из JSON версии.</summary>
/// <param name="Classifiers">Дополнительные файлы по классификаторам (в основном старые natives).</param>
/// <param name="Natives">Соответствие «ОС → классификатор» (только старые версии).</param>
/// <param name="ExtractExclude">Что не распаковывать из natives-архива.</param>
public sealed record Library(
    string Name,
    DownloadInfo? Artifact,
    IReadOnlyDictionary<string, DownloadInfo> Classifiers,
    IReadOnlyDictionary<string, string> Natives,
    IReadOnlyList<string> ExtractExclude,
    IReadOnlyList<LibraryRule> Rules);

/// <summary>Конфигурация логирования клиента (log4j). Для версий 1.7–1.18.1 её подключение закрывает уязвимость Log4Shell.</summary>
/// <param name="Argument">Шаблон JVM-аргумента; "${path}" заменяется путём к файлу.</param>
public sealed record LoggingConfigInfo(string Argument, string FileId, Uri Url, string? Sha1, long? Size);

/// <summary>Разобранный JSON конкретной версии Minecraft (то, что нужно для установки).</summary>
/// <param name="RawJson">Исходный JSON — понадобится при запуске для разбора аргументов.</param>
public sealed record VersionDetails(
    string Id,
    string? Type,
    string MainClass,
    string? InheritsFrom,
    AssetIndexInfo AssetIndex,
    DownloadInfo ClientDownload,
    int? JavaMajorVersion,
    IReadOnlyList<Library> Libraries,
    string RawJson,
    LoggingConfigInfo? Logging = null,
    IReadOnlyList<string>? RawJsonChain = null,
    string? BaseVersionId = null)
{
    /// <summary>Идентификатор версии, чей client.jar попадает в classpath (для Fabric/Forge — базовая версия Minecraft).</summary>
    public string ClientJarId => BaseVersionId ?? Id;

    /// <summary>JSON всей цепочки наследования: от базовой версии к самой версии.</summary>
    public IReadOnlyList<string> AllRawJson => RawJsonChain ?? [RawJson];
}

/// <summary>
/// Версия, которая дополняет базовую (поле inheritsFrom): профили Fabric и Forge.
/// В ней нет assetIndex и downloads — их берут у базовой версии.
/// <c>UsesOwnJar</c> (поле waundboxCustomJar в JSON): в classpath идёт jar самой версии, а не базовой (своя версия из jar-файла).
/// </summary>
public sealed record VersionOverlay(
    string Id,
    string? Type,
    string MainClass,
    string? InheritsFrom,
    int? JavaMajorVersion,
    IReadOnlyList<Library> Libraries,
    string RawJson,
    LoggingConfigInfo? Logging,
    bool UsesOwnJar = false);
