using System.Threading;

namespace NotIdle.Core.Actions;

/// <summary>
/// Walks the pointer along a <see cref="CursorPath"/> on a background thread, so a
/// run that lasts several seconds never stalls the controller's one-second poll.
///
/// <para><b>The user always wins.</b> Before every frame the player checks that the
/// pointer is still exactly where it last put it. If it isn't, someone has their
/// hand on the mouse, and the run aborts mid-path rather than fighting them for
/// control. That check is what makes a multi-second animated path safe to leave
/// switched on.</para>
///
/// <para>Only one run happens at a time — <see cref="TryStart"/> returns false while
/// one is in flight — but a run is fully concurrent with cursor pulses and the
/// auto-clicker, which is the point of giving each action its own schedule.</para>
/// </summary>
public sealed class PathPlayer : IDisposable
{
    /// <summary>Frame period. ~66 fps: smooth to the eye, trivial against a 1 s poll.</summary>
    private const int FrameIntervalMs = 15;

    /// <summary>
    /// How far the pointer may drift from where we left it before we conclude the
    /// user moved it. Absolute <c>SendInput</c> coordinates are normalized to a
    /// 0–65535 grid, so landing a pixel or two off is normal and not interference.
    /// </summary>
    private const int InterferenceTolerancePixels = 3;

    /// <summary>
    /// Hard ceiling on a single run, whatever the geometry and speed say. A
    /// hand-typed path of 256 far-flung points at 40 px/s would otherwise hold the
    /// pointer hostage for the better part of an hour.
    /// </summary>
    private const int MaxRunDurationMs = 60_000;

    private readonly ISyntheticInputSink _sink;
    private readonly ISystemInputSource _input;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _disposed;
    private long _runCount;
    private long _abortedRuns;

    public PathPlayer(ISyntheticInputSink sink, ISystemInputSource input)
    {
        _sink = sink;
        _input = input;
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

            cursor = SafeCursor();
            waypoints = CursorPath.Build(settings, cursor, SafeScreen());
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
            // Distance covered per frame, from the configured speed. At least one
            // pixel, or a slow setting would emit frames that move nothing.
            double stepPixels = Math.Max(1.0, pixelsPerSecond * FrameIntervalMs / 1000.0);
            long deadline = Environment.TickCount64 + MaxRunDurationMs;

            CursorPoint placed = startedAt;

            for (int i = 1; i < waypoints.Count && !token.IsCancellationRequested; i++)
            {
                CursorPoint from = waypoints[i - 1];
                CursorPoint to = waypoints[i];
                int frames = Math.Max(1, (int)Math.Ceiling(CursorPath.Distance(from, to) / stepPixels));

                for (int f = 1; f <= frames; f++)
                {
                    if (token.IsCancellationRequested || Environment.TickCount64 > deadline) return;

                    // The user's hand outranks us: if the pointer is not where we
                    // left it, stop rather than drag it back.
                    if (UserInterfered(placed)) { aborted = true; return; }

                    double t = (double)f / frames;
                    var next = new CursorPoint(
                        (int)Math.Round(from.X + ((to.X - from.X) * t)),
                        (int)Math.Round(from.Y + ((to.Y - from.Y) * t)));

                    placed = _sink.MoveTo(next.X, next.Y);
                    onStep(Environment.TickCount64, placed);

                    Thread.Sleep(FrameIntervalMs);
                }
            }

            if (restoreCursor && !token.IsCancellationRequested && !UserInterfered(placed))
            {
                CursorPoint back = _sink.MoveTo(startedAt.X, startedAt.Y);
                onStep(Environment.TickCount64, back);
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

    private bool UserInterfered(CursorPoint expected)
    {
        CursorPoint actual = SafeCursor();
        return Math.Abs(actual.X - expected.X) > InterferenceTolerancePixels ||
               Math.Abs(actual.Y - expected.Y) > InterferenceTolerancePixels;
    }

    private CursorPoint SafeCursor()
    {
        try { return _input.GetCursorPosition(); }
        catch { return default; }
    }

    private ScreenBounds SafeScreen()
    {
        try
        {
            ScreenBounds bounds = _sink.GetVirtualScreen();
            return bounds.Width > 0 && bounds.Height > 0 ? bounds : ScreenBounds.Fallback;
        }
        catch { return ScreenBounds.Fallback; }
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
