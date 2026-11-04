using Launcher.Core.Versions;

namespace Launcher.Core.Modpacks;

/// <summary>Один файл сборки: куда положить и откуда скачать.</summary>
/// <param name="Path">Путь относительно папки профиля (уже проверен на безопасность).</param>
/// <param name="Sha1">Контрольная сумма из сборки — файл без неё не принимается.</param>
public sealed record MrpackFile(string Path, string Sha1, IReadOnlyList<Uri> Downloads, long Size);

/// <summary>Содержимое сборки Modrinth (.mrpack), прочитанное из <c>modrinth.index.json</c>.</summary>
/// <param name="Files">Файлы, которые нужны клиенту (без помеченных как «клиенту не нужны»).</param>
/// <param name="OverridesFolders">Папки архива с файлами настроек; null — «overrides» и «client-overrides» (Modrinth).</param>
/// <param name="ExtraAllowedHosts">Дополнительные серверы, с которых разрешено скачивание (для сборок CurseForge).</param>
/// <param name="Notes">Замечания для пользователя (например, что скачать вручную).</param>
public sealed record MrpackInfo(
    string Name,
    string? Summary,
    string VersionId,
    string GameVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    IReadOnlyList<MrpackFile> Files,
    int SkippedServerOnlyFiles,
    IReadOnlyList<string>? OverridesFolders = null,
    IReadOnlyList<string>? ExtraAllowedHosts = null,
    IReadOnlyList<string>? Notes = null);

public sealed record MrpackProgress(string Text, double Fraction);

public sealed record MrpackImportResult(int FilesDownloaded, int OverrideFilesExtracted, IReadOnlyList<string> Warnings);
