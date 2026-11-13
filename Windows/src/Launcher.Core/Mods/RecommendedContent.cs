using Launcher.Core.Versions;

namespace Launcher.Core.Mods;

/// <param name="Slug">Короткое имя проекта на Modrinth. Если проекта с таким именем нет, запись молча пропускается.</param>
/// <param name="Loaders">Для каких загрузчиков рекомендуется; null — для любых (ресурспаки, шейдеры).</param>
public sealed record RecommendedItem(string Slug, ModContentType Type, LoaderKind[]? Loaders, string Reason);

/// <summary>Подборка «рекомендуем». Лежит в коде, чтобы работать без интернета; позже её можно подгружать с сайта автора.</summary>
public static class RecommendedContent
{
    private static readonly LoaderKind[] FabricOnly = [LoaderKind.Fabric];
    private static readonly LoaderKind[] FabricAndQuilt = [LoaderKind.Fabric, LoaderKind.Quilt];
    private static readonly LoaderKind[] ForgeOnly = [LoaderKind.Forge, LoaderKind.NeoForge];
    private static readonly LoaderKind[] Both = [LoaderKind.Fabric, LoaderKind.Quilt, LoaderKind.Forge, LoaderKind.NeoForge];

    public static IReadOnlyList<RecommendedItem> All { get; } =
    [
        new("fabric-api", ModContentType.Mod, FabricOnly, "Основа для большинства модов Fabric — без неё многие не запустятся."),
        new("sodium", ModContentType.Mod, FabricAndQuilt, "Сильно повышает FPS."),
        new("lithium", ModContentType.Mod, FabricAndQuilt, "Ускоряет работу игры и сервера, не меняя геймплей."),
        new("ferrite-core", ModContentType.Mod, Both, "Уменьшает потребление памяти."),
        new("entityculling", ModContentType.Mod, Both, "Не рисует то, что не видно, — меньше нагрузка на видеокарту."),
        new("immediatelyfast", ModContentType.Mod, Both, "Ускоряет отрисовку интерфейса и текста."),
        new("iris", ModContentType.Mod, FabricAndQuilt, "Поддержка шейдеров (ставится вместе с Sodium)."),
        new("modmenu", ModContentType.Mod, FabricAndQuilt, "Список модов и их настройки в меню игры."),
        new("xaeros-minimap", ModContentType.Mod, Both, "Мини-карта."),
        new("appleskin", ModContentType.Mod, Both, "Показывает насыщение и восстановление еды."),
        new("embeddium", ModContentType.Mod, ForgeOnly, "Повышает FPS (аналог Sodium для Forge)."),
        new("jei", ModContentType.Mod, ForgeOnly, "Рецепты и список предметов."),
        new("complementary-reimagined", ModContentType.Shader, null, "Красивые и нетребовательные шейдеры."),
        new("bsl-shaders", ModContentType.Shader, null, "Популярные шейдеры с мягким освещением."),
        new("faithful-32x", ModContentType.ResourcePack, null, "Знакомые текстуры в удвоенном разрешении.")
    ];

    public static IReadOnlyList<RecommendedItem> For(ModContentType type, LoaderKind loader) =>
        All.Where(i => i.Type == type && (i.Loaders is null || i.Loaders.Contains(loader))).ToList();
}
