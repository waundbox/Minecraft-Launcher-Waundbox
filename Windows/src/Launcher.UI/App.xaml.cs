using System.Windows;
using Launcher.Core.Configuration;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Launcher.UI.Services;
using Launcher.UI.ViewModels;

namespace Launcher.UI;

public partial class App : Application
{
    private AppServices? _services;
    private UiContext? _context;

    private const string InstanceMutexName = @"Local\Waundbox.SingleInstance";

    /// <summary>Частота кадров анимаций: всегда 120.</summary>
    internal static int AppliedFps => LauncherSettings.DefaultAnimationFps;

    private Mutex? _instanceMutex;

    static App()
    {
        // Частота задаётся один раз, до создания первой анимации: WPF не позволяет менять её на лету.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata(AppliedFps));
    }

    /// <summary>Лаунчер должен быть запущен один раз: два экземпляра портили бы общие файлы профилей и настроек.</summary>
    private bool AcquireSingleInstance(bool waitForPrevious)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var created);
        if (created) return true;

        if (waitForPrevious)
        {
            try
            {
                if (_instanceMutex.WaitOne(TimeSpan.FromSeconds(8))) return true;
            }
            catch (AbandonedMutexException)
            {
                return true; // предыдущий экземпляр завершился, не отпустив замок
            }
        }
        return false;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        if (!AcquireSingleInstance(waitForPrevious: e.Args.Contains("--restart")))
        {
            MessageBox.Show("Waundbox уже запущен. Найдите его значок на панели задач.", "Waundbox",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        try
        {
            _services = AppServices.Create();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить лаунчер:\n" + ex.Message, "Waundbox", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _context = new UiContext(_services);
        var viewModel = new MainViewModel(_context);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        _ = viewModel.InitializeAsync();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Последний рубеж: пишем в журнал и показываем сообщение, окно не закрываем.
        _services?.Logger.Error("Необработанное исключение интерфейса.", e.Exception);
        _context?.Toasts.Show("Неожиданная ошибка: " + e.Exception.Message, ToastKind.Error);
        // Тосты исчезают за несколько секунд, поэтому дублируем окном: так ошибку точно можно прочитать и переслать.
        MessageBox.Show(e.Exception.ToString(), "Waundbox: ошибка интерфейса", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { /* замок не принадлежал этому потоку */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
