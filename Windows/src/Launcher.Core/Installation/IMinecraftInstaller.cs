namespace Launcher.Core.Installation;

/// <summary>Параметры установки.</summary>
/// <param name="RefreshManifest">Принудительно обновить список версий перед установкой.</param>
/// <param name="MaxConcurrency">Сколько файлов качать одновременно.</param>
public sealed record InstallOptions(bool RefreshManifest = false, int MaxConcurrency = 8);

public enum InstallStage
{
    VersionList,
    VersionMetadata,
    AssetIndex,
    Files,
    Natives,
    Completed
}

/// <summary>Прогресс установки. Числа файлов/байт заполняются на стадии Files.</summary>
public sealed record InstallProgress(
    InstallStage Stage, int FilesDone = 0, int FilesTotal = 0, long BytesDone = 0, long BytesTotal = 0);

public sealed record InstallResult(
    string VersionId,
    string VersionJsonPath,
    string ClientJarPath,
    string NativesDirectory,
    int FilesTotal,
    int FilesDownloaded,
    int FilesAlreadyValid,
    long BytesDownloaded);

/// <summary>Устанавливает (или проверяет и чинит) выбранную версию Minecraft.</summary>
public interface IMinecraftInstaller
{
    Task<InstallResult> InstallAsync(
        string versionId, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Ставит версию по готовому JSON с диска (без скачивания клиентского jar).</summary>
public interface ILocalVersionInstaller
{
    Task<InstallResult> InstallLocalAsync(
        string versionId, string versionJson, InstallOptions options, IProgress<InstallProgress>? progress, CancellationToken cancellationToken);
}
