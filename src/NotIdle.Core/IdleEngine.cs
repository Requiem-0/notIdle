using System.Threading;

namespace NotIdle.Core;

/// <summary>
/// The background worker: a self-rescheduling timer that watches idle time and
/// drives the state machine. UI-agnostic and allocation-free on the hot path.
///
/// Detection deliberately uses low-frequency polling of the near-free
/// <see cref="IInputMonitor.GetIdleTime"/> syscall rather than per-input hooks —
/// that keeps the CPU parked while the user is active, which is exactly when a
/// hook-based approach would be most expensive.
/// </summary>
public sealed class IdleEngine : IDisposable
{
    // Adaptive cadence: slow while merely watching, brisk while actuating.
    private const int MonitorIntervalMs = 1000;
    private const int ActionIntervalMs = 100;

    // Below this, the user counts as "active" for display purposes. Capped at
    // half the threshold so the "Idle (counting down)" state is always reachable,
    // even for very short thresholds.
    private static readonly TimeSpan MaxActiveCutoff = TimeSpan.FromSeconds(2);

    private readonly IInputMonitor _input;
    private readonly IMouseActuator _mouse;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    private Settings _settings;
    private AppState _state = AppState.Paused;
    private bool _running;
    private long _actionEndTick; // Environment.TickCount64 deadline while acting.
    private bool _disposed;

    /// <summary>Raised whenever the observable state changes. May fire on a pool thread.</summary>
    public event Action<AppState>? StateChanged;

    public IdleEngine(IInputMonitor input, IMouseActuator mouse, Settings settings)
    {
        _input = input;
        _mouse = mouse;
        _settings = settings.Clone();
        // Created stopped; Start()/ApplySettings() arm it.
        _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public AppState CurrentState
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Begin (or resume) monitoring if enabled by the current settings.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _running || !_settings.Enabled) return;
            _running = true;
            Transition(AppState.Monitoring);
            Arm(MonitorIntervalMs);
        }
    }

    /// <summary>Stop monitoring. Idle: no timer, no CPU.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) { Transition(AppState.Paused); return; }
            _running = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Transition(AppState.Paused);
        }
    }

    /// <summary>Immediately perform several visible mouse nudges for testing.</summary>
    public void TestNudge()
    {
        for (int i = 0; i < 10; i++)
            _mouse.Nudge();
    }

    /// <summary>Swap settings atomically and (re)arm to match the new enabled flag.</summary>
    public void ApplySettings(Settings settings)
    {
        lock (_gate)
        {
            _settings = settings.Clone();
            if (!_settings.Enabled) { Stop(); return; }
            if (!_running) Start();
        }
    }

    private void OnTick(object? _)
    {
        lock (_gate)
        {
            if (!_running || _disposed) return;

            switch (_state)
            {
                case AppState.ActionRunning:
                    if (Environment.TickCount64 >= _actionEndTick)
                    {
                        Transition(AppState.Monitoring);
                        Arm(MonitorIntervalMs);
                    }
                    else
                    {
                        _mouse.Nudge();
                        Arm(ActionIntervalMs);
                    }
                    break;

                default: // Monitoring / Idle
                    TimeSpan idle = _input.GetIdleTime();
                    if (idle >= _settings.IdleThreshold)
                    {
                        _actionEndTick = Environment.TickCount64 + (long)_settings.ActionDuration.TotalMilliseconds;
                        Transition(AppState.ActionRunning);
                        _mouse.Nudge();
                        Arm(ActionIntervalMs);
                    }
                    else
                    {
                        TimeSpan cutoff = _settings.IdleThreshold / 2;
                        if (cutoff > MaxActiveCutoff) cutoff = MaxActiveCutoff;
                        Transition(idle < cutoff ? AppState.Monitoring : AppState.Idle);
                        Arm(MonitorIntervalMs);
                    }
                    break;
            }
        }
    }

    // One-shot reschedule (period = Infinite) so ticks can never overlap.
    private void Arm(int dueMs) => _timer.Change(dueMs, Timeout.Infinite);

    private void Transition(AppState next)
    {
        if (_state == next) return;
        _state = next;
        StateChanged?.Invoke(next);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
        }
        _timer.Dispose();
    }
}
