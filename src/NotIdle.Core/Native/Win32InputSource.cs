using System.Runtime.InteropServices;

namespace NotIdle.Core.Native;

/// <summary>
/// System-wide input telemetry via <c>GetLastInputInfo</c> and <c>GetCursorPos</c>
/// — two near-free syscalls, no hooks, no elevation, no per-event callbacks.
///
/// <c>GetLastInputInfo</c> is session-wide and covers keyboard, mouse movement and
/// mouse buttons alike. It does not distinguish real input from injected input;
/// that attribution is <see cref="UserActivityMonitor"/>'s job.
/// </summary>
public sealed class Win32InputSource : ISystemInputSource
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

    public CursorPoint GetCursorPosition()
    {
        // Fails on the secure desktop (UAC/lock screen); report the origin and let
        // the monitor keep its previous baseline.
        return GetCursorPos(out POINT p) ? new CursorPoint(p.x, p.y) : default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
