namespace Launcher.Core.JavaRuntime;

/// <summary>Ищет установленные на компьютере Java.</summary>
public interface IJavaDetector
{
    /// <returns>Найденные и проверенные Java в порядке приоритета (JAVA_HOME, PATH, установки, среды Mojang).</returns>
    Task<IReadOnlyList<JavaInstallation>> DetectAsync(CancellationToken cancellationToken);
}
