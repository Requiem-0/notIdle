namespace NotIdle.Core;

/// <summary>
/// An immutable snapshot of everything the dashboard displays. Built under the
/// controller's lock so the UI always sees a mutually consistent picture.
/// </summary>
/// <param name="Enabled">Master keep-awake toggle.</param>
/// <param name="State">Current state of the machine.</param>
/// <param name="TimeSinceGenuineInput">Since the last <em>real</em> interaction.</param>
/// <param name="TotalKeepAwakeDuration">Cumulative time spent holding the session awake.</param>
/// <param name="KeepAwakeEvents">Cursor pulses emitted.</param>
/// <param name="KeepAwakeSessions">Times keep-awake has engaged.</param>
/// <param name="GenuineInputEvents">Real interactions observed.</param>
/// <param name="IntervalDescription">Configured interval range, formatted.</param>
/// <param name="CurrentIntervalSeconds">The interval currently being waited out; 0 if none.</param>
/// <param name="TimeUntilNextAction">Countdown to the next pulse.</param>
/// <param name="ForegroundApp">Executable name of the focused app.</param>
/// <param name="WakeLockHeld">Whether the OS keep-awake request is currently held.</param>
public sealed record NotIdleStatus(
    bool Enabled,
    ActivityState State,
    TimeSpan TimeSinceGenuineInput,
    TimeSpan TotalKeepAwakeDuration,
    long KeepAwakeEvents,
    int KeepAwakeSessions,
    long GenuineInputEvents,
    string IntervalDescription,
    int CurrentIntervalSeconds,
    TimeSpan TimeUntilNextAction,
    string ForegroundApp,
    bool WakeLockHeld)
{
    public static readonly NotIdleStatus Empty = new(
        false, ActivityState.Disabled, TimeSpan.Zero, TimeSpan.Zero,
        0, 0, 0, "—", 0, TimeSpan.Zero, "—", false);
}
