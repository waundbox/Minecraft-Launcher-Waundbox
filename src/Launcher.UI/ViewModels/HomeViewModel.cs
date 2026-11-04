using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Главная: выбор профиля и большая кнопка «Играть».</summary>
internal sealed class HomeViewModel : PageViewModel
{
    private readonly UiContext _ctx;

    public HomeViewModel(UiContext ctx) : base("Главная", "")
    {
        _ctx = ctx;
        CreateProfileCommand = new RelayCommand(() => _ctx.Dialogs.Show(new NewProfileViewModel(_ctx)));
    }

    /// <summary>Редактор интерфейса главной страницы (включается во вкладке разработчика).</summary>
    public HomeEditorViewModel Editor => _ctx.HomeEditor;

    public ProfileCatalog Catalog => _ctx.Catalog;
    public LaunchController Launcher => _ctx.Launcher;
    public AccountViewModel Account => _ctx.Account;

    public RelayCommand CreateProfileCommand { get; }
}
