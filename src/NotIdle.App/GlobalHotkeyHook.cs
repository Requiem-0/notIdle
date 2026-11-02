using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NotIdle.App;

/// <summary>
/// Registers a system-wide global hotkey (default: Ctrl + Alt + K) to toggle keep-awake.
/// Uses a lightweight NativeWindow to capture WM_HOTKEY without interfering with focus.
/// </summary>
internal sealed class GlobalHotkeyHook : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HotkeyIdToggle = 0x4E49;      // "NI" -> Ctrl + Alt + K
    private const int HotkeyIdDashboardD = 0x4E44;  // "ND" -> Ctrl + Alt + D
    private const int HotkeyIdDashboardS = 0x4E53;  // "NS" -> Ctrl + Alt + S

    private readonly Action _onToggle;
    private readonly Action _onDashboard;
    private bool _registered;

    public GlobalHotkeyHook(Action onToggle, Action onDashboard)
    {
        _onToggle = onToggle;
        _onDashboard = onDashboard;
        CreateHandle(new CreateParams());
    }

    public bool Register()
    {
        if (_registered) return true;
        uint mods = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
        // Ctrl + Alt + K (0x4B)
        bool k = RegisterHotKey(Handle, HotkeyIdToggle, mods, 0x4B);
        if (!k) RegisterHotKey(Handle, HotkeyIdToggle, MOD_CONTROL | MOD_ALT, 0x4B);

        // Ctrl + Alt + D (0x44)
        bool d = RegisterHotKey(Handle, HotkeyIdDashboardD, mods, 0x44);
        if (!d) RegisterHotKey(Handle, HotkeyIdDashboardD, MOD_CONTROL | MOD_ALT, 0x44);

        // Ctrl + Alt + S (0x53) - secondary shortcut for Settings
        bool s = RegisterHotKey(Handle, HotkeyIdDashboardS, mods, 0x53);
        if (!s) RegisterHotKey(Handle, HotkeyIdDashboardS, MOD_CONTROL | MOD_ALT, 0x53);

        _registered = true;
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(Handle, HotkeyIdToggle);
        UnregisterHotKey(Handle, HotkeyIdDashboardD);
        UnregisterHotKey(Handle, HotkeyIdDashboardS);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HotkeyIdToggle)
            {
                _onToggle();
                return;
            }
            if (id == HotkeyIdDashboardD || id == HotkeyIdDashboardS)
            {
                _onDashboard();
                return;
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
