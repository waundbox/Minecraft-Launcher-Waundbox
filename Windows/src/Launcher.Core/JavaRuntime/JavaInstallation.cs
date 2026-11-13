namespace Launcher.Core.JavaRuntime;

/// <summary>Откуда взята найденная Java.</summary>
public enum JavaSource
{
    /// <summary>Путь, который пользователь указал сам (команда "java set").</summary>
    Configured,
    JavaHome,
    Path,
    /// <summary>Обычная установка JDK/JRE (Program Files, /usr/lib/jvm и т.п.).</summary>
    Installed,
    /// <summary>Среда, скачанная официальным лаунчером Minecraft.</summary>
    MojangRuntime
}

/// <summary>Найденная и проверенная запуском Java.</summary>
/// <param name="Path">Полный путь к java (java.exe).</param>
/// <param name="MajorVersion">Основная версия: 8, 17, 21 и т.д. (для "1.8.0_392" — 8).</param>
/// <param name="Version">Полная строка версии из java.version.</param>
public sealed record JavaInstallation(
    string Path, int MajorVersion, string Version, string? Vendor, bool Is64Bit, JavaSource Source);

/// <summary>Результат подбора Java под требования версии Minecraft.</summary>
/// <param name="Warning">Пояснение, если подобрана не рекомендованная (более новая) Java; иначе null.</param>
public sealed record JavaResolution(JavaInstallation Installation, string? Warning);
