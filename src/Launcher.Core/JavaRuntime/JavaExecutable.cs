namespace Launcher.Core.JavaRuntime;

public static class JavaExecutable
{
    /// <summary>"java.exe" в Windows, "java" в остальных системах.</summary>
    public static string FileName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    /// <summary>
    /// Принимает путь к файлу java или к папке установки Java (JAVA_HOME) и возвращает путь к исполняемому файлу.
    /// Существование файла не проверяется.
    /// </summary>
    public static string ResolvePath(string pathOrHome)
    {
        var path = pathOrHome.Trim().Trim('"');
        if (Directory.Exists(path))
        {
            var inBin = Path.Combine(path, "bin", FileName);
            if (File.Exists(inBin)) return inBin;
            var direct = Path.Combine(path, FileName);
            return File.Exists(direct) ? direct : inBin;
        }
        return path;
    }
}
