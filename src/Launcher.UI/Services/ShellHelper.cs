using System.ComponentModel;
using System.Diagnostics;

namespace Launcher.UI.Services;

internal static class ShellHelper
{
    /// <summary>Открывает папку, файл или страницу в программе по умолчанию. Возвращает false, если не получилось.</summary>
    public static bool Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    public static bool OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return Open(folder);
    }
}
