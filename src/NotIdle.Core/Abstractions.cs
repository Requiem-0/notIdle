namespace NotIdle.Core;

/// <summary>Screen coordinates of the mouse cursor.</summary>
public readonly record struct CursorPoint(int X, int Y);

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
/// Emits the single, minimal synthetic input event NotIdle is allowed to produce.
/// Deliberately narrow: no clicks, no keystrokes, no window interaction.
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
