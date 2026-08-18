using System.Threading;

namespace NotIdle.Core;

/// <summary>
/// The state machine: turns "how long since genuine input" into
/// Active / Idle / Keep-Awake, and accumulates how long keep-awake has been
/// engaged.
///
/// Pure logic — no timers, no syscalls, no I/O — so every transition is
/// reproducible from its inputs.
/// </summary>
public sealed class IdleStateManager
{
    /// <summary>
    /// Genuine idle below this still reads as "Active" in the UI. Capped at half
    /// the threshold so a very short threshold can still reach Idle.
    /// </summary>
    private static readonly TimeSpan MaxActiveWindow = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();

    private ActivityState _state = ActivityState.Disabled;
    private long _keepAwakeEnteredTick;
    private TimeSpan _completedKeepAwake;
    private int _keepAwakeSessions;

    /// <summary>Raised on every transition, with (previous, next). May fire on a pool thread.</summary>
    public event Action<ActivityState, ActivityState>? StateChanged;

    public ActivityState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Number of times keep-awake has engaged since start.</summary>
    public int KeepAwakeSessions
    {
        get { lock (_gate) return _keepAwakeSessions; }
    }

    /// <summary>Total time spent in <see cref="ActivityState.KeepAwake"/>, including the current run.</summary>
    public TimeSpan TotalKeepAwakeDuration => TotalKeepAwakeAt(Environment.TickCount64);

    /// <summary>Same as <see cref="TotalKeepAwakeDuration"/> but sampled at a caller-supplied tick.</summary>
    public TimeSpan TotalKeepAwakeAt(long nowTick)
    {
        lock (_gate)
        {
            if (_state != ActivityState.KeepAwake) return _completedKeepAwake;
            return _completedKeepAwake + TimeSpan.FromMilliseconds(nowTick - _keepAwakeEnteredTick);
        }
    }

    /// <summary>
    /// Advance the machine one step.
    /// </summary>
    /// <param name="enabled">Master keep-awake toggle.</param>
    /// <param name="standDown">Context says don't act (full screen / presenting).</param>
    /// <param name="sinceGenuineInput">Time since real user interaction.</param>
    /// <param name="idleThreshold">Configured idle threshold.</param>
    /// <param name="nowTick">Monotonic tick for duration accounting.</param>
    public ActivityState Evaluate(
        bool enabled,
        bool standDown,
        TimeSpan sinceGenuineInput,
        TimeSpan idleThreshold,
        long nowTick)
    {
        ActivityState next = Classify(enabled, standDown, sinceGenuineInput, idleThreshold);
        Transition(next, nowTick);
        return next;
    }

    private static ActivityState Classify(
        bool enabled,
        bool standDown,
        TimeSpan sinceGenuineInput,
        TimeSpan idleThreshold)
    {
        if (!enabled) return ActivityState.Disabled;
        if (standDown) return ActivityState.Suspended;
        if (sinceGenuineInput >= idleThreshold) return ActivityState.KeepAwake;

        TimeSpan activeWindow = idleThreshold / 2;
        if (activeWindow > MaxActiveWindow) activeWindow = MaxActiveWindow;
        return sinceGenuineInput < activeWindow ? ActivityState.Active : ActivityState.Idle;
    }

    /// <summary>Force the machine back to <see cref="ActivityState.Disabled"/>, closing any open run.</summary>
    public void Reset() => Transition(ActivityState.Disabled, Environment.TickCount64);

    /// <summary>Zero the accumulated statistics without disturbing the current state.</summary>
    public void ResetStatistics()
    {
        lock (_gate)
        {
            _completedKeepAwake = TimeSpan.Zero;
            _keepAwakeSessions = _state == ActivityState.KeepAwake ? 1 : 0;
            _keepAwakeEnteredTick = Environment.TickCount64;
        }
    }

    private void Transition(ActivityState next, long nowTick)
    {
        ActivityState previous;
        lock (_gate)
        {
            if (_state == next) return;
            previous = _state;

            if (previous == ActivityState.KeepAwake)
                _completedKeepAwake += TimeSpan.FromMilliseconds(nowTick - _keepAwakeEnteredTick);

            if (next == ActivityState.KeepAwake)
            {
                _keepAwakeEnteredTick = nowTick;
                _keepAwakeSessions++;
            }

            _state = next;
        }

        StateChanged?.Invoke(previous, next);
    }
}
