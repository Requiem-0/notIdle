using System.Threading;

namespace NotIdle.Core;

/// <summary>
/// Performs the minimum system-level work needed to keep the session awake.
///
/// <para><b>Two mechanisms, in order of preference.</b></para>
/// <list type="number">
/// <item>
/// <description>
/// <c>SetThreadExecutionState</c> — the supported Windows API for "don't sleep
/// while I'm doing this". It is held for as long as keep-awake is on and costs
/// nothing; it prevents system sleep, and optionally display-off and the
/// screensaver. This is the primary mechanism.
/// </description>
/// </item>
/// <item>
/// <description>
/// A 1 px cursor pulse on a randomized interval. The execution-state request does
/// not touch the <em>input</em> idle clock, which is what session-level idle and
/// lock-on-idle policies read. One imperceptible movement resets it. That is the
/// entire extent of the synthetic input: no clicks, no keystrokes, no window or
/// control interaction, and nothing that imitates a person working.
/// </description>
/// </item>
/// </list>
///
/// <para>Intervals are drawn uniformly from the configured
/// [min, max] range plus sub-second jitter, so the cadence is not a fixed
/// heartbeat.</para>
/// </summary>
public sealed class KeepAwakeEngine : IDisposable
{
    private readonly ISyntheticInputSink _sink;
    private readonly IExecutionStateController _power;
    private readonly Lock _gate = new();

    private bool _wakeLockHeld;
    private bool _wakeLockDisplay;
    private long _nextPulseTick;
    private int _currentIntervalSeconds;
    private long _pulseCount;
    private bool _disposed;

    public KeepAwakeEngine(ISyntheticInputSink sink, IExecutionStateController power)
    {
        _sink = sink;
        _power = power;
    }

    /// <summary>Total cursor pulses emitted since start.</summary>
    public long PulseCount
    {
        get { lock (_gate) return _pulseCount; }
    }

    /// <summary>The interval, in seconds, currently being waited out. 0 before the first pulse.</summary>
    public int CurrentIntervalSeconds
    {
        get { lock (_gate) return _currentIntervalSeconds; }
    }

    /// <summary>True while the OS-level keep-awake request is held.</summary>
    public bool WakeLockHeld
    {
        get { lock (_gate) return _wakeLockHeld; }
    }

    /// <summary>Time until the next pulse is due, or <see cref="TimeSpan.Zero"/> if due now.</summary>
    public TimeSpan TimeUntilNextPulse(long nowTick)
    {
        lock (_gate)
        {
            long remaining = _nextPulseTick - nowTick;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(remaining);
        }
    }

    /// <summary>
    /// Ensure the OS keep-awake request is held. Idempotent, and re-issued if the
    /// display preference changes.
    /// </summary>
    public void HoldWakeLock(bool keepDisplayOn)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_wakeLockHeld && _wakeLockDisplay == keepDisplayOn) return;
            _power.Acquire(keepDisplayOn);
            _wakeLockHeld = true;
            _wakeLockDisplay = keepDisplayOn;
        }
    }

    /// <summary>Drop the OS keep-awake request. Idempotent.</summary>
    public void ReleaseWakeLock()
    {
        lock (_gate)
        {
            if (!_wakeLockHeld) return;
            _power.Release();
            _wakeLockHeld = false;
        }
    }

    /// <summary>
    /// Re-arm so the next entry into keep-awake pulses immediately. Called on every
    /// tick spent outside keep-awake, so the schedule never carries stale state.
    /// </summary>
    public void ResetSchedule(long nowTick)
    {
        lock (_gate)
        {
            _nextPulseTick = nowTick;
            _currentIntervalSeconds = 0;
        }
    }

    /// <summary>
    /// Emit a pulse if one is due, and pick the next randomized interval.
    /// </summary>
    /// <returns>True if a pulse was emitted; <paramref name="cursorAfter"/> is then valid.</returns>
    public bool TryPulse(long nowTick, Settings settings, out CursorPoint cursorAfter)
    {
        lock (_gate)
        {
            cursorAfter = default;
            if (_disposed || nowTick < _nextPulseTick) return false;

            cursorAfter = _sink.Pulse(settings.RestoreCursor);
            _pulseCount++;
            ScheduleNext(nowTick, settings);
            return true;
        }
    }

    /// <summary>
    /// Emit one pulse right now regardless of schedule, for the UI's "Test" action.
    /// It counts as a keep-awake event because that is exactly what it is.
    /// </summary>
    public CursorPoint PulseNow(long nowTick, Settings settings)
    {
        lock (_gate)
        {
            CursorPoint cursor = _sink.Pulse(settings.RestoreCursor);
            _pulseCount++;
            ScheduleNext(nowTick, settings);
            return cursor;
        }
    }

    /// <summary>Zero the pulse counter.</summary>
    public void ResetStatistics()
    {
        lock (_gate) _pulseCount = 0;
    }

    // Uniform over [min, max] seconds, plus sub-second jitter so successive pulses
    // don't land on a whole-second grid.
    private void ScheduleNext(long nowTick, Settings settings)
    {
        int min = settings.MinIntervalSeconds;
        int max = Math.Max(min, settings.MaxIntervalSeconds);
        _currentIntervalSeconds = Random.Shared.Next(min, max + 1);
        _nextPulseTick = nowTick + (_currentIntervalSeconds * 1000L) + Random.Shared.Next(0, 1000);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_wakeLockHeld)
            {
                _power.Release();
                _wakeLockHeld = false;
            }
        }
    }
}
