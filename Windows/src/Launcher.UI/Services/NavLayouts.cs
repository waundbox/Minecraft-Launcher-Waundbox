namespace Launcher.UI.Services;

/// <summary>Расположение меню: сверху (как раньше) или слева (узкое, выезжает с названиями при наведении мыши).</summary>
internal static class NavLayouts
{
    public const string Top = "top";
    public const string Side = "side";

    /// <summary>Старое значение «sidewide» (слева с названиями) теперь тоже левое меню.</summary>
    public static string Normalize(string? value) => value is Side or "sidewide" ? Side : Top;

    /// <summary>В настройки пишется null для варианта «сверху».</summary>
    public static string? ToSetting(string? value) => Normalize(value) == Top ? null : Side;
}
