namespace NotIdle.Core;

/// <summary>Source of system-wide user-input idle time. Abstracted so the engine is testable.</summary>
public interface IInputMonitor
{
    /// <summary>Time since the last keyboard/mouse input, system-wide. A near-free syscall.</summary>
    TimeSpan GetIdleTime();
}

/// <summary>Performs the "keep awake" action. Kept tiny and side-effect-only.</summary>
public interface IMouseActuator
{
    /// <summary>Emit one imperceptible cursor nudge (moves and returns).</summary>
    void Nudge();
}

/// <summary>Persistence for <see cref="Settings"/>. Implementations must never throw on load.</summary>
public interface ISettingsStore
{
    Settings Load();
    void Save(Settings settings);
}
