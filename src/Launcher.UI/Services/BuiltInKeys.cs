namespace Launcher.UI.Services;

/// <summary>
/// Ключ API CurseForge, встроенный в сборку по решению автора (репозиторий приватный).
/// Порядок: ключ из настроек → переменная среды WAUNDBOX_CURSEFORGE_KEY → этот ключ.
/// Внимание: ключ, зашитый в программу, можно достать из готового .exe; не публикуйте репозиторий и сборки, не заменив его.
/// </summary>
internal static class BuiltInKeys
{
    public const string CurseForge = "$2a$10$Grq9NPAm1t8rvcd0aEclneQZf.yLpEFobU/NmeXFevhamFsEWS7cK";
}
