using System.Threading;

namespace NotIdle.Core.Actions;

/// <summary>Why a <see cref="PointerTravel.Segment"/> call stopped.</summary>
public enum TravelOutcome
{
    /// <summary>Reached the target.</summary>
    Completed,

    /// <summary>The pointer was somewhere we did not put it — a real hand on the mouse.</summary>
    Interfered,

    /// <summary>The caller stood the run down. An orderly stop, not the user.</summary>
    Cancelled,

    /// <summary>The run exceeded its deadline. Also orderly.</summary>
    TimedOut,
}

/// <summary>Where a travel ended, and why it stopped there.</summary>
/// <param name="Position">The last position the pointer was actually placed at.</param>
/// <param name="Outcome">Why the run ended.</param>
public readonly record struct TravelResult(CursorPoint Position, TravelOutcome Outcome)
{
    public bool Completed => Outcome == TravelOutcome.Completed;

    /// <summary>
    /// True only for a real hand on the mouse. Distinguishing this from the orderly
    /// stops is what keeps a deliberate stand-down out of the "the user took over"
    /// statistics — and out of the behaviour that follows from it.
    /// </summary>
    public bool Interfered => Outcome == TravelOutcome.Interfered;
}

/// <summary>
/// Walks the pointer from one screen position to another at a configured speed,
/// in small interpolated steps, and gives up the moment the user touches the mouse.
///
/// <para>Shared by <see cref="PathPlayer"/>, which strings segments together into a
/// shape, and <see cref="AutoClicker"/>, which travels to each click point. Both
/// need identical "the user always wins" behaviour, and that rule is too important
/// to be implemented twice.</para>
///
/// <para>Every step is reported through a callback so the caller can credit the
/// movement to NotIdle. Without that, the app's own travel reads as the user
/// returning, and keep-awake switches itself straight back off.</para>
/// </summary>
public sealed class PointerTravel
{
    /// <summary>Frame period. ~66 fps: smooth to the eye, trivial against a 1 s poll.</summary>
    public const int FrameIntervalMs = 15;

    /// <summary>
    /// How far the pointer may sit from where we left it before we conclude someone
    /// else moved it. Absolute <c>SendInput</c> coordinates are normalized to a
    /// 0–65535 grid, so landing a pixel or two off is rounding, not interference.
    /// </summary>
    public const int InterferenceTolerancePixels = 3;

    private readonly ISyntheticInputSink _sink;
    private readonly ISystemInputSource _input;

    public PointerTravel(ISyntheticInputSink sink, ISystemInputSource input)
    {
        _sink = sink;
        _input = input;
    }

    /// <summary>
    /// Move the pointer from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    /// <param name="pixelsPerSecond">Travel speed; at least one pixel per frame is emitted.</param>
    /// <param name="onStep">Called after every emitted move, on the calling thread.</param>
    /// <param name="cancelled">Polled each frame, so a caller can stand the run down.</param>
    /// <param name="deadlineTick">Absolute tick past which the run gives up.</param>
    /// <returns>
    /// Where the pointer ended up and why it stopped. Callers must distinguish
    /// <see cref="TravelOutcome.Interfered"/> — the user — from the orderly stops.
    /// </returns>
    public TravelResult Segment(
        CursorPoint from,
        CursorPoint to,
        int pixelsPerSecond,
        Action<long, CursorPoint> onStep,
        Func<bool> cancelled,
        long deadlineTick)
    {
        // Distance covered per frame. At least one pixel, or a slow setting would
        // emit frames that move nothing at all.
        double stepPixels = Math.Max(1.0, pixelsPerSecond * FrameIntervalMs / 1000.0);
        int frames = Math.Max(1, (int)Math.Ceiling(Distance(from, to) / stepPixels));

        CursorPoint placed = from;

        for (int f = 1; f <= frames; f++)
        {
            if (cancelled()) return new TravelResult(placed, TravelOutcome.Cancelled);
            if (Environment.TickCount64 > deadlineTick) return new TravelResult(placed, TravelOutcome.TimedOut);

            // The user's hand outranks us: if the pointer is not where we left it,
            // stop rather than drag it back.
            if (HasDrifted(placed)) return new TravelResult(placed, TravelOutcome.Interfered);

            double t = (double)f / frames;
            var next = new CursorPoint(
                (int)Math.Round(from.X + ((to.X - from.X) * t)),
                (int)Math.Round(from.Y + ((to.Y - from.Y) * t)));

            placed = _sink.MoveTo(next.X, next.Y);
            onStep(Environment.TickCount64, placed);

            Thread.Sleep(FrameIntervalMs);
        }

        return new TravelResult(placed, TravelOutcome.Completed);
    }

    /// <summary>True when the pointer is no longer where we last put it.</summary>
    public bool HasDrifted(CursorPoint expected)
    {
        CursorPoint actual = Current;
        return Math.Abs(actual.X - expected.X) > InterferenceTolerancePixels ||
               Math.Abs(actual.Y - expected.Y) > InterferenceTolerancePixels;
    }

    /// <summary>Current pointer position; falls back to the origin if the platform refuses.</summary>
    public CursorPoint Current
    {
        get
        {
            try { return _input.GetCursorPosition(); }
            catch { return default; }
        }
    }

    /// <summary>Virtual desktop bounds, with a sane fallback.</summary>
    public ScreenBounds Screen
    {
        get
        {
            try
            {
                ScreenBounds bounds = _sink.GetVirtualScreen();
                return bounds.Width > 0 && bounds.Height > 0 ? bounds : ScreenBounds.Fallback;
            }
            catch { return ScreenBounds.Fallback; }
        }
    }

    public static double Distance(CursorPoint a, CursorPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
