using System.Text.RegularExpressions;
using Launcher.Core.Errors;
using Launcher.Core.GameLaunch;

namespace Launcher.Core.Profiles;

/// <summary>Игровой профиль: какая версия запускается и с какими настройками.</summary>
/// <param name="Name">Имя профиля (используется как имя папки игры, поэтому набор символов ограничен).</param>
/// <param name="MaxMemoryMb">Максимальная память (-Xmx) в мегабайтах.</param>
/// <param name="MinMemoryMb">Начальная память (-Xms) в мегабайтах или null.</param>
/// <param name="JvmArguments">Дополнительные аргументы JVM одной строкой (значения с пробелами — в кавычках).</param>
/// <param name="GameArguments">Дополнительные аргументы игры одной строкой.</param>
/// <param name="JavaPath">Путь к Java только для этого профиля или null.</param>
/// <param name="Icon">Имя файла значка из папки icons лаунчера или null — без значка.</param>
/// <param name="GarbageCollector">Идентификатор сборщика мусора (<see cref="GarbageCollectors"/>) или null — по умолчанию.</param>
public sealed record GameProfile(
    string Name,
    string VersionId,
    int MaxMemoryMb = GameProfile.DefaultMaxMemoryMb,
    int? MinMemoryMb = null,
    string? JvmArguments = null,
    string? GameArguments = null,
    string? JavaPath = null,
    string? GarbageCollector = null,
    string? Icon = null)
{
    public const int DefaultMaxMemoryMb = 2048;
    public const int MinAllowedMemoryMb = 512;
    /// <summary>Верхняя граница — лишь защита от опечатки (16 ТБ). Реальный предел задаёт компьютер, поэтому лаунчер только предупреждает.</summary>
    public const int MaxAllowedMemoryMb = 16_777_216;

    private static readonly Regex NameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9 _.\-]{0,39}$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <exception cref="ProfileException">Данные профиля некорректны.</exception>
    public void Validate()
    {
        if (!NameRegex.IsMatch(Name) || Name.EndsWith(' ') || Name.EndsWith('.') || ReservedNames.Contains(Name))
            throw new ProfileException(
                "Имя профиля: до 40 символов, латинские буквы, цифры, пробел, точка, дефис и подчёркивание; " +
                "должно начинаться с буквы или цифры и не заканчиваться пробелом или точкой.");

        if (string.IsNullOrWhiteSpace(VersionId))
            throw new ProfileException("В профиле не указана версия Minecraft.");

        if (MaxMemoryMb is < MinAllowedMemoryMb or > MaxAllowedMemoryMb)
            throw new ProfileException($"Максимальная память должна быть от {MinAllowedMemoryMb} до {MaxAllowedMemoryMb} МБ.");

        if (MinMemoryMb is { } min && (min < 128 || min > MaxMemoryMb))
            throw new ProfileException($"Начальная память должна быть от 128 МБ и не больше максимальной ({MaxMemoryMb} МБ).");

        if (GarbageCollector is not null && GarbageCollectors.Find(GarbageCollector) is null)
            throw new ProfileException($"Неизвестный сборщик мусора: «{GarbageCollector}».");

        try
        {
            CommandLineSplitter.Split(JvmArguments);
            CommandLineSplitter.Split(GameArguments);
        }
        catch (LauncherException ex)
        {
            throw new ProfileException(ex.Message, ex);
        }
    }
}
