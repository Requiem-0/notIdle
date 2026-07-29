namespace NotIdle.Core;

/// <summary>Observable engine states, surfaced to the UI for status display.</summary>
public enum AppState
{
    /// <summary>Feature disabled or paused by the user. The engine timer is stopped.</summary>
    Paused,

    /// <summary>Enabled and watching; the user is currently active.</summary>
    Monitoring,

    /// <summary>User has gone idle but the threshold has not yet been reached.</summary>
    Idle,

    /// <summary>Idle threshold reached; the mouse action is being performed.</summary>
    ActionRunning,
}
