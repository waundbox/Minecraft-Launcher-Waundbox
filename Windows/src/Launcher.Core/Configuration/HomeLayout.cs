using System.Text.Json;

namespace Launcher.Core.Configuration;

/// <summary>
/// Настройки одного элемента главной страницы в редакторе интерфейса (положение, размер, прозрачность).
/// Функция элемента (что делает кнопка) не настраивается — только внешний вид.
/// </summary>
/// <param name="X">Сдвиг по горизонтали, пикселей.</param>
/// <param name="Y">Сдвиг по вертикали, пикселей.</param>
/// <param name="Scale">Масштаб (1 — как есть).</param>
/// <param name="Opacity">Прозрачность от 0 до 1.</param>
/// <param name="Visible">Показывать элемент.</param>
/// <param name="Corner">Скругление углов, пикселей; null — как задано по умолчанию.</param>
/// <param name="Z">Слой: у кого число больше, тот рисуется поверх (−50…50).</param>
public sealed record ElementLayout(
    double X = 0, double Y = 0, double Scale = 1, double Opacity = 1, bool Visible = true, double? Corner = null, int Z = 0,
    double StretchX = 1, double StretchY = 1)
{
    public const double MaxOffset = 1500;
    public const double MinScale = 0.3;
    public const double MaxScale = 3;
    public const double MaxCorner = 80;
    public const int MaxLayer = 50;

    /// <summary>Значения, приведённые к допустимым пределам (защита от испорченного файла настроек).</summary>
    public ElementLayout Clamped() => this with
    {
        X = Fix(X, -MaxOffset, MaxOffset, 0),
        Y = Fix(Y, -MaxOffset, MaxOffset, 0),
        Scale = Fix(Scale, MinScale, MaxScale, 1),
        Opacity = Fix(Opacity, 0, 1, 1),
        Corner = Corner is { } c ? Fix(c, 0, MaxCorner, 0) : null,
        Z = Math.Clamp(Z, -MaxLayer, MaxLayer),
        StretchX = Fix(StretchX, MinScale, MaxScale, 1),
        StretchY = Fix(StretchY, MinScale, MaxScale, 1)
    };

    private static double Fix(double value, double min, double max, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Clamp(value, min, max);
}

/// <summary>Чтение и запись раскладки главной страницы (хранится в настройках одной JSON-строкой).</summary>
public static class HomeLayout
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Разбирает строку из настроек; испорченная строка даёт пустую раскладку.</summary>
    public static Dictionary<string, ElementLayout> Parse(string? json)
    {
        var result = new Dictionary<string, ElementLayout>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, ElementLayout>>(json, Options);
            if (raw is null) return result;
            foreach (var (id, layout) in raw)
                if (layout is not null && !string.IsNullOrWhiteSpace(id)) result[id] = layout.Clamped();
        }
        catch (JsonException)
        {
            // испорченная раскладка — стандартный вид
        }
        return result;
    }

    /// <summary>Строка для настроек: элементы с обычными значениями не записываются; пустая раскладка — null.</summary>
    public static string? Serialize(IReadOnlyDictionary<string, ElementLayout> layout)
    {
        var changed = layout
            .Where(p => p.Value.Clamped() != new ElementLayout())
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Key, p => p.Value.Clamped());
        return changed.Count == 0 ? null : JsonSerializer.Serialize(changed, Options);
    }
}
