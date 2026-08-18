namespace NotIdle.Core;

/// <summary>
/// The observable state of the session, as judged from <em>genuine</em> user
/// input only. NotIdle's own synthetic pulses never move this machine.
/// </summary>
public enum ActivityState
{
    /// <summary>Keep-awake is switched off. No timers, no power request, no pulses.</summary>
    Disabled,

    /// <summary>Genuine user interaction was seen very recently.</summary>
    Active,

    /// <summary>No genuine interaction, but the idle threshold has not been reached yet.</summary>
    Idle,

    /// <summary>Idle threshold passed — the session is being held awake.</summary>
    KeepAwake,

    /// <summary>
    /// Enabled, but standing down on cursor pulses because the foreground app is
    /// full-screen or the system reports presentation/game mode. The power request
    /// is still held: full screen is exactly when the display should stay on.
    /// </summary>
    Suspended,

    /// <summary>
    /// The workstation is locked. The power request is held so unattended work
    /// keeps running, but cursor pulses are pointless — the session is already
    /// locked — and the secure desktop refuses cursor queries anyway.
    /// </summary>
    Locked,
}

/// <summary>Display helpers kept out of the enum so <see cref="ActivityState"/> stays a plain value.</summary>
public static class ActivityStateText
{
    public static string ToDisplayString(this ActivityState state) => state switch
    {
        ActivityState.Active => "Active",
        ActivityState.Idle => "Idle",
        ActivityState.KeepAwake => "Keep Awake",
        ActivityState.Suspended => "Full screen",
        ActivityState.Locked => "Locked",
        _ => "Off",
    };

    /// <summary>
    /// States in which NotIdle is holding the session awake on the user's behalf.
    /// Used for the "total keep-awake time" statistic — Active and Idle don't count,
    /// because the user is present and the power request is merely precautionary.
    /// </summary>
    public static bool IsHoldingAwake(this ActivityState state) =>
        state is ActivityState.KeepAwake or ActivityState.Locked;

    /// <summary>States in which a cursor pulse would be useful. Only one qualifies.</summary>
    public static bool AllowsCursorPulse(this ActivityState state) =>
        state is ActivityState.KeepAwake;
}
