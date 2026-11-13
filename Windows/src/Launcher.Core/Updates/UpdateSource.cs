namespace Launcher.Core.Updates;

/// <summary>Откуда лаунчер берёт обновления: только официальный репозиторий проекта на GitHub.</summary>
public static class UpdateSource
{
    public const string Owner = "waundbox";
    public const string Repository = "Minecraft-Launcher-Waundbox";

    public static string ReleasesPage => $"https://github.com/{Owner}/{Repository}/releases";

    /// <summary>
    /// true, только если адрес — файл выпуска именно этого репозитория на github.com по https.
    /// Всё остальное (чужой сайт, чужой репозиторий, http) скачивать нельзя.
    /// </summary>
    public static bool IsTrustedAssetUrl(string? url, string owner = Owner, string repository = Repository)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var prefix = $"/{owner}/{repository}/releases/download/";
        return uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !uri.AbsolutePath.Contains("..", StringComparison.Ordinal);
    }
}
