using System.Threading;
using NotIdle.Core.Actions;

namespace NotIdle.Core;

/// <summary>What fired during one call to <see cref="KeepAwakeEngine.RunDueActions"/>.</summary>
/// <param name="Pulsed">A cursor pulse was emitted.</param>
/// <param name="PathStarted">A path run was started; it finishes asynchronously.</param>
/// <param name="CursorAfterPulse">Where the pulse left the pointer. Valid only when <paramref name="Pulsed"/>.</param>
public readonly record struct ActionsFired(bool Pulsed, bool PathStarted, CursorPoint CursorAfterPulse)
{
    public bool Any => Pulsed || PathStarted;
}

/// <summary>
/// Performs the system-level work needed to keep the session awake.
///
/// <para><b>The power request comes first.</b> <c>SetThreadExecutionState</c> is the
/// supported Windows API for "don't sleep while I'm doing this". It is held for as
/// long as keep-awake is on, costs nothing, and prevents system sleep plus
/// optionally display-off and the screensaver.</para>
///
/// <para><b>Synthetic input covers what it can't.</b> The execution-state request
/// does not touch the <em>input</em> idle clock, which is what session-level idle
/// and lock-on-idle policies read. Three actions can reset it, each with its own
/// enable flag and its own independent schedule, so any combination of them can be
/// in flight at once:</para>
/// <list type="number">
/// <item><description>
/// <b>Cursor pulse</b> — one imperceptible 1 px move, on a randomized interval.
/// The cheapest thing that works, and the default.
/// </description></item>
/// <item><description>
/// <b>Path movement</b> — the pointer traces a configured shape, on its own
/// randomized interval, animated on a background thread by <see cref="PathPlayer"/>.
/// </description></item>
/// <item><description>
/// <b>Auto-click</b> — <see cref="AutoClicker"/> on its own millisecond-granularity
/// timer, since useful click rates are far faster than the one-second poll.
/// </description></item>
/// </list>
///
/// <para>With <see cref="Settings.AdaptiveMode"/> on, <see cref="AdaptiveTuner"/>
/// chooses between the first two and picks the cadence, based on whether the OS
/// actually registered the previous action. Clicking is never chosen automatically.</para>
/// </summary>
public sealed class KeepAwakeEngine : IDisposable
{
    private readonly ISyntheticInputSink _sink;
    private readonly IExecutionStateController _power;
    private readonly PathPlayer _path;
    private readonly AutoClicker _clicker;
    private readonly AdaptiveTuner _tuner = new();
    private readonly Lock _gate = new();

    private readonly Schedule _pulseSchedule = new();
    private readonly Schedule _pathSchedule = new();

    private bool _wakeLockHeld;
    private bool _wakeLockDisplay;
    private long _pulseCount;
    private long _clickInterruptions;
    private bool _disposed;

    /// <summary>
    /// Raised for every synthetic event NotIdle emits — pulse, path frame, or click —
    /// with the tick and the resulting pointer position. The controller uses it to
    /// keep <see cref="Monitoring.UserActivityMonitor"/> from mistaking our own input
    /// for the user's. May fire on a pool or timer thread.
    /// </summary>
    public event Action<long, CursorPoint>? SyntheticInputEmitted;

    public KeepAwakeEngine(ISyntheticInputSink sink, IExecutionStateController power, ISystemInputSource input)
    {
        _sink = sink;
        _power = power;
        _path = new PathPlayer(sink, input);
        _clicker = new AutoClicker(sink, input, () => _path.IsRunning);

        _clicker.SyntheticInput += OnSyntheticInput;
        _clicker.UserTookOver += OnClickerStoppedByUser;
    }

    /// <summary>Total cursor pulses emitted since start.</summary>
    public long PulseCount
    {
        get { lock (_gate) return _pulseCount; }
    }

    /// <summary>Path runs started since start, including ones the user interrupted.</summary>
    public long PathRunCount => _path.RunCount;

    /// <summary>Path runs cut short because the user took the mouse back.</summary>
    public long PathAbortedRuns => _path.AbortedRuns;

    /// <summary>Clicks emitted since start.</summary>
    public long ClickCount => _clicker.ClickCount;

    /// <summary>Times the auto-clicker stopped itself because the pointer moved under it.</summary>
    public long ClickInterruptions
    {
        get { lock (_gate) return _clickInterruptions; }
    }

    /// <summary>True while the pointer is being walked along a path.</summary>
    public bool IsPathRunning => _path.IsRunning;

