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

    /// <summary>Элементы остальных страниц (кроме главной и разработчика). Id вида «страница/элемент», чтобы не пересекаться с главной.</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Part>> PageParts =
        new Dictionary<string, IReadOnlyList<Part>>
        {
            ["profiles"] = new Part[]
            {
                new("profiles/title", "Заголовок страницы"),
                new("profiles/actions", "Кнопки «Импорт» и «Новый профиль»"),
                new("profiles/list", "Карточки профилей")
            },
            ["logs"] = new Part[]
            {
                new("logs/title", "Заголовок страницы"),
                new("logs/actions", "Кнопки журнала"),
                new("logs/picker", "Выбор игры"),
                new("logs/output", "Окно вывода игры")
            },
            ["account"] = new Part[]
            {
                new("account/title", "Заголовок страницы"),
                new("account/tabs", "Переключатель «Аккаунт / Скины»"),
                new("account/main", "Вход в Microsoft"),
                new("account/local", "Локальный режим"),
                new("account/elyby", "Ely.by"),
                new("account/prefer", "Какой аккаунт использовать"),
                new("skins/skin", "Скин"),
                new("skins/collection", "Коллекция скинов")
            },
            ["settings"] = new Part[]
            {
                new("settings/title", "Заголовок страницы"),
                new("settings/tabs", "Переключатель вкладок"),
                new("settings/presets-back", "Кнопка «К оформлению»"),
                new("settings/theme-sky", "Тема «Небо»"),
                new("settings/theme-cozy", "Тема «Уютный дом»"),
                new("settings/theme-forest", "Тема «Вечерний лес»"),
                new("settings/theme-sakura", "Тема «Сакура»"),
                new("settings/theme-silk", "Тема «Шёлк»"),
                new("settings/theme-plain", "Тема «Обычный фон»"),
                new("settings/theme-folder", "Темы из папки themes"),
                new("settings/look", "Экономия ресурсов"),
                new("settings/accent", "Основной цвет"),
                new("settings/background", "Цвет фона"),
                new("settings/panelcolor", "Фон виджетов"),
                new("settings/theme", "Тема"),
                new("settings/glow", "Свечение и жидкое стекло"),
                new("settings/navlayout", "Расположение интерфейса"),
                new("settings/theme-title", "Тема: заголовок"),
                new("settings/theme-reset", "Тема: кнопка «Обычная тема»"),
                new("settings/theme-dark", "Тема: кнопка «Тёмная»"),
                new("settings/theme-light", "Тема: кнопка «Светлая»"),
                new("settings/theme-presets", "Тема: кнопка «Готовые темы»"),
                new("settings/nav-title", "Расположение: заголовок"),
                new("settings/nav-hint", "Расположение: пояснение"),
                new("settings/nav-top", "Расположение: кнопка «Сверху»"),
                new("settings/nav-side", "Расположение: кнопка «Слева»"),
                new("settings/nav-editor", "Расположение: кнопка редактора"),
                new("settings/glow-title", "Свечение: заголовок"),
                new("settings/glow-switch", "Свечение: переключатель"),
                new("settings/liquid-title", "Жидкое стекло: заголовок"),
                new("settings/liquid-switch", "Жидкое стекло: переключатель"),
                new("settings/accent-title", "Основной цвет: заголовок"),
                new("settings/accent-swatches", "Основной цвет: образцы"),
                new("settings/accent-wheel", "Основной цвет: круг"),
                new("settings/panel-title", "Фон виджетов: заголовок"),
                new("settings/panel-reset", "Фон виджетов: кнопка «По умолчанию»"),
                new("settings/panel-swatches", "Фон виджетов: образцы"),
                new("settings/panel-wheel", "Фон виджетов: круг"),
                new("settings/bg-title", "Фон: заголовок"),
                new("settings/bg-swatches", "Фон: образцы"),
                new("settings/bg-wheel", "Фон: круг"),
                new("settings/folders", "Папки"),
                new("settings/updates", "Обновления"),
                new("settings/version", "Плашка версии"),
                new("settings/java", "Java"),
                new("settings/model", "Модель персонажа"),
                new("settings/icons", "Значки профилей")
            }
        };

    /// <summary>Элементы, которые можно править на странице; у режима разработчика (и неизвестной страницы) список пуст.</summary>
    public static IReadOnlyList<Part> ElementsOf(string pageId) =>
        pageId == "home" ? HomeElements
        : PageParts.TryGetValue(pageId, out var parts) ? parts
        : Array.Empty<Part>();

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
