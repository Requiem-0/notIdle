namespace NotIdle.Core;

/// <summary>Screen coordinates of the mouse cursor.</summary>
public readonly record struct CursorPoint(int X, int Y);

/// <summary>
/// The bounding box of the whole virtual desktop, in screen coordinates. Path
/// movement is clamped to it so a badly-typed waypoint can never fling the
/// pointer somewhere unreachable.
/// </summary>
public readonly record struct ScreenBounds(int Left, int Top, int Width, int Height)
{
    /// <summary>Used when the platform refuses to answer; a plausible single 1080p monitor.</summary>
    public static readonly ScreenBounds Fallback = new(0, 0, 1920, 1080);

    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public CursorPoint Center => new(Left + (Width / 2), Top + (Height / 2));

    public CursorPoint Clamp(CursorPoint p) => new(
        Math.Clamp(p.X, Left, Math.Max(Left, Right - 1)),
        Math.Clamp(p.Y, Top, Math.Max(Top, Bottom - 1)));
}

/// <summary>Which button a synthetic click presses.</summary>
public enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>How synthetic mouse scrolling behaves.</summary>
public enum ScrollMode
{
    /// <summary>Scroll down and immediately back up. Resets idle time without displacing content.</summary>
    Rebound,

    /// <summary>Scroll downward only.</summary>
    Down,

    /// <summary>Scroll upward only.</summary>
    Up,

    /// <summary>Alternate between downward and upward scrolling on successive runs.</summary>
    Alternating,
}

/// <summary>
/// System-wide input telemetry. Abstracted so the monitoring layer is testable
/// without a real desktop session.
/// </summary>
public interface ISystemInputSource
{
    /// <summary>
    /// Time since the last system-wide input event of any kind — keyboard, mouse
    /// movement, or mouse button. A near-free syscall.
    /// </summary>
    /// <remarks>
    /// This counter is reset by <em>injected</em> input as well as real input, so
    /// callers must attribute events themselves. See
    /// <see cref="UserActivityMonitor"/>.
    /// </remarks>
    TimeSpan GetIdleTime();

    /// <summary>Current cursor position, used to corroborate input attribution.</summary>
    CursorPoint GetCursorPosition();
}

/// <summary>
/// Emits the synthetic input NotIdle is allowed to produce.
///
/// <para>Four primitives, each opted into separately by the user:
/// the imperceptible keep-awake <see cref="Pulse"/> (always available),
/// <see cref="MoveTo"/> for tracing a configured path, <see cref="Click"/>
/// for the auto-clicker, and <see cref="Scroll"/> for mouse wheel activity.
/// Keystrokes are still never synthesized.</para>
/// </summary>
public interface ISyntheticInputSink
{
    /// <summary>
    /// Emit one imperceptible cursor movement (1 px out and back).
    /// </summary>
    /// <param name="restoreCursor">
    /// When true the cursor is explicitly returned to where it started, so a pulse
    /// that races the user's own hand cannot leave the pointer displaced.
    /// </param>
    /// <returns>Where the cursor ended up, so the caller can keep its baseline in sync.</returns>
    CursorPoint Pulse(bool restoreCursor);

    /// <summary>
    /// Move the pointer to an absolute screen position, as a real mouse move —
    /// not a silent <c>SetCursorPos</c>, so it resets the input-idle clock too.
    /// </summary>
    /// <returns>Where the cursor actually ended up.</returns>
    CursorPoint MoveTo(int x, int y);

    /// <summary>Press and release a mouse button at the current pointer position.</summary>
    void Click(MouseButton button, bool doubleClick);

    /// <summary>
    /// Rotate the mouse wheel by the specified number of notches (positive = up, negative = down).
    /// </summary>
    void Scroll(int notches);

    /// <summary>Bounds of the whole virtual desktop, for clamping path waypoints.</summary>
    ScreenBounds GetVirtualScreen();
}

/// <summary>
/// Wraps the supported Windows "keep this session awake" request
/// (<c>SetThreadExecutionState</c>). This — not cursor movement — is the primary
/// mechanism for preventing sleep.
/// </summary>
public interface IExecutionStateController
{
    /// <summary>Request that the system (and optionally the display) stay awake.</summary>
    void Acquire(bool keepDisplayOn);

    /// <summary>Drop the request and let normal power policy resume.</summary>
    void Release();
}

/// <summary>Describes system battery/power status.</summary>
public interface ISystemPowerSource
{
    /// <summary>True when running on battery (unplugged from AC power).</summary>
    bool IsOnBattery { get; }

    /// <summary>Battery percentage remaining (0-100), or -1 if unknown / AC desktop.</summary>
    int BatteryPercentage { get; }
}

/// <summary>Describes the window the user is currently focused on.</summary>
/// <param name="Handle">Opaque window handle; used only to detect focus changes.</param>
/// <param name="Process">Executable name without extension, e.g. "Figma". May be empty.</param>
/// <param name="IsFullScreen">The foreground window covers an entire monitor.</param>
/// <param name="IsPresenting">
/// Windows reports the user as presenting, gaming (exclusive D3D), or otherwise
/// not to be interrupted.
/// </param>
public readonly record struct ForegroundWindow(
    nint Handle,
    string Process,
    bool IsFullScreen,
    bool IsPresenting)
{
    public static readonly ForegroundWindow Unknown = new(0, string.Empty, false, false);

    /// <summary>True when keep-awake actions should stand down out of politeness.</summary>
    public bool WantsUninterrupted => IsFullScreen || IsPresenting;
}

/// <summary>Reads the foreground window. One cheap syscall batch per poll.</summary>
public interface IForegroundWindowSource
{
    ForegroundWindow Read();
}

/// <summary>Persistence for <see cref="Settings"/>. Implementations must never throw on load.</summary>
public interface ISettingsStore
{
    Settings Load();
    void Save(Settings settings);
}

/// <summary>Represents a snapshot interval of activity for timeline visualization.</summary>
public readonly record struct ActivityBucket(DateTimeOffset Time, ActivityState State);

