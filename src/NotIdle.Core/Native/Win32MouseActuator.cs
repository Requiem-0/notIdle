using System.Runtime.InteropServices;

namespace NotIdle.Core.Native;

/// <summary>
/// Traces a small circle with the cursor via <c>SendInput</c>. Each
/// <see cref="Nudge"/> call advances one step along the circle, producing a
/// smooth circular motion when called every ~100 ms.
/// </summary>
public sealed class Win32MouseActuator : IMouseActuator
{
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;

    // 8 points on a ~60px radius circle. Each entry is the relative offset
    // from the previous point to the next.
    private static readonly (int dx, int dy)[] CircleSteps =
    [
        (-18, 42), (-42, 18), (-42, -18), (-18, -42),
        (18, -42), (42, -18), (42, 18), (18, 42),
    ];

    private int _step;

    // Reused across calls — the hot path allocates nothing.
    private readonly INPUT[] _buffer = new INPUT[1];

    public void Nudge()
    {
        (int dx, int dy) = CircleSteps[_step];
        _step = (_step + 1) % CircleSteps.Length;
        Move(dx, dy);
    }

    private void Move(int dx, int dy)
    {
        _buffer[0] = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE }
            }
        };
        _ = SendInput(1, _buffer, Marshal.SizeOf<INPUT>());
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
}
