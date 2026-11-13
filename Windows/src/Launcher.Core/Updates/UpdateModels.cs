using Launcher.Core.Errors;

namespace Launcher.Core.Updates;

/// <summary>Не удалось проверить обновления или скачать установщик. Сообщение пригодно для показа пользователю.</summary>
public class UpdateException : LauncherException
{
    public UpdateException(string message) : base(message) { }
    public UpdateException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Один выпуск (релиз) на GitHub.</summary>
/// <param name="Version">Номер версии из тега релиза (например, v2.0.0 → 2.0.0).</param>
/// <param name="Tag">Тег релиза как есть.</param>
/// <param name="Title">Название релиза.</param>
/// <param name="Notes">Описание релиза («что нового») без служебных строк.</param>
/// <param name="PageUrl">Страница релиза в браузере.</param>
/// <param name="IsRequired">В описании есть строка «Обязательно: да».</param>
/// <param name="InstallerName">Имя файла установщика (.exe) среди файлов релиза или null.</param>
/// <param name="InstallerUrl">Адрес скачивания установщика или null.</param>
/// <param name="InstallerSize">Размер установщика в байтах, если известен.</param>
/// <param name="InstallerSha256">Контрольная сумма установщика (hex), если найдена в релизе.</param>
/// <param name="HashFileUrl">Адрес файла «имя.exe.sha256» среди файлов релиза: из него берётся сумма, если её нет в самом релизе.</param>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Title,
    string Notes,
    string PageUrl,
    bool IsRequired,
    string? InstallerName = null,
    string? InstallerUrl = null,
    long? InstallerSize = null,
    string? InstallerSha256 = null,
    string? HashFileUrl = null);

/// <summary>Итог проверки обновлений.</summary>
/// <param name="Current">Версия запущенного лаунчера.</param>
/// <param name="Latest">Самый новый выпуск (только если он новее текущей версии), иначе null.</param>
/// <param name="IsRequired">Среди выпусков новее текущей версии есть обязательный: пока лаунчер не обновлён, пользоваться им нельзя.</param>
public sealed record UpdateCheckResult(Version Current, ReleaseInfo? Latest, bool IsRequired)
{
    public bool IsUpdateAvailable => Latest is not null;
}

/// <summary>Источник сведений о выпусках лаунчера.</summary>
public interface IUpdateClient
{
    /// <summary>Выпуски лаунчера (без черновиков и предварительных), в любом порядке.</summary>
    Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken);

    /// <summary>Читает файл с контрольной суммой (первые 64 шестнадцатеричных символа) или возвращает null.</summary>
    Task<string?> ReadHashFileAsync(string url, CancellationToken cancellationToken);
}

/// <summary>Работа с номерами версий.</summary>
public static class AppVersion
{
    /// <summary>
    /// Разбирает «v2.0.0», «2.0», «1.1.0-beta»: ведущая «v» и суффикс после «-» или «+» отбрасываются,
    /// недостающие части дополняются нулями. <paramref name="preRelease"/> — был ли суффикс «-…».
    /// </summary>
    public static bool TryParse(string? text, out Version version, out bool preRelease)
    {
        version = new Version(0, 0, 0);
        preRelease = false;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        var cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0)
        {
            preRelease = s[cut] == '-';
            s = s[..cut];
        }

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var numbers = new int[3];
        for (var i = 0; i < Math.Min(parts.Length, 3); i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        if (parts.Length == 4 && !int.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            return false;

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>Приводит версию к виду «основной.дополнительный.исправление».</summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
