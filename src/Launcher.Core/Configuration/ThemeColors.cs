namespace Launcher.Core.Configuration;

/// <summary>Проверка записи цвета из настроек. Лежит в ядре, чтобы проверяться тестами без интерфейса.</summary>
public static class ThemeColors
{
    /// <summary>Приводит «#RRGGBB», «RRGGBB» или «#rgb» к виду «#RRGGBB» (заглавные буквы); null, если запись некорректна.</summary>
    public static string? TryNormalizeHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length == 3) t = string.Concat(t.Select(ch => new string(ch, 2)));
        if (t.Length != 6) return null;
        foreach (var ch in t)
            if (!Uri.IsHexDigit(ch)) return null;
        return "#" + t.ToUpperInvariant();
    }
}
