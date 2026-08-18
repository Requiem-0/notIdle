using System.Threading;

namespace NotIdle.Core.Monitoring;

/// <summary>
/// Tracks the foreground application so keep-awake can be context-aware: stand
/// down while the user is gaming, presenting, or otherwise in a full-screen app
/// where an unexpected cursor twitch would be disruptive.
///
/// The last good reading is retained if a probe fails, so a transient failure
/// (secure desktop, window vanishing mid-poll) can't be misread as "no longer
/// full screen".
/// </summary>
public sealed class WindowMonitor
{
    private readonly IForegroundWindowSource _source;
    private readonly Lock _gate = new();
    private ForegroundWindow _current = ForegroundWindow.Unknown;

    public WindowMonitor(IForegroundWindowSource source) => _source = source;

    /// <summary>The most recent reading. Safe to read from any thread.</summary>
    public ForegroundWindow Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Re-read the foreground window. One batch of cheap syscalls.</summary>
    public ForegroundWindow Poll()
    {
        ForegroundWindow next;
        try
        {
            next = _source.Read();
        }
        catch
        {
            return Current; // Keep the last good reading rather than guessing.
        }

        lock (_gate)
        {
            if (next.Handle != 0) _current = next;
            return _current;
        }
    }
}
