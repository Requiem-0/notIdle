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
/// <para><b>Click points.</b> With <see cref="Settings.AutoClickPoints"/> set, the
/// clicker cycles through those screen positions: it travels to the next one at
/// <see cref="Settings.AutoClickTravelSpeed"/>, clicks, waits the interval, then
/// moves on. With no points configured it never moves the pointer at all and simply
/// clicks wherever it rests.</para>
///
/// <para><b>Guards.</b> The pointer being somewhere we did not put it is proof that
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

    /// <summary>Ceiling on one hop between click points, however far apart they are.</summary>
    private const int MaxTravelMs = 30_000;

    private readonly ISyntheticInputSink _sink;
    private readonly PointerTravel _travel;
    private readonly Func<bool> _pathIsRunning;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    private bool _active;
    private bool _disposed;
    private bool _busy;
    private long _clickCount;
    private CursorPoint _baseline;

    // Snapshot of the settings this activation is running under, so a save
    // mid-burst cannot tear the values apart.
    private MouseButton _button = MouseButton.Left;
    private bool _doubleClick;
    private int _intervalMs = 1000;
    private bool _jitter;
    private int _travelSpeed = 900;
    private CursorPoint[] _points = [];
    private int _nextPoint;

    /// <summary>
    /// Raised for every synthetic event this clicker emits — travel frames as well
    /// as clicks — with the tick and the resulting pointer position, so the activity
    /// monitor can credit the input to NotIdle. Fires on a timer thread.
    /// </summary>
    public event Action<long, CursorPoint>? SyntheticInput;

    /// <summary>
    /// Raised when the clicker stopped itself because the pointer moved under it.
    /// Fires on a timer thread.
    /// </summary>
    public event Action? UserTookOver;

    public AutoClicker(ISyntheticInputSink sink, ISystemInputSource input, Func<bool> pathIsRunning)
    {
        _sink = sink;
        _travel = new PointerTravel(sink, input);
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

    /// <summary>How many click points are configured; 0 means "click in place".</summary>
    public int PointCount
    {
        get { lock (_gate) return _points.Length; }
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
            if (!_busy) _timer.Change(NextDelayMs(), Timeout.Infinite);
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

    /// <summary>
    /// Emit one click immediately, for the UI's test action, ignoring the schedule.
    /// </summary>
    /// <remarks>
    /// With click points configured this performs the <em>real</em> gesture — travel
    /// to the next point, then click — because a test that quietly did something
    /// simpler than the thing being tested would be worse than no test at all. The
    /// travel runs on a background thread so the window stays responsive while the
    /// pointer moves, and it obeys the same interference check, so grabbing the mouse
    /// still cancels it.
    /// </remarks>
    public void ClickNow(Settings settings)
    {
        MouseButton button;
        bool doubleClick;
        int travelSpeed;
        CursorPoint from;
        CursorPoint? target;

        lock (_gate)
        {
            if (_disposed || _busy) return;
            ApplySettings(settings);
            button = _button;
            doubleClick = _doubleClick;
            travelSpeed = _travelSpeed;
            from = SafeCursor();
            _baseline = from;
            target = _points.Length == 0 ? null : _points[_nextPoint % _points.Length];

            if (target is null)
            {
                // Nowhere to travel: do it inline so a plain test click stays instant.
                _sink.Click(button, doubleClick);
                _clickCount++;
                _baseline = SafeCursor();
                CursorPoint at = _baseline;
                SyntheticInput?.Invoke(Environment.TickCount64, at);
                return;
            }

            _busy = true;
        }

        _ = Task.Run(() => TravelAndClick(from, target.Value, travelSpeed, button, doubleClick));
    }

    // The test action's slow path: walk to the point, then click, off the UI thread.
    private void TravelAndClick(CursorPoint from, CursorPoint target, int travelSpeed, MouseButton button, bool doubleClick)
    {
        CursorPoint at = from;
        bool clicked = false;
        try
        {
            TravelResult trip = _travel.Segment(
                from, target, travelSpeed, RaiseSyntheticInput, () => _disposed,
                Environment.TickCount64 + MaxTravelMs);

            at = trip.Position;
            if (trip.Completed)
            {
                _sink.Click(button, doubleClick);
                at = SafeCursor();
                clicked = true;
            }
        }
        catch
        {
            // A refused injection is not worth surfacing from a test button.
        }

        lock (_gate)
        {
            _busy = false;
            if (_disposed) return;
            if (clicked)
            {
                _clickCount++;
                if (_points.Length > 0) _nextPoint = (_nextPoint + 1) % _points.Length;
            }
            _baseline = at;
        }

        if (clicked) SyntheticInput?.Invoke(Environment.TickCount64, at);
    }

    // Caller must hold _gate. Returns true when anything the timer depends on changed.
    private bool ApplySettings(Settings settings)
    {
        MouseButton button = settings.AutoClickButton;
        bool doubleClick = settings.AutoClickDoubleClick;
        int interval = Math.Clamp(settings.AutoClickIntervalMs, Settings.MinClickIntervalMs, Settings.MaxClickIntervalMs);
        bool jitter = settings.AdaptiveMode;
        int travelSpeed = Math.Clamp(settings.AutoClickTravelSpeed, Settings.MinPathSpeed, Settings.MaxPathSpeed);

        CursorPoint[] points = ParsePoints(settings);
        bool pointsChanged = !points.AsSpan().SequenceEqual(_points);

        bool changed = button != _button || doubleClick != _doubleClick ||
                       interval != _intervalMs || jitter != _jitter ||
                       travelSpeed != _travelSpeed || pointsChanged;

        _button = button;
        _doubleClick = doubleClick;
        _intervalMs = interval;
        _jitter = jitter;
        _travelSpeed = travelSpeed;

        if (pointsChanged)
        {
            _points = points;
            _nextPoint = 0; // a new list always starts from its first point
        }

        return changed;
    }

    private CursorPoint[] ParsePoints(Settings settings)
    {
        IReadOnlyList<CursorPoint> parsed = CursorPath.ParsePoints(settings.AutoClickPoints);
        if (parsed.Count == 0) return [];

        ScreenBounds screen = _travel.Screen;
        var clamped = new CursorPoint[parsed.Count];
        for (int i = 0; i < parsed.Count; i++) clamped[i] = screen.Clamp(parsed[i]);
        return clamped;
    }

    // Three phases: decide under the lock, act outside it, record under it again.
    // Travelling to a click point takes hundreds of milliseconds, and holding the
    // gate that long would stall the controller poll that arms and disarms us.
    private void OnTick(object? _)
    {
        MouseButton button;
        bool doubleClick;
        int travelSpeed;
        CursorPoint from;
        CursorPoint? target;

        lock (_gate)
        {
            if (_disposed || !_active || _busy) return;

            // Stand down for the path player instead of clicking mid-stroke; the
            // baseline is stale while it moves, so re-adopt it on the way past.
            if (_pathIsRunning())
            {
                _baseline = SafeCursor();
                _timer.Change(NextDelayMs(), Timeout.Infinite);
                return;
            }

            if (HasMoved(SafeCursor()))
            {
                // We only move the pointer to our own click points, and we update the
                // baseline when we do, so this can only be someone else.
                _active = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                UserTookOver?.Invoke();
                return;
            }

            button = _button;
            doubleClick = _doubleClick;
            travelSpeed = _travelSpeed;
            from = _baseline;
            target = _points.Length == 0 ? null : _points[_nextPoint % _points.Length];
            _busy = true;
        }

        bool tookOver = false;
        bool stopped = false;
        CursorPoint at = from;

        try
        {
            if (target is not null)
            {
                TravelResult trip = _travel.Segment(
                    from, target.Value, travelSpeed, RaiseSyntheticInput, () => !IsActive,
                    Environment.TickCount64 + MaxTravelMs);

                at = trip.Position;

                // Only a hand on the mouse disarms us and counts as an interruption.
                // Being stood down by the controller, or running out of travel time,
                // just means this click does not happen.
                tookOver = trip.Interfered;
                stopped = !trip.Completed;
            }

            if (!stopped)
            {
                _sink.Click(button, doubleClick);
                at = SafeCursor();
            }
        }
        catch
        {
            // A refused injection is not fatal; try again next interval.
        }

        bool clicked = false;
        long tick = Environment.TickCount64;

        lock (_gate)
        {
            _busy = false;
            if (_disposed) return;

            if (tookOver)
            {
                _active = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            else if (stopped)
            {
                // Stood down or out of time: keep the baseline honest and let
                // whoever disarmed us decide whether we run again.
                _baseline = at;
                if (_active) _timer.Change(NextDelayMs(), Timeout.Infinite);
            }
            else
            {
                _clickCount++;
                _baseline = at;
                if (_points.Length > 0) _nextPoint = (_nextPoint + 1) % _points.Length;
                clicked = true;
                if (_active) _timer.Change(NextDelayMs(), Timeout.Infinite);
            }
        }

        // Raised outside the lock: subscribers take their own locks.
        if (tookOver) UserTookOver?.Invoke();
        else if (clicked) SyntheticInput?.Invoke(tick, at);
    }

    private void RaiseSyntheticInput(long tick, CursorPoint cursor)
    {
        // Keep our own baseline in step as we travel, so the guard on the next frame
        // is comparing against where we actually are.
        lock (_gate) _baseline = cursor;
        SyntheticInput?.Invoke(tick, cursor);
    }

    // Caller must hold _gate.
    private bool HasMoved(CursorPoint now) =>
        Math.Abs(now.X - _baseline.X) > MovementTolerancePixels ||
        Math.Abs(now.Y - _baseline.Y) > MovementTolerancePixels;

    // Caller must hold _gate.
    private int NextDelayMs()
    {
        if (!_jitter) return _intervalMs;

        int spread = (int)(_intervalMs * AdaptiveJitterFraction);
        return spread <= 0 ? _intervalMs : _intervalMs - spread + Random.Shared.Next(0, (2 * spread) + 1);
    }

    private CursorPoint SafeCursor() => _travel.Current;

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
