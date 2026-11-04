using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Launcher.UI.Services;

/// <summary>Сжимает память процесса: полная сборка мусора с уплотнением и сброс рабочего набора (Windows вернёт страницы по мере надобности).</summary>
internal static class MemoryTrimmer
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            using var self = Process.GetCurrentProcess();
            EmptyWorkingSet(self.Handle);
        }
        catch (Exception)
        {
            // сжатие памяти — необязательная оптимизация
        }
    }
}
