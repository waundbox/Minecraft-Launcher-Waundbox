namespace Launcher.UI.Services;

/// <summary>Вкладки навигации и элементы главной страницы для редактора интерфейса.</summary>
internal static class TabCatalog
{
    public sealed record Tab(string Id, string Title, bool CanHide);

    public static readonly IReadOnlyList<Tab> All = new[]
    {
        new Tab("home", "Главная", false),
        new Tab("profiles", "Профили", true),
        new Tab("logs", "Логи", true),
        new Tab("account", "Аккаунт", true),
        new Tab("settings", "Настройки", false),   // скрыть нельзя: иначе негде вернуть
        new Tab("developer", "Разработчик", false) // скрыть нельзя: здесь сам редактор
    };

    public sealed record Part(string Id, string Title);

    /// <summary>Элементы главной страницы, которые правятся в редакторе интерфейса.</summary>
    public static readonly IReadOnlyList<Part> HomeElements = new[]
    {
        new Part("model", "3D-модель"),
        new Part("wordmark", "Название «WAUNDBOX»"),
        new Part("tagline", "Подпись «Лаунчер Minecraft»"),
        new Part("panel", "Панель запуска"),
        new Part("profiles", "Список профилей"),
        new Part("play", "Кнопка «Играть»"),
        new Part("account", "Строка с ником"),
        new Part("create", "Кнопка «Создать профиль»")
    };

    private static List<string> Split(string? saved) =>
        string.IsNullOrWhiteSpace(saved)
            ? new List<string>()
            : saved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Сохранённый порядок вкладок: неизвестные id пропускаются, недостающие дописываются в стандартном порядке.</summary>
    public static List<string> Order(string? saved)
    {
        var known = All.Select(t => t.Id).ToList();
        var result = Split(saved).Where(known.Contains).Distinct().ToList();
        result.AddRange(known.Where(id => !result.Contains(id)));
        return result;
    }

    public static HashSet<string> Hidden(string? saved) => Split(saved).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string? Join(IEnumerable<string> ids)
    {
        var text = string.Join(',', ids);
        return text.Length == 0 ? null : text;
    }
}
