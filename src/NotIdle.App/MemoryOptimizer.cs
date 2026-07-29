using System.Runtime.InteropServices;

namespace NotIdle.App;

/// <summary>
/// One-shot working-set trim after startup. The JIT/loader touch many pages
/// during init that are never needed again; releasing them back to the OS keeps
/// the idle footprint of this background utility small. Windows pages anything
/// still needed back in on demand — this is a hint, not a leak.
/// </summary>
internal static partial class MemoryOptimizer
{
    public static void TrimWorkingSet()
    {
        try
        {
            // -1 for both min and max tells Windows to trim the working set now.
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch
        {
            // Purely an optimization; never let it affect startup.
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(nint hProcess, nint dwMin, nint dwMax);
}
