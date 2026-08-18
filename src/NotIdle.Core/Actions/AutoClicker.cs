using System.Threading;

namespace NotIdle.Core.Actions;

/// <summary>
/// Clicks a mouse button on its own timer, independently of the cursor pulse and
/// the path player, so all three can run at once.
///
/// <para>It has its own timer rather than riding the controller's one-second poll
/// because click intervals are useful down to tens of milliseconds, which a
/// one-second loop cannot express.</para>
///
/// <para><b>Guards.</b> The clicker never moves the pointer — it clicks wherever the
/// pointer is parked. Because of that, the pointer moving <em>at all</em> is proof
/// someone else is driving, so the clicker stops itself and reports it rather than
/// waiting up to a second for the controller's next poll to notice. It also stands
/// down while the path player is moving the pointer, so a click can never land
/// halfway through a stroke and drag something.</para>
/// </summary>
public sealed class AutoClicker : IDisposable
{
    /// <summary>
    /// Jitter applied to each interval in adaptive mode, as a fraction. A perfectly
    /// even metronome is the one thing no human hand produces.
    /// </summary>
    private const double AdaptiveJitterFraction = 0.15;

    /// <summary>
    /// Slack in the "has the pointer moved?" guard. A cursor pulse firing on the
    /// same instant as a click tick can be caught mid-flight, one pixel out from
    /// where it started and not yet restored; without this the clicker would read
    /// its own sibling action as the user and stand down. Any real movement clears
    /// it, and the controller's poll catches even a one-pixel human nudge within
    /// the second.
    /// </summary>
    private const int MovementTolerancePixels = 2;

    private readonly ISyntheticInputSink _sink;
    private readonly ISystemInputSource _input;
    private readonly Func<bool> _pathIsRunning;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    private bool _active;
    private bool _disposed;
    private long _clickCount;
    private CursorPoint _baseline;

    // Snapshot of the settings this activation is running under, so a save
    // mid-burst cannot tear the values apart.
    private MouseButton _button = MouseButton.Left;
    private bool _doubleClick;
    private int _intervalMs = 1000;
    private bool _jitter;

    /// <summary>
    /// Raised after each click with the tick and the pointer position, so the
    /// activity monitor can credit the input to NotIdle instead of the user.
    /// Fires on a timer thread.
    /// </summary>
    public event Action<long, CursorPoint>? Clicked;

    /// <summary>
    /// Raised when the clicker stopped itself because the pointer moved under it.
    /// Fires on a timer thread.
    /// </summary>
    public event Action? UserTookOver;

    public AutoClicker(ISyntheticInputSink sink, ISystemInputSource input, Func<bool> pathIsRunning)
    {
        _sink = sink;
        _input = input;
        _pathIsRunning = pathIsRunning;
        _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>True while the clicker is armed and firing.</summary>
    public bool IsActive
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>Clicks emitted since the counters were last reset.</summary>
    public long ClickCount
    {
        get { lock (_gate) return _clickCount; }
    }

    /// <summary>
    /// Arm or disarm the clicker. Called every controller poll, so it must be cheap
    /// and idempotent; re-arming with unchanged settings does not restart the timer
    /// and therefore does not reset the countdown to the next click.
    /// </summary>
    public void SetActive(bool active, Settings settings)
    {
        lock (_gate)
        {
            if (_disposed) return;

            if (!active)
            {
                if (!_active) return;
                _active = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            bool changed = ApplySettings(settings);

            if (_active && !changed) return;

            // Arming fresh adopts the pointer's current position as the baseline,
            // so merely having moved the mouse before going idle is not read as
            // interference the instant we start.
            _baseline = SafeCursor();
            _active = true;
            _timer.Change(NextDelayMs(), Timeout.Infinite);
        }
    }

    /// <summary>
    /// Tell the clicker that NotIdle itself moved the pointer, so the next tick does
    /// not mistake our own path or pulse for the user grabbing the mouse.
    /// </summary>
    public void SyncCursorBaseline(CursorPoint cursor)
    {
        lock (_gate) _baseline = cursor;
    }

    /// <summary>Zero the click counter.</summary>
    public void ResetStatistics()
    {
        lock (_gate) _clickCount = 0;
    }

    /// <summary>Emit one click immediately, for the UI's test action. Ignores the schedule.</summary>
    public CursorPoint ClickNow(Settings settings)
    {
        lock (_gate)
        {
            if (_disposed) return default;
            ApplySettings(settings);
            _sink.Click(_button, _doubleClick);
            _clickCount++;
            _baseline = SafeCursor();
            return _baseline;
        }
    }

    // Caller must hold _gate. Returns true when anything the timer depends on changed.
    private bool ApplySettings(Settings settings)
    {
        MouseButton button = settings.AutoClickButton;
        bool doubleClick = settings.AutoClickDoubleClick;
        int interval = Math.Clamp(settings.AutoClickIntervalMs, Settings.MinClickIntervalMs, Settings.MaxClickIntervalMs);
        bool jitter = settings.AdaptiveMode;

        bool changed = button != _button || doubleClick != _doubleClick ||
                       interval != _intervalMs || jitter != _jitter;

        _button = button;
        _doubleClick = doubleClick;
        _intervalMs = interval;
        _jitter = jitter;
        return changed;
    }

    private void OnTick(object? _)
    {
        bool tookOver = false;
        long tick = 0;
        CursorPoint at = default;

        lock (_gate)
        {
            if (_disposed || !_active) return;

            // Stand down for the path player instead of clicking mid-stroke; the
            // baseline is stale while it moves, so re-adopt it on the way past.
            if (_pathIsRunning())
            {
                _baseline = SafeCursor();
                _timer.Change(NextDelayMs(), Timeout.Infinite);
                return;
            }

            CursorPoint now = SafeCursor();
            if (HasMoved(now))
            {
                // We never move the pointer, so this can only be the user.
                _active = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                tookOver = true;
            }
            else
            {
                try
                {
                    _sink.Click(_button, _doubleClick);
                    _clickCount++;
                    tick = Environment.TickCount64;
                    at = now;
                }
                catch
                {
                    // A refused injection is not fatal; try again next interval.
                }
                _timer.Change(NextDelayMs(), Timeout.Infinite);
            }
        }

        // Raised outside the lock: subscribers take their own locks.
        if (tookOver) UserTookOver?.Invoke();
        else if (tick != 0) Clicked?.Invoke(tick, at);
    }

    // Caller must hold _gate.
    private int NextDelayMs()
    {
        if (!_jitter) return _intervalMs;

        int spread = (int)(_intervalMs * AdaptiveJitterFraction);
        return spread <= 0 ? _intervalMs : _intervalMs - spread + Random.Shared.Next(0, (2 * spread) + 1);
    }

    // Caller must hold _gate. We never move the pointer ourselves, so anything
    // beyond the tolerance can only be the user or another app.
    private bool HasMoved(CursorPoint now) =>
        Math.Abs(now.X - _baseline.X) > MovementTolerancePixels ||
        Math.Abs(now.Y - _baseline.Y) > MovementTolerancePixels;

    private CursorPoint SafeCursor()
    {
        try { return _input.GetCursorPosition(); }
        catch { return _baseline; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active = false;
        }
        _timer.Dispose();
    }
}
