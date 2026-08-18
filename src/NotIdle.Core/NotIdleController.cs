using System.Threading;
using NotIdle.Core.Monitoring;

namespace NotIdle.Core;

/// <summary>
/// Coordinates the whole system. Owns one self-rescheduling timer; every tick it
/// samples the foreground window and genuine user activity, advances the state
/// machine, and lets <see cref="KeepAwakeEngine"/> act if — and only if — the
/// state says so.
///
/// This is the only class that knows the order of operations. Each collaborator
/// stays independently testable.
/// </summary>
public sealed class NotIdleController : IDisposable
{
    /// <summary>
    /// One cheap syscall batch per second. Slow enough to be invisible on a power
    /// meter, fast enough that a threshold is honoured to within a second.
    /// </summary>
    private const int PollIntervalMs = 1000;

    private readonly SettingsManager _settingsManager;
    private readonly UserActivityMonitor _activity;
    private readonly WindowMonitor _windows;
    private readonly IdleStateManager _stateMachine;
    private readonly KeepAwakeEngine _keepAwake;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    private Settings _settings;
    private bool _running;
    private bool _disposed;

    /// <summary>Raised on every state transition. May fire on a pool thread.</summary>
    public event Action<ActivityState>? StateChanged;

    public NotIdleController(
        SettingsManager settingsManager,
        UserActivityMonitor activity,
        WindowMonitor windows,
        IdleStateManager stateMachine,
        KeepAwakeEngine keepAwake)
    {
        _settingsManager = settingsManager;
        _activity = activity;
        _windows = windows;
        _stateMachine = stateMachine;
        _keepAwake = keepAwake;
        _settings = settingsManager.Current;

        _stateMachine.StateChanged += (_, next) => StateChanged?.Invoke(next);
        _settingsManager.Changed += OnSettingsChanged;

        // Created stopped; Start() arms it.
        _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public ActivityState CurrentState => _stateMachine.State;

    /// <summary>Begin monitoring, if the current settings enable it.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _running || !_settings.Enabled) return;
            _running = true;
            // Starting is itself a user action — never fire keep-awake instantly.
            _activity.MarkActiveNow();
            _keepAwake.ResetSchedule(Environment.TickCount64);
        }
        Evaluate(); // Publish a state immediately rather than after one poll.
        Arm();
    }

    /// <summary>Stop monitoring and drop the OS keep-awake request. Zero CPU afterwards.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _running = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        _keepAwake.ReleaseWakeLock();
        _stateMachine.Reset();
    }

    /// <summary>Flip the master toggle, persisting it through the settings manager.</summary>
    public void SetEnabled(bool enabled) => _settingsManager.Update(s => s.Enabled = enabled);

    /// <summary>Emit one keep-awake pulse right now, for the UI's Test action.</summary>
    public void TriggerTestAction()
    {
        Settings settings;
        lock (_gate) settings = _settings;

        long now = Environment.TickCount64;
        CursorPoint after = _keepAwake.PulseNow(now, settings);
        _activity.NoteSyntheticInput(now, after);
    }

    /// <summary>Zero the session counters shown on the dashboard.</summary>
    public void ResetStatistics()
    {
        _stateMachine.ResetStatistics();
        _keepAwake.ResetStatistics();
    }

    /// <summary>A consistent snapshot for the dashboard. Cheap enough to call once a second.</summary>
    public NotIdleStatus GetStatus()
    {
        Settings settings;
        bool running;
        lock (_gate)
        {
            settings = _settings;
            running = _running;
        }

        long now = Environment.TickCount64;
        ForegroundWindow window = _windows.Current;

        return new NotIdleStatus(
            Enabled: settings.Enabled && running,
            State: _stateMachine.State,
            TimeSinceGenuineInput: _activity.TimeSinceGenuineInput,
            TotalKeepAwakeDuration: _stateMachine.TotalKeepAwakeAt(now),
            KeepAwakeEvents: _keepAwake.PulseCount,
            KeepAwakeSessions: _stateMachine.KeepAwakeSessions,
            GenuineInputEvents: _activity.GenuineEventCount,
            IntervalDescription: settings.DescribeInterval(),
            CurrentIntervalSeconds: _keepAwake.CurrentIntervalSeconds,
            TimeUntilNextAction: _keepAwake.TimeUntilNextPulse(now),
            ForegroundApp: string.IsNullOrEmpty(window.Process) ? "—" : window.Process,
            WakeLockHeld: _keepAwake.WakeLockHeld);
    }

    private void OnSettingsChanged(Settings settings)
    {
        bool shouldRun;
        lock (_gate)
        {
            if (_disposed) return;
            _settings = settings;
            shouldRun = settings.Enabled;
        }

        if (shouldRun) Start();
        else Stop();
    }

    private void OnTick(object? _)
    {
        try
        {
            Evaluate();
        }
        catch
        {
            // A failed poll must never kill the timer — try again next tick.
        }
        finally
        {
            Arm();
        }
    }

    /// <summary>
    /// One full observe → decide → act cycle. Kept outside the controller lock's
    /// critical section for the acting part so a slow syscall can't stall
    /// <see cref="GetStatus"/> on the UI thread.
    /// </summary>
    private void Evaluate()
    {
        Settings settings;
        lock (_gate)
        {
            if (_disposed || !_running) return;
            settings = _settings;
        }

        long now = Environment.TickCount64;

        // ── Observe ──────────────────────────────────────────────────────────
        ForegroundWindow window = _windows.Poll();
        ActivitySample sample = _activity.Sample(window.Handle, now);
        bool standDown = settings.PauseWhenFullScreen && window.WantsUninterrupted;

        // ── Decide ───────────────────────────────────────────────────────────
        ActivityState state = _stateMachine.Evaluate(
            settings.Enabled, standDown, sample.TimeSinceGenuineInput, settings.IdleThreshold, now);

        // ── Act ──────────────────────────────────────────────────────────────
        switch (state)
        {
            case ActivityState.KeepAwake:
                _keepAwake.HoldWakeLock(settings.KeepDisplayOn);
                if (_keepAwake.TryPulse(now, settings, out CursorPoint cursorAfter))
                    _activity.NoteSyntheticInput(now, cursorAfter);
                break;

            case ActivityState.Active:
            case ActivityState.Idle:
                // Hold the wake lock from the moment keep-awake is switched on, not
                // only once the threshold expires — otherwise a threshold longer
                // than the machine's sleep timer would never get the chance to fire.
                // No synthetic input is emitted here: the user is at the keyboard.
                _keepAwake.HoldWakeLock(settings.KeepDisplayOn);
                _keepAwake.ResetSchedule(now);
                break;

            default: // Disabled / Suspended
                _keepAwake.ReleaseWakeLock();
                _keepAwake.ResetSchedule(now);
                break;
        }
    }

    // One-shot reschedule (period = Infinite) so ticks can never overlap.
    private void Arm()
    {
        lock (_gate)
        {
            if (_disposed || !_running) return;
            _timer.Change(PollIntervalMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
        }

        _settingsManager.Changed -= OnSettingsChanged;
        _timer.Dispose();
        _keepAwake.Dispose();
    }
}
