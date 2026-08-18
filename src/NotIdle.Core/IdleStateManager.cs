using System.Threading;

namespace NotIdle.Core;

/// <summary>Everything the state machine needs to decide, gathered at one instant.</summary>
/// <param name="Enabled">Master keep-awake toggle.</param>
/// <param name="Locked">The workstation is locked.</param>
/// <param name="StandDown">Context says don't pulse (full screen / presenting / gaming).</param>
/// <param name="SinceGenuineInput">Time since real user interaction.</param>
/// <param name="IdleThreshold">Configured idle threshold.</param>
public readonly record struct StateInputs(
    bool Enabled,
    bool Locked,
    bool StandDown,
    TimeSpan SinceGenuineInput,
    TimeSpan IdleThreshold);

/// <summary>
/// The state machine: turns "how long since genuine input" plus session context
/// into Active / Idle / Keep-Awake / Suspended / Locked, and accumulates how long
/// keep-awake has been engaged.
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
    private long _holdingSinceTick;
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

    /// <summary>Total time spent holding the session awake, including the current run.</summary>
    public TimeSpan TotalKeepAwakeDuration => TotalKeepAwakeAt(Environment.TickCount64);

    /// <summary>Same as <see cref="TotalKeepAwakeDuration"/> but sampled at a caller-supplied tick.</summary>
    public TimeSpan TotalKeepAwakeAt(long nowTick)
    {
        lock (_gate)
        {
            if (!_state.IsHoldingAwake()) return _completedKeepAwake;
            return _completedKeepAwake + TimeSpan.FromMilliseconds(nowTick - _holdingSinceTick);
        }
    }

    /// <summary>Advance the machine one step.</summary>
    public ActivityState Evaluate(in StateInputs inputs, long nowTick)
    {
        ActivityState next = Classify(inputs);
        Transition(next, nowTick);
        return next;
    }

    private static ActivityState Classify(in StateInputs inputs)
    {
        if (!inputs.Enabled) return ActivityState.Disabled;

        // Lock wins over everything: with the session locked there is no meaningful
        // foreground app, and the user is away by definition.
        if (inputs.Locked) return ActivityState.Locked;
        if (inputs.StandDown) return ActivityState.Suspended;
        if (inputs.SinceGenuineInput >= inputs.IdleThreshold) return ActivityState.KeepAwake;

        TimeSpan activeWindow = inputs.IdleThreshold / 2;
        if (activeWindow > MaxActiveWindow) activeWindow = MaxActiveWindow;
        return inputs.SinceGenuineInput < activeWindow ? ActivityState.Active : ActivityState.Idle;
    }

    /// <summary>Force the machine back to <see cref="ActivityState.Disabled"/>, closing any open run.</summary>
    public void Reset() => Transition(ActivityState.Disabled, Environment.TickCount64);

    /// <summary>Zero the accumulated statistics without disturbing the current state.</summary>
    public void ResetStatistics()
    {
        lock (_gate)
        {
            _completedKeepAwake = TimeSpan.Zero;
            _keepAwakeSessions = _state.IsHoldingAwake() ? 1 : 0;
            _holdingSinceTick = Environment.TickCount64;
        }
    }

    private void Transition(ActivityState next, long nowTick)
    {
        ActivityState previous;
        lock (_gate)
        {
            if (_state == next) return;
            previous = _state;

            bool wasHolding = previous.IsHoldingAwake();
            bool isHolding = next.IsHoldingAwake();

            if (wasHolding)
                _completedKeepAwake += TimeSpan.FromMilliseconds(nowTick - _holdingSinceTick);

            if (isHolding)
            {
                _holdingSinceTick = nowTick;
                // KeepAwake -> Locked is one continuous run, not a second session.
                if (!wasHolding) _keepAwakeSessions++;
            }

            _state = next;
        }

        StateChanged?.Invoke(previous, next);
    }
}
