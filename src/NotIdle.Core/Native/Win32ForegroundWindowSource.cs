using System.Runtime.InteropServices;

namespace NotIdle.Core.Native;

/// <summary>
/// Reads the foreground window and decides whether the user is in a context that
/// should not be interrupted.
///
/// <para>Two independent signals:</para>
/// <list type="bullet">
/// <item><description>
/// <c>SHQueryUserNotificationState</c> — the shell's own answer to "is now a bad
/// time?". It reports presentation mode, exclusive-fullscreen Direct3D (games),
/// and full-screen apps, and is what Windows itself uses to suppress toasts.
/// </description></item>
/// <item><description>
/// Geometry — the foreground window's rect covering its monitor entirely. Catches
/// borderless-fullscreen apps that the shell reports as ordinary windows.
/// </description></item>
/// </list>
///
/// <para>The process name is resolved only when the focused window changes, so the
/// steady-state cost is a handful of trivial syscalls per second.</para>
/// </summary>
public sealed class Win32ForegroundWindowSource : IForegroundWindowSource
{
    // QUERY_USER_NOTIFICATION_STATE values that mean "don't interrupt".
    // QUNS_NOT_PRESENT (1) is deliberately excluded: it also covers the lock screen
    // and screensaver, which is precisely when keep-awake should still be working.
    private const int QUNS_BUSY = 2;                     // full-screen app or presentation settings
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;  // exclusive-mode game
    private const int QUNS_PRESENTATION_MODE = 4;        // presentation settings applied
    private const int QUNS_APP = 7;                      // Store app running full screen

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private readonly char[] _pathBuffer = new char[260];

    // Process-name cache, keyed by the window handle it was resolved for.
    private nint _cachedHandle;
    private string _cachedProcess = string.Empty;

    public ForegroundWindow Read()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0 || hwnd == GetShellWindow() || hwnd == GetDesktopWindow())
            return ForegroundWindow.Unknown;

        return new ForegroundWindow(
            hwnd,
            ResolveProcessName(hwnd),
            IsFullScreen(hwnd),
            IsPresenting());
    }

    private static bool IsPresenting()
    {
        // Fails outside an interactive session; absence of an answer is not evidence
        // of presenting, so fall through to "no".
        if (SHQueryUserNotificationState(out int state) != 0) return false;
        return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE or QUNS_APP;
    }

    private static bool IsFullScreen(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT window)) return false;

        nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == 0) return false;

        MONITORINFO info = default;
        info.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();
        if (!GetMonitorInfo(monitor, ref info)) return false;

        RECT screen = info.rcMonitor;

        // A 1 px tolerance: some apps size themselves a pixel proud of the monitor.
        return window.left <= screen.left + 1
            && window.top <= screen.top + 1
            && window.right >= screen.right - 1
            && window.bottom >= screen.bottom - 1;
    }

    private string ResolveProcessName(nint hwnd)
    {
        if (hwnd == _cachedHandle) return _cachedProcess;

        _cachedHandle = hwnd;
        _cachedProcess = QueryProcessName(hwnd);
        return _cachedProcess;
    }

    private string QueryProcessName(nint hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return string.Empty;

        // LIMITED_INFORMATION is the least privilege that still yields an image
        // path, and works against elevated processes without elevating ourselves.
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return string.Empty;

        try
        {
            uint size = (uint)_pathBuffer.Length;
            if (!QueryFullProcessImageName(process, 0, _pathBuffer, ref size) || size == 0)
                return string.Empty;

            return Path.GetFileNameWithoutExtension(new string(_pathBuffer, 0, (int)size));
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);
}
