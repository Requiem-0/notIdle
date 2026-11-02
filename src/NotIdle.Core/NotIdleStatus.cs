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
/// <param name="IntervalDescription">Configured pulse interval range, formatted.</param>
/// <param name="CurrentIntervalSeconds">The pulse interval currently being waited out; 0 if none.</param>
/// <param name="TimeUntilNextAction">Countdown to the next pulse.</param>
/// <param name="ForegroundApp">Executable name of the focused app.</param>
/// <param name="WakeLockHeld">Whether the OS keep-awake request is currently held.</param>
/// <param name="AutoOffArmed">Whether an auto-off deadline is running.</param>
/// <param name="TimeUntilAutoOff">Countdown to auto-off; zero when not armed.</param>
/// <param name="ActionsDescription">Which actions are switched on, e.g. "pulse + click".</param>
/// <param name="PathRuns">Path runs started.</param>
/// <param name="PathAbortedRuns">Path runs the user interrupted by taking the mouse.</param>
/// <param name="PathRunning">A path is being traced right now.</param>
/// <param name="TimeUntilNextPath">Countdown to the next path run.</param>
/// <param name="PathDescription">Configured path, formatted.</param>
/// <param name="ClickCount">Clicks emitted.</param>
/// <param name="AutoClickActive">The auto-clicker is armed and firing.</param>
/// <param name="ClickInterruptions">Times the clicker stood down because the pointer moved.</param>
/// <param name="AutoClickDescription">Configured click cadence, formatted.</param>
/// <param name="ScrollCount">Scroll actions emitted.</param>
/// <param name="TimeUntilNextScroll">Countdown to the next scroll action.</param>
/// <param name="ScrollDescription">Configured scroll cadence, formatted.</param>
/// <param name="IsOnBattery">Whether the system is running on battery power.</param>
/// <param name="BurstModeEnabled">Whether human work burst mode is enabled.</param>
/// <param name="TargetActivityPercent">Target activity score percentage (e.g. 80%).</param>
/// <param name="BurstRunning">Whether a burst cycle is running right now.</param>
/// <param name="BurstCount">Completed burst cycles since start.</param>
/// <param name="BurstDescription">Configured burst mode summary.</param>
/// <param name="AdaptiveMode">Whether NotIdle is choosing the actions itself.</param>
/// <param name="AdaptiveNote">What the adaptive tuner decided and why. Empty when adaptive mode is off.</param>
/// <param name="RecentHistory">Recent activity timeline history buckets for visualization.</param>
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
    bool WakeLockHeld,
    bool AutoOffArmed,
    TimeSpan TimeUntilAutoOff,
    string ActionsDescription,
    long PathRuns,
    long PathAbortedRuns,
    bool PathRunning,
    TimeSpan TimeUntilNextPath,
    string PathDescription,
    long ClickCount,
    bool AutoClickActive,
    long ClickInterruptions,
    string AutoClickDescription,
    long ScrollCount,
    TimeSpan TimeUntilNextScroll,
    string ScrollDescription,
    bool BurstModeEnabled,
    int TargetActivityPercent,
    bool BurstRunning,
    long BurstCount,
    string BurstDescription,
    bool IsOnBattery,
    int BatteryPercent,
    bool AdaptiveMode,
    string AdaptiveNote,
    IReadOnlyList<ActivityBucket> RecentHistory)
{
    public static readonly NotIdleStatus Empty = new(
        false, ActivityState.Disabled, TimeSpan.Zero, TimeSpan.Zero,
        0, 0, 0, "—", 0, TimeSpan.Zero, "—", false, false, TimeSpan.Zero,
        "—", 0, 0, false, TimeSpan.Zero, "off",
        0, false, 0, "off",
        0, TimeSpan.Zero, "off",
        false, 80, false, 0, "off",
        false, -1,
        false, string.Empty,
        []);
}
