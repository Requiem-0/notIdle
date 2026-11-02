using System.Runtime.InteropServices;
using System.Threading;

namespace NotIdle.Core.Native;

/// <summary>
/// The synthetic-input surface of NotIdle, and the only place in the app that
/// injects anything.
///
/// <list type="bullet">
/// <item><description>
/// <see cref="Pulse"/> — one 1 px cursor movement, sent out and immediately back,
/// which resets the session input-idle clock without being perceptible or
/// dragging anything under the pointer.
/// </description></item>
/// <item><description>
/// <see cref="MoveTo"/> — an absolute pointer move for path playback. Sent through
/// <c>SendInput</c> rather than <c>SetCursorPos</c>: the latter relocates the
/// pointer but does <em>not</em> reset the idle clock, which would defeat the point.
/// </description></item>
/// <item><description>
/// <see cref="Click"/> — a button press and release at the current position, for
/// the opt-in auto-clicker.
/// </description></item>
/// </list>
///
/// <para>Keystrokes are still never synthesized.</para>
/// </summary>
public sealed class Win32SyntheticInput : ISyntheticInputSink
{
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const int WHEEL_DELTA = 120;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>
    /// Reused across calls so the hot path allocates nothing. Four entries is the
    /// largest batch we send (a double click). Guarded by <see cref="_gate"/>: the
    /// pulse, the path player and the click timer all live on different threads.
    /// </summary>
    private readonly INPUT[] _buffer = new INPUT[4];
    private readonly Lock _gate = new();

    public CursorPoint Pulse(bool restoreCursor)
    {
        CursorPoint before = ReadCursor();

        lock (_gate)
        {
            // Out and back in a single SendInput call, so the two moves are delivered
            // atomically and no other input can be interleaved between them.
            _buffer[0] = MoveBy(1, 0);
            _buffer[1] = MoveBy(-1, 0);
            _ = SendInput(2, _buffer, Marshal.SizeOf<INPUT>());
        }

        if (restoreCursor && before != default)
        {
            // Belt and braces: relative moves clamp at screen edges, so the round
            // trip is not guaranteed to land exactly where it started.
            CursorPoint after = ReadCursor();
            if (after != before) SetCursorPos(before.X, before.Y);
            return before;
        }

        return ReadCursor();
    }

    public CursorPoint MoveTo(int x, int y)
    {
        ScreenBounds screen = GetVirtualScreen();
        CursorPoint target = screen.Clamp(new CursorPoint(x, y));

        lock (_gate)
        {
            _buffer[0] = MoveAbsolute(target, screen);
            _ = SendInput(1, _buffer, Marshal.SizeOf<INPUT>());
        }

        return ReadCursor();
    }

    public void Click(MouseButton button, bool doubleClick)
    {
        (uint down, uint up) = button switch
        {
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };

        lock (_gate)
        {
            // One SendInput call for the whole gesture: a double click delivered as
            // four events in one batch cannot be split by another app's input, which
            // is what would otherwise degrade it into two singles.
            _buffer[0] = Flag(down);
            _buffer[1] = Flag(up);
            if (doubleClick)
            {
                _buffer[2] = Flag(down);
                _buffer[3] = Flag(up);
            }

            _ = SendInput(doubleClick ? 4u : 2u, _buffer, Marshal.SizeOf<INPUT>());
        }
    }

    public void Scroll(int notches)
    {
        if (notches == 0) return;

        lock (_gate)
        {
            _buffer[0] = Wheel(notches * WHEEL_DELTA);
            _ = SendInput(1, _buffer, Marshal.SizeOf<INPUT>());
        }
    }

    public ScreenBounds GetVirtualScreen()
    {
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0) return ScreenBounds.Fallback;

        return new ScreenBounds(
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            width,
            height);
    }

    private static INPUT MoveBy(int dx, int dy) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion
        {
            mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                dwFlags = MOUSEEVENTF_MOVE,
            }
        }
    };

    // Absolute mouse input is expressed on a normalized 0–65535 grid spanning the
    // whole virtual desktop, not in pixels — hence the conversion, and hence
    // MOUSEEVENTF_VIRTUALDESK so the grid covers every monitor rather than the
    // primary one.
    private static INPUT MoveAbsolute(CursorPoint target, ScreenBounds screen)
    {
        int width = Math.Max(1, screen.Width - 1);
        int height = Math.Max(1, screen.Height - 1);

        return new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((target.X - screen.Left) * 65535.0 / width),
                    dy = (int)Math.Round((target.Y - screen.Top) * 65535.0 / height),
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                }
            }
        };
    }

    private static INPUT Flag(uint flags) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion
        {
            mi = new MOUSEINPUT
            {
                dwFlags = flags,
            }
        }
    };

    private static INPUT Wheel(int delta) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion
        {
            mi = new MOUSEINPUT
            {
                mouseData = unchecked((uint)delta),
                dwFlags = MOUSEEVENTF_WHEEL,
            }
        }
    };

    private static CursorPoint ReadCursor() =>
        GetCursorPos(out POINT p) ? new CursorPoint(p.x, p.y) : default;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
