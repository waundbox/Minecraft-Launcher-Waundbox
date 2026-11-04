using Launcher.UI.ViewModels;

namespace Launcher.UI.Services;

/// <summary>Общие объекты приложения, которые нужны страницам и диалогам.</summary>
internal sealed class UiContext
{
    public UiContext(AppServices services)
    {
        Services = services;
        Settings = new UiSettings(services.Settings);
        Settings.Changed += () => ThemeService.Apply(Settings.Current);
        Toasts = new ToastService();
        Dialogs = new DialogService();
        Catalog = new ProfileCatalog(services, Settings);
        Console = new ConsoleViewModel(this);
        Launcher = new LaunchController(this);
        Account = new AccountViewModel(this);
        Skins = new SkinsViewModel(this);
        HomeEditor = new HomeEditorViewModel(this);
    }

    public AppServices Services { get; }
    public UiSettings Settings { get; }
    public ToastService Toasts { get; }
    public DialogService Dialogs { get; }
    public ProfileCatalog Catalog { get; }
    public ConsoleViewModel Console { get; }
    public LaunchController Launcher { get; }
    public AccountViewModel Account { get; }
    public SkinsViewModel Skins { get; }
    public HomeEditorViewModel HomeEditor { get; }
}
