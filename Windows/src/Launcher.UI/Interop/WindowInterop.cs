using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Launcher.UI.Interop;

/// <summary>Мелкие возможности Windows 11: скруглённые углы окна и тёмный заголовок. На Windows 10 вызовы безвредно игнорируются.</summary>
internal static class WindowInterop
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var round = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref round, sizeof(int));
            var dark = 1;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            // старая система без этих возможностей — внешний вид просто останется обычным
        }
    }
}
