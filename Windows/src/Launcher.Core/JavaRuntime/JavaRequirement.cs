using Launcher.Core.Versions;

namespace Launcher.Core.JavaRuntime;

public static class JavaRequirement
{
    /// <summary>Старые версии Minecraft не указывают javaVersion в метаданных — для них нужна Java 8.</summary>
    public const int LegacyDefaultMajor = 8;

    public static int For(VersionDetails details) => details.JavaMajorVersion ?? LegacyDefaultMajor;
}
