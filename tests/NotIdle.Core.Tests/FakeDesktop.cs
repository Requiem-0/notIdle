using NotIdle.Core;

namespace NotIdle.Core.Tests;

/// <summary>
/// A simulated desktop on a virtual clock. Crucially, <see cref="Pulse"/> resets
/// <see cref="LastInputTick"/> exactly as a real <c>SendInput</c> resets
/// <c>GetLastInputInfo</c> — that shared counter is the whole reason attribution
/// is hard, so the fake must reproduce it or the tests prove nothing.
/// </summary>
public sealed class FakeDesktop : ISystemInputSource, ISyntheticInputSink, IExecutionStateController
{
    public long Now { get; set; } = 1_000_000;
    public long LastInputTick { get; set; } = 1_000_000;
    public CursorPoint Cursor { get; set; } = new(500, 500);

    public int PulseCount { get; private set; }
    public bool PowerHeld { get; private set; }
    public bool DisplayHeld { get; private set; }

    public TimeSpan GetIdleTime() => TimeSpan.FromMilliseconds(Now - LastInputTick);
    public CursorPoint GetCursorPosition() => Cursor;

    public CursorPoint Pulse(bool restoreCursor)
    {
        PulseCount++;
        LastInputTick = Now;                                   // the OS cannot tell who sent it
        if (!restoreCursor) Cursor = new(Cursor.X + 1, Cursor.Y);
        return Cursor;
    }

    public void Acquire(bool keepDisplayOn) { PowerHeld = true; DisplayHeld = keepDisplayOn; }
    public void Release() => PowerHeld = false;

    /// <summary>A real interaction. Keyboard input moves the timestamp but not the cursor.</summary>
    public void RealInput(bool moveMouse = true)
    {
        LastInputTick = Now;
        if (moveMouse) Cursor = new(Cursor.X + 7, Cursor.Y - 3);
    }
}
