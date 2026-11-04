using System.Runtime.InteropServices;

namespace Launcher.Core.Libraries;

/// <summary>Операционная система и архитектура в терминах метаданных Mojang.</summary>
/// <param name="OsName">"windows", "linux" или "osx".</param>
/// <param name="Arch">"x86", "x86_64" или "arm64".</param>
/// <param name="OsVersion">Версия ОС (нужна только для редких правил с regex по версии).</param>
public sealed record PlatformInfo(string OsName, string Arch, string? OsVersion)
{
    /// <summary>"32" или "64" — подставляется вместо ${arch} в именах классификаторов.</summary>
    public string ArchBits => Arch == "x86" ? "32" : "64";

    public static PlatformInfo Current()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant()
        };
        return new PlatformInfo(os, arch, Environment.OSVersion.Version.ToString());
    }
}
