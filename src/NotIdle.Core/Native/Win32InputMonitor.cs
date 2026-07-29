using System.Runtime.InteropServices;

namespace NotIdle.Core.Native;

/// <summary>
/// Idle time via <c>GetLastInputInfo</c> — a single, system-wide, near-free
/// syscall. No hooks, no elevation, no per-event callbacks.
/// </summary>
public sealed class Win32InputMonitor : IInputMonitor
{
    public TimeSpan GetIdleTime()
    {
        LASTINPUTINFO info = default;
        info.cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>();
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero; // Treat a failed probe as "active" — never fire spuriously.

        // Both counters are 32-bit tick counts; unchecked subtraction handles wrap-around.
        uint elapsedMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsedMs);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
