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
    private bool _sessionLocked;

    // Auto-off deadline as a TickCount64 value. 0 means "not armed".
    private long _autoOffDeadlineTick;
    private int _autoOffArmedForMinutes;

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
            ArmAutoOff(_settings, Environment.TickCount64);
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
            _autoOffDeadlineTick = 0;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        _keepAwake.ReleaseWakeLock();
        _stateMachine.Reset();
    }

    /// <summary>Flip the master toggle, persisting it through the settings manager.</summary>
    public void SetEnabled(bool enabled) => _settingsManager.Update(s => s.Enabled = enabled);

    // ── Session and power notifications ──────────────────────────────────────
    // Raised by the shell layer from SystemEvents. Thread-safe; these may arrive
    // on the dedicated SystemEvents thread.

    /// <summary>The workstation was locked, or the session was disconnected.</summary>
    public void NotifySessionLocked()
    {
        lock (_gate)
        {
            if (_disposed || _sessionLocked) return;
            _sessionLocked = true;
        }
        Evaluate();
    }

    /// <summary>
    /// The workstation was unlocked or reconnected. Unlocking requires a physical
    /// person, so it re-baselines the genuine-activity clock.
    /// </summary>
    public void NotifySessionUnlocked()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _sessionLocked = false;
        }
        _activity.MarkActiveNow();
        Evaluate();
    }

    /// <summary>
    /// The machine resumed from sleep. Without this the genuine-idle clock would
    /// report hours of inactivity even though someone just physically woke the box.
    /// </summary>
    public void NotifyResumedFromSleep()
    {
        lock (_gate) { if (_disposed) return; }
        _activity.MarkActiveNow();
        _keepAwake.ResetSchedule(Environment.TickCount64);
        Evaluate();
    }

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
        long autoOffDeadline;
        lock (_gate)
        {
            settings = _settings;
            running = _running;
            autoOffDeadline = _autoOffDeadlineTick;
        }

        long now = Environment.TickCount64;
        ForegroundWindow window = _windows.Current;

        bool autoOffArmed = running && autoOffDeadline != 0;
        TimeSpan untilAutoOff = autoOffArmed && autoOffDeadline > now
            ? TimeSpan.FromMilliseconds(autoOffDeadline - now)
            : TimeSpan.Zero;

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
            WakeLockHeld: _keepAwake.WakeLockHeld,
            AutoOffArmed: autoOffArmed,
            TimeUntilAutoOff: untilAutoOff);
    }

    private void OnSettingsChanged(Settings settings)
    {
        bool wasRunning;
        lock (_gate)
        {
            if (_disposed) return;
            wasRunning = _running;
            _settings = settings;
        }

        if (!settings.Enabled) { Stop(); return; }

        if (!wasRunning)
        {
            Start(); // arms auto-off from scratch
        }
        else if (settings.AutoOffMinutes != _autoOffArmedForMinutes)
        {
            // Only re-arm when the auto-off value itself changed, so saving an
            // unrelated setting does not silently restart the countdown.
            lock (_gate) ArmAutoOff(settings, Environment.TickCount64);
        }
    }

    // Caller must hold _gate.
    private void ArmAutoOff(Settings settings, long nowTick)
    {
        _autoOffArmedForMinutes = settings.AutoOffMinutes;
        _autoOffDeadlineTick = settings.AutoOffMinutes > 0
            ? nowTick + (settings.AutoOffMinutes * 60_000L)
            : 0;
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
    /// One full observe → decide → act cycle. The controller lock is taken only to
    /// read shared fields, so a slow syscall cannot stall <see cref="GetStatus"/>
    /// on the UI thread.
    /// </summary>
    private void Evaluate()
    {
        Settings settings;
        bool locked;
        lock (_gate)
        {
            if (_disposed || !_running) return;
            settings = _settings;
            locked = _sessionLocked;

            if (_autoOffDeadlineTick != 0 && Environment.TickCount64 >= _autoOffDeadlineTick)
            {
                _autoOffDeadlineTick = 0;
                // Route through the settings manager so the tray, the dashboard and
                // the persisted file all follow. Off-thread because that re-enters
                // OnSettingsChanged -> Stop(), which would otherwise run inside this
                // timer callback while it is still holding the gate.
                _ = Task.Run(() => _settingsManager.Update(s => s.Enabled = false));
                return;
            }
        }

        long now = Environment.TickCount64;

        // ── Observe ──────────────────────────────────────────────────────────
        // A locked session has no meaningful foreground window, and probing the
        // secure desktop just burns syscalls.
        ForegroundWindow window = locked ? ForegroundWindow.Unknown : _windows.Poll();
        ActivitySample sample = _activity.Sample(window.Handle, now);
        bool standDown = settings.PauseWhenFullScreen && window.WantsUninterrupted;

        // ── Decide ───────────────────────────────────────────────────────────
        var inputs = new StateInputs(
            Enabled: settings.Enabled,
            Locked: locked,
            StandDown: standDown,
            SinceGenuineInput: sample.TimeSinceGenuineInput,
            IdleThreshold: settings.IdleThreshold);

        ActivityState state = _stateMachine.Evaluate(inputs, now);

        // ── Act ──────────────────────────────────────────────────────────────
        if (state == ActivityState.Disabled)
        {
            _keepAwake.ReleaseWakeLock();
            _keepAwake.ResetSchedule(now);
            return;
        }

        // Every enabled state holds the power request — notably including Suspended
        // and Locked. A full-screen film is exactly when the display should stay on,
        // and a locked machine should still finish its downloads. What varies
        // between states is whether a cursor pulse is warranted.
        _keepAwake.HoldWakeLock(settings.KeepDisplayOn);

        if (state.AllowsCursorPulse())
        {
            if (_keepAwake.TryPulse(now, settings, out CursorPoint cursorAfter))
                _activity.NoteSyntheticInput(now, cursorAfter);
        }
        else
        {
            _keepAwake.ResetSchedule(now);
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
