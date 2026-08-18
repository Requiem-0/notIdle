using System.Threading;

namespace NotIdle.Core.Monitoring;

/// <summary>One poll's worth of activity information.</summary>
/// <param name="GenuineInputDetected">Real interaction happened since the previous poll.</param>
/// <param name="TimeSinceGenuineInput">How long since the last <em>real</em> interaction.</param>
/// <param name="Cursor">Cursor position at poll time.</param>
public readonly record struct ActivitySample(
    bool GenuineInputDetected,
    TimeSpan TimeSinceGenuineInput,
    CursorPoint Cursor);

/// <summary>
/// Detects <em>genuine</em> user interaction and keeps a genuine-idle clock that
/// NotIdle's own synthetic input can never reset.
///
/// <para><b>Signals.</b> <c>GetLastInputInfo</c> covers keyboard, mouse movement and
/// mouse buttons system-wide in one near-free syscall — that is the primary signal.
/// Cursor position and foreground-window changes are folded in as corroborating
/// signals: they catch interaction the timestamp alone would misattribute, and a
/// focus change is itself evidence someone is at the keyboard.</para>
///
/// <para><b>Attribution.</b> Injected input resets the same OS counter as real input,
/// so the raw timestamp cannot distinguish the two. When
/// <see cref="NoteSyntheticInput"/> records the tick at which NotIdle pulsed, a
/// subsequent input timestamp landing inside
/// <see cref="SyntheticAttributionWindowMs"/> of that tick — with the cursor sitting
/// exactly where the pulse left it — is credited to NotIdle and ignored. Anything
/// else is genuine. The failure mode is deliberately conservative in the honest
/// direction: an ambiguous event is only discarded when both the timing and the
/// cursor agree, so real input is far more likely to be over-counted than
/// under-counted.</para>
///
/// <para>Polling rather than installing a low-level input hook keeps the CPU parked
/// while the user is active — exactly when a hook would be most expensive — at the
/// cost of one poll interval of latency, which is irrelevant against a
/// minutes-long idle threshold.</para>
/// </summary>
public sealed class UserActivityMonitor
{
    /// <summary>
    /// How close an input timestamp must be to our injection tick to be credited to
    /// NotIdle. Wide enough to absorb the ~15 ms system timer granularity and
    /// SendInput delivery latency, far narrower than the poll interval.
    /// </summary>
    private const int SyntheticAttributionWindowMs = 400;

    /// <summary>
    /// Minimum advance in the last-input timestamp that counts as a new event.
    /// Absorbs the couple of milliseconds of jitter between two reads of the tick
    /// counters when nothing at all has happened.
    /// </summary>
    private const int NewInputEpsilonMs = 100;

    private readonly ISystemInputSource _input;
    private readonly Lock _gate = new();

    private long _lastGenuineTick;
    private long _lastObservedInputTick;
    private long _lastSyntheticTick = long.MinValue / 2;
    private CursorPoint _lastCursor;
    private nint _lastWindow;
    private long _genuineEventCount;

    public UserActivityMonitor(ISystemInputSource input)
    {
        _input = input;
        _lastGenuineTick = Environment.TickCount64;
        _lastCursor = SafeCursor();
    }

    /// <summary>Number of distinct genuine interaction events observed since start.</summary>
    public long GenuineEventCount
    {
        get { lock (_gate) return _genuineEventCount; }
    }

    /// <summary>Time since the last genuine interaction. Never advanced by our own pulses.</summary>
    public TimeSpan TimeSinceGenuineInput
    {
        get { lock (_gate) return TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastGenuineTick); }
    }

    /// <summary>
    /// The system's last-input timestamp as of the most recent <see cref="Sample"/>,
    /// whoever caused it. Unlike the genuine-idle clock this one <em>is</em> moved by
    /// our own injections — which is exactly what makes it useful: comparing it
    /// against the tick we injected at tells us whether the OS accepted our input at
    /// all. See <see cref="Actions.AdaptiveTuner"/>.
    /// </summary>
    public long LastObservedInputTick
    {
        get { lock (_gate) return _lastObservedInputTick; }
    }

    /// <summary>
    /// Take one reading. <paramref name="foregroundWindow"/> comes from
    /// <see cref="WindowMonitor"/> so both monitors observe the same instant.
    /// </summary>
    public ActivitySample Sample(nint foregroundWindow, long nowTick)
    {
        lock (_gate)
        {
            long lastInputTick = nowTick - (long)_input.GetIdleTime().TotalMilliseconds;
            CursorPoint cursor = SafeCursor();

            bool newInput = lastInputTick > _lastObservedInputTick + NewInputEpsilonMs;
            bool cursorMoved = cursor != _lastCursor;

            // A first observation has no previous window to compare against.
            bool windowChanged = _lastWindow != 0 && foregroundWindow != 0 && foregroundWindow != _lastWindow;

            // Ours only if the timing lines up AND the pointer is untouched.
            bool attributableToUs =
                Math.Abs(lastInputTick - _lastSyntheticTick) <= SyntheticAttributionWindowMs && !cursorMoved;

            bool genuine = (newInput && !attributableToUs) || cursorMoved || windowChanged;

            if (lastInputTick > _lastObservedInputTick) _lastObservedInputTick = lastInputTick;
            _lastCursor = cursor;
            if (foregroundWindow != 0) _lastWindow = foregroundWindow;

            if (genuine)
            {
                _lastGenuineTick = nowTick;
                _genuineEventCount++;
            }

            return new ActivitySample(
                genuine,
                TimeSpan.FromMilliseconds(nowTick - _lastGenuineTick),
                cursor);
        }
    }

    /// <summary>
    /// Record that NotIdle just injected input, so the next poll discounts it.
    /// Must be called immediately after the pulse.
    /// </summary>
    /// <param name="tick">The tick at which the pulse was emitted.</param>
    /// <param name="cursorAfterPulse">
    /// Where the pulse left the cursor. Adopting it as the baseline means a pulse
    /// never registers as "the cursor moved", whether or not it was restored.
    /// </param>
    public void NoteSyntheticInput(long tick, CursorPoint cursorAfterPulse)
    {
        lock (_gate)
        {
            _lastSyntheticTick = tick;
            _lastCursor = cursorAfterPulse;
        }
    }

    /// <summary>
    /// Treat this instant as genuine activity. Used when resuming from a paused
    /// state so keep-awake never fires the moment it is switched on.
    /// </summary>
    public void MarkActiveNow()
    {
        lock (_gate)
        {
            _lastGenuineTick = Environment.TickCount64;
            _lastCursor = SafeCursor();
        }
    }

    private CursorPoint SafeCursor()
    {
        try { return _input.GetCursorPosition(); }
        catch { return _lastCursor; } // e.g. secure desktop — keep the old baseline.
    }
}
