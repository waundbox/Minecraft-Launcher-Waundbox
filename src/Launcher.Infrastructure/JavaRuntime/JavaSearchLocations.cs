namespace Launcher.Infrastructure.JavaRuntime;

/// <summary>Где искать Java. Вынесено в отдельный объект, чтобы поиск можно было проверять в тестах на временных папках.</summary>
/// <param name="JavaHome">Значение JAVA_HOME или null.</param>
/// <param name="PathEntries">Папки из переменной PATH.</param>
/// <param name="InstallRoots">Папки, внутри каждой из которых лежат отдельные установки JDK/JRE.</param>
/// <param name="MojangRuntimeRoots">Папки со средами официального лаунчера (внутри ищем bin/java на глубину до 4 уровней).</param>
public sealed record JavaSearchLocations(
    string? JavaHome,
    IReadOnlyList<string> PathEntries,
    IReadOnlyList<string> InstallRoots,
    IReadOnlyList<string> MojangRuntimeRoots)
{
    public static JavaSearchLocations ForCurrentSystem()
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"'))
            .ToList();

        var installRoots = new List<string>();
        var mojangRoots = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            foreach (var programFiles in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     }.Where(p => !string.IsNullOrEmpty(p)).Distinct())
            {
                foreach (var vendor in new[]
                         {
                             "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu", "BellSoft",
                             "Amazon Corretto", "Semeru", "AdoptOpenJDK", "OpenJDK"
                         })
                    installRoots.Add(Path.Combine(programFiles, vendor));
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(appData)) mojangRoots.Add(Path.Combine(appData, ".minecraft", "runtime"));
            if (!string.IsNullOrEmpty(localAppData))
                mojangRoots.Add(Path.Combine(localAppData, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"));
        }
        else
        {
            installRoots.AddRange(["/usr/lib/jvm", "/usr/java", "/Library/Java/JavaVirtualMachines"]);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                installRoots.Add(Path.Combine(home, ".sdkman", "candidates", "java"));
                mojangRoots.Add(Path.Combine(home, ".minecraft", "runtime"));
            }
        }

        return new JavaSearchLocations(Environment.GetEnvironmentVariable("JAVA_HOME"), pathEntries, installRoots, mojangRoots);
    }
}