    /// <summary>True while the auto-clicker is armed.</summary>
    public bool IsAutoClickActive => _clicker.IsActive;

    /// <summary>The adaptive ladder, for the dashboard to report on.</summary>
    public AdaptiveTuner Tuner => _tuner;

    /// <summary>The interval, in seconds, currently being waited out. 0 before the first pulse.</summary>
    public int CurrentIntervalSeconds
    {
        get { lock (_gate) return _pulseSchedule.CurrentIntervalSeconds; }
    }

    /// <summary>True while the OS-level keep-awake request is held.</summary>
    public bool WakeLockHeld
    {
        get { lock (_gate) return _wakeLockHeld; }
    }

    /// <summary>Time until the next pulse is due, or <see cref="TimeSpan.Zero"/> if due now.</summary>
    public TimeSpan TimeUntilNextPulse(long nowTick)
    {
        lock (_gate) return _pulseSchedule.TimeRemaining(nowTick);
    }

    /// <summary>Time until the next path run is due, or <see cref="TimeSpan.Zero"/> if due now.</summary>
    public TimeSpan TimeUntilNextPath(long nowTick)
    {
        lock (_gate) return _pathSchedule.TimeRemaining(nowTick);
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
    /// Re-arm so the next entry into keep-awake acts immediately, and stand every
    /// action down. Called on every tick spent outside keep-awake, so no schedule,
    /// path run, or click timer survives into a period where it isn't wanted.
    /// </summary>
    public void ResetSchedule(long nowTick)
    {
        _path.Stop();
        _clicker.SetActive(false, SafeSettings());

        lock (_gate)
        {
            _pulseSchedule.Reset(nowTick);
            _pathSchedule.Reset(nowTick);
        }

        _tuner.Reset();
    }

    /// <summary>
    /// Emit a pulse if one is due, and pick the next randomized interval.
    /// </summary>
    /// <returns>True if a pulse was emitted; <paramref name="cursorAfter"/> is then valid.</returns>
    public bool TryPulse(long nowTick, Settings settings, out CursorPoint cursorAfter)
    {
        (int min, int max) = ResolvePulseInterval(settings);

        lock (_gate)
        {
            cursorAfter = default;
            if (_disposed || !_pulseSchedule.IsDue(nowTick)) return false;

            cursorAfter = _sink.Pulse(settings.RestoreCursor);
            _pulseCount++;
            _pulseSchedule.Advance(nowTick, min, max);
        }

        return true;
    }

    /// <summary>
    /// Run every action that is enabled and due. Each has its own schedule, so a
    /// single call can start a path run and emit a pulse in the same instant — that
    /// is deliberate, not a race.
    /// </summary>
    /// <remarks>
    /// The auto-clicker is not driven from here: <see cref="SetAutoClickActive"/>
    /// arms it once and its own timer takes over, because click intervals go well
    /// below the controller's one-second poll.
    /// </remarks>
    public ActionsFired RunDueActions(long nowTick, Settings settings)
    {
        if (_disposed) return default;

        AdaptivePlan plan = Resolve(settings);
        bool pulsed = false;
        bool pathStarted = false;
        CursorPoint cursorAfter = default;

        if (plan.PulseEnabled && TryPulse(nowTick, settings, out cursorAfter))
        {
            pulsed = true;
            OnSyntheticInput(nowTick, cursorAfter);
        }

        if (plan.PathEnabled)
        {
            bool due;
            lock (_gate)
            {
                due = !_disposed && _pathSchedule.IsDue(nowTick);
                // Advance before starting: a run takes seconds, and the next slot is
                // measured from when this one began, not when it happened to finish.
                if (due) _pathSchedule.Advance(nowTick, settings.PathMinIntervalSeconds, settings.PathMaxIntervalSeconds);
            }

            if (due) pathStarted = _path.TryStart(settings, OnSyntheticInput);
        }

        return new ActionsFired(pulsed, pathStarted, cursorAfter);
    }

    /// <summary>
    /// Arm or disarm the auto-clicker. Called every poll; cheap and idempotent.
    /// Never armed by the adaptive tuner — only by the user's explicit setting.
    /// </summary>
    public void SetAutoClickActive(bool active, Settings settings) =>
        _clicker.SetActive(active && settings.AutoClickEnabled && !_disposed, settings);

    /// <summary>
    /// Feed the adaptive ladder the answer to "did the last action actually register
    /// with the OS?". Ignored when the user is driving the configuration by hand.
    /// </summary>
    public void NoteActionOutcome(Settings settings, bool landed)
    {
        if (settings.AdaptiveMode) _tuner.Observe(landed);
    }

    /// <summary>
    /// Emit one pulse right now regardless of schedule, for the UI's "Test" action.
    /// It counts as a keep-awake event because that is exactly what it is.
    /// </summary>
    public CursorPoint PulseNow(long nowTick, Settings settings)
    {
        (int min, int max) = ResolvePulseInterval(settings);

        CursorPoint cursor;
        lock (_gate)
        {
            cursor = _sink.Pulse(settings.RestoreCursor);
            _pulseCount++;
            _pulseSchedule.Advance(nowTick, min, max);
        }

        OnSyntheticInput(nowTick, cursor);
        return cursor;
    }

    /// <summary>Start one path run right now, for the UI's "Test path" action.</summary>
    /// <returns>False if a run is already in flight or the path is empty.</returns>
    public bool RunPathNow(Settings settings) => !_disposed && _path.TryStart(settings, OnSyntheticInput);

    /// <summary>
    /// Emit one click right now, for the UI's "Test click" action. With click points
    /// configured this travels to the next one first, on a background thread, and
    /// reports itself through <see cref="SyntheticInputEmitted"/> when it lands.
    /// </summary>
    public void ClickNow(long nowTick, Settings settings)
    {
        if (_disposed) return;
        _clicker.ClickNow(settings);
    }

    /// <summary>Zero every counter.</summary>
    public void ResetStatistics()
    {
        lock (_gate)
        {
            _pulseCount = 0;
            _clickInterruptions = 0;
        }
        _clicker.ResetStatistics();
    }

    // ── Internals ────────────────────────────────────────────────────────────

    // Adaptive mode may add path movement and retune the pulse cadence, but it can
    // never switch off something the user explicitly asked for.
    private AdaptivePlan Resolve(Settings settings)
    {
        if (!settings.AdaptiveMode)
        {
            return new AdaptivePlan(
                settings.CursorPulseEnabled,
                settings.PathEnabled,
                settings.MinIntervalSeconds,
                settings.MaxIntervalSeconds);
        }

        AdaptivePlan plan = _tuner.PlanFor(settings);
        return plan with
        {
            PulseEnabled = settings.CursorPulseEnabled && plan.PulseEnabled,
            PathEnabled = settings.PathEnabled || plan.PathEnabled,
        };
    }

    private (int Min, int Max) ResolvePulseInterval(Settings settings)
    {
        AdaptivePlan plan = Resolve(settings);
        int min = plan.MinIntervalSeconds;
        return (min, Math.Max(min, plan.MaxIntervalSeconds));
    }

    private void OnSyntheticInput(long tick, CursorPoint cursor)
    {
        // The clicker's "did the pointer move?" guard is only sound if it knows
        // about the pointer moves we make ourselves.
        _clicker.SyncCursorBaseline(cursor);
        SyntheticInputEmitted?.Invoke(tick, cursor);
    }

    private void OnClickerStoppedByUser()
    {
        lock (_gate) _clickInterruptions++;
    }

    // ResetSchedule has to disarm the clicker before it has a Settings instance to
    // hand; disarming ignores everything but the flag, so defaults will do.
    private static Settings SafeSettings() => new();

    /// <summary>
    /// One action's randomized cadence. Two of these — pulse and path — run
    /// independently, which is what lets the actions overlap.
    /// </summary>
    private sealed class Schedule
    {
        private long _nextTick;

        /// <summary>The interval currently being waited out. 0 before the first run.</summary>
        public int CurrentIntervalSeconds { get; private set; }

        public void Reset(long nowTick)
        {
            _nextTick = nowTick;
            CurrentIntervalSeconds = 0;
        }

        public bool IsDue(long nowTick) => nowTick >= _nextTick;

        public TimeSpan TimeRemaining(long nowTick)
        {
            long remaining = _nextTick - nowTick;
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(remaining);
        }

        // Uniform over [min, max] seconds, plus sub-second jitter so successive runs
        // don't land on a whole-second grid.
        public void Advance(long nowTick, int minSeconds, int maxSeconds)
        {
            int min = Math.Max(1, minSeconds);
            int max = Math.Max(min, maxSeconds);
            CurrentIntervalSeconds = Random.Shared.Next(min, max + 1);
            _nextTick = nowTick + (CurrentIntervalSeconds * 1000L) + Random.Shared.Next(0, 1000);
        }
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

        _clicker.SyntheticInput -= OnSyntheticInput;
        _clicker.UserTookOver -= OnClickerStoppedByUser;
        _clicker.Dispose();
        _path.Dispose();
    }
}
