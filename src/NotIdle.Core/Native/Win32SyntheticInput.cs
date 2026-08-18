using System.Runtime.InteropServices;

namespace NotIdle.Core.Native;

/// <summary>
/// The entire synthetic-input surface of NotIdle: one 1 px cursor movement, sent
/// out and immediately back, which resets the session input-idle clock without
/// being perceptible or dragging anything under the pointer.
///
/// <para>No clicks, no keystrokes, no scrolling, no window or control interaction
/// — those are neither needed to keep a session awake nor something this app
/// should ever synthesize.</para>
///
/// <para>Every injected event carries <see cref="SignatureTag"/> in
/// <c>dwExtraInfo</c>, the conventional Win32 way to mark input as one's own.
/// It costs nothing and makes the origin of these events legible to anything
/// that inspects them, including NotIdle itself.</para>
/// </summary>
public sealed class Win32SyntheticInput : ISyntheticInputSink
{
    /// <summary>Marker written into <c>dwExtraInfo</c> on every event we inject.</summary>
    public const int SignatureTag = 0x4E49_444C; // "NIDL"

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;

    // Reused across calls — the hot path allocates nothing.
    private readonly INPUT[] _buffer = new INPUT[2];

    public CursorPoint Pulse(bool restoreCursor)
    {
        CursorPoint before = ReadCursor();

        // Out and back in a single SendInput call, so the two moves are delivered
        // atomically and no other input can be interleaved between them.
        _buffer[0] = MoveBy(1, 0);
        _buffer[1] = MoveBy(-1, 0);
        _ = SendInput(2, _buffer, Marshal.SizeOf<INPUT>());

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
                dwExtraInfo = SignatureTag,
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
}
