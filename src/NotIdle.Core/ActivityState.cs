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
    /// Enabled, but standing down because the foreground app is full-screen or the
    /// system reports presentation/game mode.
    /// </summary>
    Suspended,
}

/// <summary>Display helpers kept out of the enum so <see cref="ActivityState"/> stays a plain value.</summary>
public static class ActivityStateText
{
    public static string ToDisplayString(this ActivityState state) => state switch
    {
        ActivityState.Active => "Active",
        ActivityState.Idle => "Idle",
        ActivityState.KeepAwake => "Keep Awake",
        ActivityState.Suspended => "Paused (full screen)",
        _ => "Off",
    };
}
