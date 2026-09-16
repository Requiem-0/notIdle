using System.Threading;

namespace NotIdle.Core.Actions;

/// <summary>
/// Walks the pointer along a <see cref="CursorPath"/> on a background thread, so a
/// run that lasts several seconds never stalls the controller's one-second poll.
///
/// <para><b>The user always wins.</b> <see cref="PointerTravel"/> checks before every
/// frame that the pointer is still where it last put it. If it isn't, someone has
/// their hand on the mouse, and the run aborts mid-path rather than fighting them
/// for control. That check is what makes a multi-second animated path safe to leave
/// switched on.</para>
///
/// <para>Only one run happens at a time — <see cref="TryStart"/> returns false while
/// one is in flight — but a run is fully concurrent with cursor pulses and the
/// auto-clicker, which is the point of giving each action its own schedule.</para>
/// </summary>
public sealed class PathPlayer : IDisposable
{
    /// <summary>
    /// Hard ceiling on a single run, whatever the geometry and speed say. A
    /// hand-typed path of 256 far-flung points at 40 px/s would otherwise hold the
    /// pointer hostage for the better part of an hour.
    /// </summary>
    private const int MaxRunDurationMs = 60_000;

    private readonly PointerTravel _travel;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _disposed;
    private long _runCount;
    private long _abortedRuns;

    public PathPlayer(ISyntheticInputSink sink, ISystemInputSource input)
    {
        _travel = new PointerTravel(sink, input);
    }

    /// <summary>True while a path is being traced.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>Completed and aborted runs since start.</summary>
    public long RunCount
    {
        get { lock (_gate) return _runCount; }
    }

    /// <summary>Runs cut short because the user took the mouse back.</summary>
    public long AbortedRuns
    {
        get { lock (_gate) return _abortedRuns; }
    }

    /// <summary>
    /// Begin one run, if none is in flight and the configuration describes a real path.
    /// </summary>
    /// <param name="settings">Pattern, radius, speed and restore preference.</param>
    /// <param name="onStep">
    /// Called on the background thread after every emitted move, with the tick and the
    /// resulting pointer position. The controller uses it to credit the movement to
    /// NotIdle — without it, the app's own path would read as genuine user activity
    /// and immediately switch keep-awake back off.
    /// </param>
    /// <returns>False if a run is already in flight or the path is empty.</returns>
    public bool TryStart(Settings settings, Action<long, CursorPoint> onStep)
    {
        CancellationToken token;
        IReadOnlyList<CursorPoint> waypoints;
        CursorPoint cursor;
        int speed;
        bool restore;

        lock (_gate)
        {
            if (_disposed || _running) return false;

            cursor = _travel.Current;
            waypoints = CursorPath.Build(settings, cursor, _travel.Screen);
            if (waypoints.Count < 2) return false;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            _running = true;
            _runCount++;

            // Snapshot what the run needs; settings may be replaced under us.
            speed = Math.Clamp(settings.PathSpeedPixelsPerSecond, Settings.MinPathSpeed, Settings.MaxPathSpeed);
            restore = settings.PathRestoreCursor;
        }

        _ = Task.Run(() => Walk(waypoints, cursor, speed, restore, onStep, token), CancellationToken.None);
        return true;
    }

    /// <summary>Cut a run short. Safe to call when nothing is running.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _cts?.Cancel();
        }
    }

    // Runs on a pool thread. Never throws out: a failed run must not take the app
    // down, and the finally block is what lets the next one start.
    private void Walk(
        IReadOnlyList<CursorPoint> waypoints,
        CursorPoint startedAt,
        int pixelsPerSecond,
        bool restoreCursor,
        Action<long, CursorPoint> onStep,
        CancellationToken token)
    {
        bool aborted = false;
        try
        {
            long deadline = Environment.TickCount64 + MaxRunDurationMs;
            bool Cancelled() => token.IsCancellationRequested;

            // The first waypoint is where the path begins, so travel there from
            // wherever the pointer actually is before tracing the shape.
            CursorPoint placed = startedAt;

            for (int i = 0; i < waypoints.Count; i++)
            {
                TravelResult leg = _travel.Segment(
                    placed, waypoints[i], pixelsPerSecond, onStep, Cancelled, deadline);

                if (!leg.Completed)
                {
                    // Cancellation and the deadline are orderly stops; only a hand on
                    // the mouse counts as the run being interrupted.
                    aborted = leg.Interfered;
                    return;
                }

                placed = leg.Position;
            }

            if (restoreCursor && !token.IsCancellationRequested)
            {
                _ = _travel.Segment(placed, startedAt, pixelsPerSecond, onStep, Cancelled, deadline);
            }
        }
        catch
        {
            // A syscall failure mid-path is not worth crashing over; the next
            // scheduled run will try again.
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                if (aborted) _abortedRuns++;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
