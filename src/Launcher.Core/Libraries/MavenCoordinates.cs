using Launcher.Core.Errors;

namespace Launcher.Core.Libraries;

/// <summary>Координаты Maven вида группа:имя:версия[:классификатор][@расширение].</summary>
public static class MavenCoordinates
{
    /// <summary>Относительный путь файла в репозитории: com/example/lib/1.0/lib-1.0.jar.</summary>
    /// <exception cref="VersionMetadataException">Координаты записаны неверно.</exception>
    public static string ToRelativePath(string coordinates)
    {
        var extension = "jar";
        var main = coordinates;
        var at = coordinates.IndexOf('@');
        if (at >= 0)
        {
            extension = coordinates[(at + 1)..];
            main = coordinates[..at];
        }

        var parts = main.Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(extension))
            throw new VersionMetadataException($"Некорректные координаты библиотеки \"{coordinates}\".");
        if (parts.Append(extension).Any(p => p.Contains('/') || p.Contains('\\') || p.Contains("..", StringComparison.Ordinal)))
            throw new VersionMetadataException($"Небезопасные координаты библиотеки \"{coordinates}\".");

        var (group, artifact, version) = (parts[0].Replace('.', '/'), parts[1], parts[2]);
        var classifier = parts.Length == 4 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{extension}";
    }

    /// <summary>Ключ «группа:имя[:классификатор]» без версии — по нему библиотеки потомка заменяют библиотеки базовой версии.</summary>
    public static string KeyWithoutVersion(string coordinates)
    {
        var main = coordinates.Split('@')[0];
        var parts = main.Split(':');
        if (parts.Length < 3) return coordinates;
        return parts.Length >= 4 ? $"{parts[0]}:{parts[1]}:{parts[3]}" : $"{parts[0]}:{parts[1]}";
    }
}
