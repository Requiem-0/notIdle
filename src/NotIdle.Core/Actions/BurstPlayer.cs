using System.Threading;

namespace NotIdle.Core.Actions;

/// <summary>
/// Executes coordinated human-like activity bursts (organic micro-wander, subtle rebound scrolls,
/// and natural micro-pauses) to maintain a targeted tracking activity percentage (e.g. 75%–90%)
/// on activity monitoring software (Time Doctor, Hubstaff, Upwork, Crossover, etc.).
///
/// <para><b>The user always wins.</b> Every movement segment checks <see cref="PointerTravel"/>
/// for interference before each frame. The instant a real hand touches the mouse, the burst
/// immediately aborts and stands down.</para>
/// </summary>
public sealed class BurstPlayer : IDisposable
{
    private readonly ISyntheticInputSink _sink;
    private readonly ISystemInputSource _input;
    private readonly PointerTravel _travel;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _disposed;
    private long _burstCount;
    private long _interruptedBursts;

    public BurstPlayer(ISyntheticInputSink sink, ISystemInputSource input)
    {
        _sink = sink;
        _input = input;
        _travel = new PointerTravel(sink, input);
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public long BurstCount
    {
        get { lock (_gate) return _burstCount; }
    }

    public long InterruptedBursts
    {
        get { lock (_gate) return _interruptedBursts; }
    }

    public void ResetStatistics()
    {
        lock (_gate)
        {
            _burstCount = 0;
            _interruptedBursts = 0;
        }
    }

    /// <summary>
    /// Begin the burst loop if none is currently active.
    /// </summary>
    public bool TryStart(Settings settings, Action<long, CursorPoint> onSyntheticInput, Action onInterrupted)
    {
        lock (_gate)
        {
            if (_disposed || _running) return false;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _running = true;
        }

        CancellationToken token = _cts.Token;
        _ = Task.Run(() => RunBurstLoop(settings, onSyntheticInput, onInterrupted, token), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Cooperatively stand down the active burst loop.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();
        }
    }

    private void RunBurstLoop(
        Settings settings,
        Action<long, CursorPoint> onSyntheticInput,
        Action onInterrupted,
        CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Calculate duty cycle: e.g. 80% target = 0.80 duty
                int targetPercent = Math.Clamp(settings.TargetActivityPercent, Settings.MinTargetActivityPercent, Settings.MaxTargetActivityPercent);
                double duty = targetPercent / 100.0;

                // Active duration: 3500ms to 6500ms
                int activeMs = Random.Shared.Next(3500, 6500);
                // Pause duration required to hit target active percentage:
                // active / (active + pause) = duty => pause = active * (1 - duty) / duty
                int pauseMs = (int)(activeMs * (1.0 - duty) / duty);
                // Add natural human jitter (+/- 15%)
                int variance = Math.Max(50, (int)(pauseMs * 0.15));
                pauseMs = Math.Max(250, pauseMs + Random.Shared.Next(-variance, variance + 1));

                // ── Active Phase: Human Work Activity ────────────────────────
                long activeDeadline = Environment.TickCount64 + activeMs;
                bool handTookOver = false;

                while (Environment.TickCount64 < activeDeadline && !token.IsCancellationRequested)
                {
                    CursorPoint origin = _input.GetCursorPosition();
                    ScreenBounds screen = _sink.GetVirtualScreen();

                    // Generate gentle organic micro-loop around the cursor (8–18 px drift)
                    int driftRadius = Random.Shared.Next(16, 36);
                    IReadOnlyList<CursorPoint> waypoints = CursorPath.Build(
                        new Settings
                        {
                            PathPattern = PathPattern.OrganicJiggle,
                            PathRadius = driftRadius,
                            PathSpeedPixelsPerSecond = Random.Shared.Next(160, 240),
                        },
                        origin,
                        screen);

                    if (waypoints.Count >= 2)
                    {
                        for (int i = 1; i < waypoints.Count; i++)
                        {
                            if (token.IsCancellationRequested) break;
                            long segDeadline = Environment.TickCount64 + 1000;
                            TravelResult result = _travel.Segment(
                                waypoints[i - 1],
                                waypoints[i],
                                Random.Shared.Next(180, 260),
                                onSyntheticInput,
                                () => token.IsCancellationRequested,
                                segDeadline);

                            if (result.Interfered)
                            {
                                handTookOver = true;
                                break;
                            }
                        }
                    }

                    if (handTookOver || token.IsCancellationRequested) break;

                    // Subtle micro-scroll (Rebound) during active burst:
                    // Scrolls down 1 notch, short pause, scrolls back up 1 notch.
                    if (Random.Shared.NextDouble() < 0.65)
                    {
                        try
                        {
                            _sink.Scroll(-1);
                            onSyntheticInput(Environment.TickCount64, _input.GetCursorPosition());

                            int scrollPause = Random.Shared.Next(180, 320);
                            if (token.WaitHandle.WaitOne(scrollPause)) break;

                            _sink.Scroll(1);
                            onSyntheticInput(Environment.TickCount64, _input.GetCursorPosition());
                        }
                        catch
                        {
                            // Injected scroll failure is non-fatal
                        }
                    }

                    // Natural short reading/scanning pause between micro-movements
                    int subPause = Random.Shared.Next(200, 450);
                    if (token.WaitHandle.WaitOne(subPause)) break;
                }

                if (handTookOver)
                {
                    lock (_gate)
                    {
                        _running = false;
                        _interruptedBursts++;
                    }
                    onInterrupted();
                    return;
                }

                if (token.IsCancellationRequested) break;

                lock (_gate) _burstCount++;

                // ── Rest Phase: Natural Human Thinking Pause ─────────────────
                if (token.WaitHandle.WaitOne(pauseMs)) break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate) _running = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
