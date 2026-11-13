namespace Launcher.Core.Configuration;

/// <summary>Пользовательские настройки лаунчера (хранятся вне исходного кода, в файле).</summary>
/// <param name="JavaPath">Путь к java, заданный пользователем; null — искать автоматически.</param>
/// <param name="MicrosoftClientId">Идентификатор приложения Azure (client id), зарегистрированного пользователем для входа в Microsoft; не секрет.</param>
/// <param name="EcoMode">Экономный режим интерфейса: без фоновых анимаций и сложных эффектов.</param>
/// <param name="DevUsername">Ник для development-режима (когда вход в Microsoft не выполнен).</param>
/// <param name="LastProfile">Последний выбранный профиль.</param>
/// <param name="AnimationFps">Устарело: частота анимаций теперь всегда 120 кадров/с, значение игнорируется (поле оставлено для совместимости со старыми файлами настроек).</param>
/// <param name="Theme">"dark" или "light"; null — тёмная.</param>
/// <param name="AccentColor">Основной цвет интерфейса в виде #RRGGBB; null — стандартный для темы.</param>
/// <param name="BackgroundColor">Цвет фона в виде #RRGGBB; null — стандартный для темы.</param>
/// <param name="PreferredAccount">Какой аккаунт использовать для запуска: "microsoft", "elyby" или "dev"; null — первый доступный (Microsoft, затем Ely.by, затем режим разработки).</param>
/// <param name="CurseForgeApiKey">Бесплатный ключ API CurseForge (console.curseforge.com) для импорта сборок CurseForge; хранится в settings.json открытым текстом.</param>
/// <param name="SkinSlim">Модель скина: false — Steve (руки 4 пикселя), true — Alex (тонкие руки, 3 пикселя). Используется для показа и при смене скина Microsoft.</param>
/// <param name="ModelPose">Поза стандартной модели на главной (id из <c>PlayerPoses</c>); null — «Стоит».</param>
/// <param name="ModelOverlay">Показывать второй слой скина (шапка, куртка, рукава, штаны) на стандартной модели.</param>
/// <param name="ModelUseSkin">Накладывать выбранный скин на свою модель Blockbench вместо текстуры из файла (модели из шаблона лаунчера делают так всегда).</param>
/// <param name="ShowModel3D">Показывать 3D-персонажа на главной; если выключено, панель запуска встаёт по центру.</param>
/// <param name="ModelSpin">Вращать 3D-персонажа на главной автоматически.</param>
/// <param name="DeveloperMode">Открыта вкладка разработчика (10 нажатий на плашку версии в настройках).</param>
/// <param name="IconSize">Разрешение значков профилей в пикселях (по умолчанию 32; меняется во вкладке разработчика).</param>
/// <param name="ShowFps">Счётчик кадров в углу окна (вкладка разработчика).</param>
/// <param name="TabOrder">Порядок вкладок навигации: id через запятую; null — стандартный.</param>
/// <param name="HiddenTabs">Скрытые вкладки: id через запятую.</param>
/// <param name="HiddenHomeParts">Скрытые части главной: id через запятую.</param>
/// <param name="HomeLayout">Раскладка главной страницы из редактора интерфейса (JSON, см. <see cref="Launcher.Core.Configuration.HomeLayout"/>); null — стандартная.</param>
/// <param name="BackgroundGlow">Показывать фоновое свечение («дым»).</param>
/// <param name="MaxGraphics">Максимальная графика (режим разработчика): 3D-модель всегда рисуется в полном размере окна.</param>
/// <param name="IdleEco">Через 10 секунд без действий в окне лаунчера переходить в экономный режим.</param>
/// <param name="AutoCheckUpdates">Проверять наличие новой версии лаунчера при запуске и раз в несколько часов.</param>
/// <param name="RequiredUpdateVersion">Версия, до которой лаунчер обязан обновиться (запоминается, чтобы окно обновления оставалось и без интернета); null — обязательного обновления нет.</param>
/// <param name="LiquidGlass">Эффект «жидкое стекло»: панели и окна с переливающимся бликом и светлой кромкой (только оформление, без размытия фона, ресурсы не тратятся).</param>
/// <param name="PanelColor">Цвет фона виджетов (панелей и окон) в виде #RRGGBB; null — стандартный (прозрачное «стекло» под цвет темы).</param>
/// <param name="HomeLayoutNoModel">Раскладка интерфейса для режима без 3D-модели (второе «пространство» редактора; первое — <c>HomeLayout</c>, когда модель показывается); null — стандартная.</param>
/// <param name="ProfileOrder">Порядок профилей в списках (имена через запятую); профили вне списка идут после, по алфавиту. null — по алфавиту.</param>
/// <param name="BackgroundImage">Имя файла своего фонового изображения в папке <c>backgrounds</c> лаунчера (копия выбранной картинки); null — без изображения.</param>
/// <param name="BackgroundImageAuto">Автоподбор цветов интерфейса под яркость изображения: на светлой картинке интерфейс становится светлым (тёмный текст), на тёмной — тёмным; сама картинка не меняется. Выключено — тема по выбору, а картинку можно затемнить вручную.</param>
/// <param name="BackgroundImageLuminance">Яркость изображения 0–1 (считается при выборе картинки), по ней работает автоподбор.</param>
/// <param name="BackgroundImageDim">Затемнение своего фонового изображения, 0–0,9, чтобы текст оставался читаемым.</param>
/// <param name="TextColor">Свой цвет текста интерфейса в виде #RRGGBB; null — подбирается автоматически под тему.</param>
/// <param name="ThemeKey">Какая готовая или своя тема сейчас стоит (id пресета, «folder:имя»); null — тема настроена вручную. По нему раскладка из редактора хранится отдельно для каждой темы.</param>
/// <param name="ThemeLayouts">Раскладки по темам (JSON: ключ темы → раскладка с моделью, без модели и расположение меню); см. ThemeLayoutStore.</param>
/// <param name="NavLayout">Расположение меню: null — сверху, "side" — слева значками, "sidewide" — слева с названиями.</param>
/// <param name="CustomModelPath">Путь к .bbmodel из Blockbench, который показывается на главной вместо стандартной модели; null — стандартная.</param>
public sealed record LauncherSettings(
    string? JavaPath = null,
    string? MicrosoftClientId = null,
    bool EcoMode = false,
    string? DevUsername = null,
    string? LastProfile = null,
    int AnimationFps = LauncherSettings.DefaultAnimationFps,
    string? PreferredAccount = null,
    string? Theme = null,
    string? AccentColor = null,
    string? BackgroundColor = null,
    string? CurseForgeApiKey = null,
    bool SkinSlim = false,
    string? CustomModelPath = null,
    string? ModelPose = null,
    bool ModelOverlay = true,
    bool ModelUseSkin = false,
    bool ShowModel3D = true,
    bool ModelSpin = true,
    bool DeveloperMode = false,
    int IconSize = LauncherSettings.DefaultIconSize,
    bool ShowFps = false,
    string? TabOrder = null,
    string? HiddenTabs = null,
    string? HiddenHomeParts = null,
    string? HomeLayout = null,
    bool BackgroundGlow = true,
    bool MaxGraphics = false,
    bool IdleEco = true,
    bool AutoCheckUpdates = true,
    string? RequiredUpdateVersion = null,
    bool LiquidGlass = false,
    string? PanelColor = null,
    string? HomeLayoutNoModel = null,
    string? ProfileOrder = null,
    string? BackgroundImage = null,
    double BackgroundImageDim = LauncherSettings.DefaultImageDim,
    bool BackgroundImageAuto = true,
    double BackgroundImageLuminance = 0.3,
    string? TextColor = null,
    string? NavLayout = null,
    string? ThemeKey = null,
    string? ThemeLayouts = null)
{
    public const int DefaultAnimationFps = 120;
    public const int DefaultIconSize = 32;
    public const double DefaultImageDim = 0.35;
}

/// <summary>Хранилище настроек.</summary>
public interface ISettingsStore
{
    /// <summary>Загружает настройки; если файла нет или он повреждён — возвращает настройки по умолчанию.</summary>
    Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken);
}
