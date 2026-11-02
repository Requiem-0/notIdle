using NotIdle.Core;

namespace NotIdle.Core.Tests;

/// <summary>
/// A simulated desktop on a virtual clock. Crucially, every injected action resets
/// <see cref="LastInputTick"/> exactly as a real <c>SendInput</c> resets
/// <c>GetLastInputInfo</c> — that shared counter is the whole reason attribution
/// is hard, so the fake must reproduce it or the tests prove nothing.
/// </summary>
public sealed class FakeDesktop : ISystemInputSource, ISyntheticInputSink, IExecutionStateController, ISystemPowerSource
{
    public long Now { get; set; } = 1_000_000;
    public long LastInputTick { get; set; } = 1_000_000;
    public CursorPoint Cursor { get; set; } = new(500, 500);
    public ScreenBounds Screen { get; set; } = new(0, 0, 1920, 1080);

    public int PulseCount { get; private set; }
    public int MoveCount { get; private set; }
    public int ClickCount { get; private set; }
    public int ScrollCount { get; private set; }
    public int LastScrollNotches { get; private set; }
    public MouseButton LastButton { get; private set; }
    public bool LastClickWasDouble { get; private set; }
    public bool PowerHeld { get; private set; }
    public bool DisplayHeld { get; private set; }
    public bool IsOnBattery { get; set; }
    public int BatteryPercentage { get; set; } = 100;

    /// <summary>
    /// When true, injected input moves the pointer but not the idle timestamp —
    /// the "our input is being swallowed" case the adaptive tuner escalates on.
    /// </summary>
    public bool SwallowInjectedInput { get; set; }

    public TimeSpan GetIdleTime() => TimeSpan.FromMilliseconds(Now - LastInputTick);
    public CursorPoint GetCursorPosition() => Cursor;
    public ScreenBounds GetVirtualScreen() => Screen;

    public CursorPoint Pulse(bool restoreCursor)
    {
        PulseCount++;
        RegisterInput();
        if (!restoreCursor) Cursor = new(Cursor.X + 1, Cursor.Y);
        return Cursor;
    }

    public CursorPoint MoveTo(int x, int y)
    {
        MoveCount++;
        RegisterInput();
        Cursor = Screen.Clamp(new CursorPoint(x, y));
        return Cursor;
    }

    /// <summary>Notified where the pointer was for each click, so tests can assert click points.</summary>
    public Action<CursorPoint>? OnClick { get; set; }

    public void Click(MouseButton button, bool doubleClick)
    {
        ClickCount++;
        LastButton = button;
        LastClickWasDouble = doubleClick;
        RegisterInput();
        OnClick?.Invoke(Cursor);
    }

    public void Scroll(int notches)
    {
        ScrollCount++;
        LastScrollNotches = notches;
        RegisterInput();
    }

    public void Acquire(bool keepDisplayOn) { PowerHeld = true; DisplayHeld = keepDisplayOn; }
    public void Release() => PowerHeld = false;

    /// <summary>A real interaction. Keyboard input moves the timestamp but not the cursor.</summary>
    public void RealInput(bool moveMouse = true)
    {
        LastInputTick = Now;
        if (moveMouse) Cursor = new(Cursor.X + 7, Cursor.Y - 3);
    }

    // The OS cannot tell who sent it — unless something upstream is dropping our
    // events, which is what SwallowInjectedInput models.
    private void RegisterInput()
    {
        if (!SwallowInjectedInput) LastInputTick = Now;
    }
}
