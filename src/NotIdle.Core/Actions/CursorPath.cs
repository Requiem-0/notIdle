namespace NotIdle.Core.Actions;

/// <summary>Shape the pointer traces when path movement runs.</summary>
public enum PathPattern
{
    /// <summary>A ring around the origin.</summary>
    Circle,

    /// <summary>Four corners of a box around the origin.</summary>
    Square,

    /// <summary>A lemniscate — wider than it is tall, and never retraces itself.</summary>
    FigureEight,

    /// <summary>A handful of unpredictable hops inside the radius. Different every run.</summary>
    RandomWalk,

    /// <summary>
    /// Wild hops to random points anywhere on the virtual desktop, ignoring radius
    /// and origin. Different every run.
    /// </summary>
    FullScreen,

    /// <summary>Subtle, natural human-like micro-wander around the cursor with organic drift and return.</summary>
    OrganicJiggle,

    /// <summary>The absolute screen coordinates listed in <see cref="Settings.PathCustomPoints"/>.</summary>
    Custom,
}

/// <summary>Where a generated pattern is centred.</summary>
public enum PathOrigin
{
    /// <summary>Around wherever the pointer is parked, so the path stays local.</summary>
    Cursor,

    /// <summary>Around the middle of the virtual desktop, so it is the same every run.</summary>
    ScreenCenter,
}

/// <summary>
/// Turns a path configuration into a list of waypoints. Pure and deterministic
/// apart from <see cref="PathPattern.RandomWalk"/> and
/// <see cref="PathPattern.FullScreen"/>, so the geometry is unit
/// testable without a desktop.
///
/// <para>Waypoints are corners, not frames: <see cref="PathPlayer"/> interpolates
/// between them at the configured speed. A circle therefore needs enough points to
/// look round, while a square needs only five.</para>
/// </summary>
public static class CursorPath
{
    /// <summary>Points used to approximate a full circle. 24 reads as smooth at any sane radius.</summary>
    private const int CircleSteps = 24;

    /// <summary>Points used for one full figure-eight sweep.</summary>
    private const int FigureEightSteps = 32;

    /// <summary>Hops in a random walk. Enough to look aimless, few enough to finish quickly.</summary>
    private const int RandomWalkSteps = 8;

    /// <summary>Hops in a full-screen run. Plenty of chaos without running forever.</summary>
    private const int FullScreenSteps = 20;

    /// <summary>Subtle drift steps in an organic jiggle.</summary>
    private const int OrganicJiggleSteps = 12;

    /// <summary>A path longer than this is refused, so a pasted coordinate dump can't run for hours.</summary>
    public const int MaxWaypoints = 256;

    /// <summary>
    /// Build the waypoints for one run, already clamped to the desktop.
    /// </summary>
    /// <param name="settings">Supplies the pattern, radius and origin.</param>
    /// <param name="cursor">Current pointer position, used by <see cref="PathOrigin.Cursor"/>.</param>
    /// <param name="screen">Virtual desktop bounds, used for clamping and centring.</param>
    /// <returns>
    /// Two or more points, or an empty list when the configuration describes no
    /// movement (an empty custom list, say). Callers must treat empty as "skip".
    /// </returns>
    public static IReadOnlyList<CursorPoint> Build(Settings settings, CursorPoint cursor, ScreenBounds screen)
    {
        if (settings.PathPattern == PathPattern.Custom)
        {
            IReadOnlyList<CursorPoint> custom = ParsePoints(settings.PathCustomPoints);
            return custom.Count < 2 ? [] : Clamp(custom, screen);
        }

        CursorPoint origin = settings.PathOrigin == PathOrigin.ScreenCenter ? screen.Center : cursor;
        int radius = Math.Clamp(settings.PathRadius, Settings.MinPathRadius, Settings.MaxPathRadius);

        IReadOnlyList<CursorPoint> points = settings.PathPattern switch
        {
            PathPattern.Circle => Circle(origin, radius),
            PathPattern.Square => Square(origin, radius),
            PathPattern.FigureEight => FigureEight(origin, radius),
            PathPattern.RandomWalk => RandomWalk(origin, radius),
            PathPattern.FullScreen => FullScreen(cursor, screen),
            PathPattern.OrganicJiggle => OrganicJiggle(origin, radius),
            _ => [],
        };

        return Clamp(points, screen);
    }

    /// <summary>
    /// Parse a free-form coordinate list. Pairs are <c>x,y</c>; anything may
    /// separate them — spaces, newlines, semicolons, or an <c>-&gt;</c> arrow —
    /// because this is typed by hand and being fussy about it helps nobody.
    /// Malformed pairs are skipped rather than throwing.
    /// </summary>
    public static IReadOnlyList<CursorPoint> ParsePoints(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var points = new List<CursorPoint>();
        string normalized = text.Replace("->", " ").Replace("→", " ");

        foreach (string token in normalized.Split(
            [' ', '\t', '\r', '\n', ';', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            int comma = token.IndexOf(',');
            if (comma <= 0 || comma == token.Length - 1) continue;

            if (int.TryParse(token.AsSpan(0, comma), out int x) &&
                int.TryParse(token.AsSpan(comma + 1), out int y))
            {
                points.Add(new CursorPoint(x, y));
                if (points.Count == MaxWaypoints) break;
            }
        }

        return points;
    }

    /// <summary>Total pointer travel, in pixels — what the UI uses to predict a run's duration.</summary>
    public static double TotalLength(IReadOnlyList<CursorPoint> points)
    {
        double length = 0;
        for (int i = 1; i < points.Count; i++) length += Distance(points[i - 1], points[i]);
        return length;
    }

    public static double Distance(CursorPoint a, CursorPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    // ── Patterns ─────────────────────────────────────────────────────────────
    // Each closes back on its first point so a run leaves the pointer where it
    // started even when "restore cursor" is off.

    private static CursorPoint[] Circle(CursorPoint o, int r)
    {
        var points = new CursorPoint[CircleSteps + 1];
        for (int i = 0; i <= CircleSteps; i++)
        {
            double angle = 2 * Math.PI * i / CircleSteps;
            points[i] = new CursorPoint(
                o.X + (int)Math.Round(r * Math.Cos(angle)),
                o.Y + (int)Math.Round(r * Math.Sin(angle)));
        }
        return points;
    }

    private static CursorPoint[] Square(CursorPoint o, int r) =>
    [
        new(o.X - r, o.Y - r),
        new(o.X + r, o.Y - r),
        new(o.X + r, o.Y + r),
        new(o.X - r, o.Y + r),
        new(o.X - r, o.Y - r),
    ];

    // Lemniscate of Gerono: x = r·cos t, y = (r/2)·sin 2t. Crosses itself once,
    // which is what makes it read as a figure eight rather than an oval.
    private static CursorPoint[] FigureEight(CursorPoint o, int r)
    {
        var points = new CursorPoint[FigureEightSteps + 1];
        for (int i = 0; i <= FigureEightSteps; i++)
        {
            double t = 2 * Math.PI * i / FigureEightSteps;
            points[i] = new CursorPoint(
                o.X + (int)Math.Round(r * Math.Cos(t)),
                o.Y + (int)Math.Round(r / 2.0 * Math.Sin(2 * t)));
        }
        return points;
    }

    private static CursorPoint[] RandomWalk(CursorPoint o, int r)
    {
        var points = new CursorPoint[RandomWalkSteps + 2];
        points[0] = o;
        for (int i = 1; i <= RandomWalkSteps; i++)
        {
            // Uniform over the disc: sqrt on the radius, or the hops crowd the centre.
            double angle = Random.Shared.NextDouble() * 2 * Math.PI;
            double distance = r * Math.Sqrt(Random.Shared.NextDouble());
            points[i] = new CursorPoint(
                o.X + (int)Math.Round(distance * Math.Cos(angle)),
                o.Y + (int)Math.Round(distance * Math.Sin(angle)));
        }
        points[^1] = o;
        return points;
    }

    // Starts and ends at the pointer, like every other pattern, but the hops in
    // between land anywhere on any monitor.
    private static CursorPoint[] FullScreen(CursorPoint start, ScreenBounds screen)
    {
        var points = new CursorPoint[FullScreenSteps + 2];
        points[0] = start;
        for (int i = 1; i <= FullScreenSteps; i++)
        {
            points[i] = new CursorPoint(
                Random.Shared.Next(screen.Left, Math.Max(screen.Left + 1, screen.Right)),
                Random.Shared.Next(screen.Top, Math.Max(screen.Top + 1, screen.Bottom)));
        }
        points[^1] = start;
        return points;
    }

    private static CursorPoint[] OrganicJiggle(CursorPoint o, int r)
    {
        int maxDrift = Math.Clamp(r / 4, 6, 24);
        var points = new CursorPoint[OrganicJiggleSteps + 2];
        points[0] = o;

        double currentX = o.X;
        double currentY = o.Y;

        for (int i = 1; i <= OrganicJiggleSteps; i++)
        {
            double angle = Random.Shared.NextDouble() * 2 * Math.PI;
            double step = 2.0 + (Random.Shared.NextDouble() * (maxDrift / 3.0));
            currentX += step * Math.Cos(angle);
            currentY += step * Math.Sin(angle);

            double distFromOrigin = Math.Sqrt(Math.Pow(currentX - o.X, 2) + Math.Pow(currentY - o.Y, 2));
            if (distFromOrigin > maxDrift)
            {
                currentX = o.X + ((currentX - o.X) / distFromOrigin * maxDrift);
                currentY = o.Y + ((currentY - o.Y) / distFromOrigin * maxDrift);
            }

            points[i] = new CursorPoint((int)Math.Round(currentX), (int)Math.Round(currentY));
        }

        points[^1] = o;
        return points;
    }

    private static IReadOnlyList<CursorPoint> Clamp(IReadOnlyList<CursorPoint> points, ScreenBounds screen)
    {
        if (points.Count == 0) return points;

        var clamped = new CursorPoint[points.Count];
        for (int i = 0; i < points.Count; i++) clamped[i] = screen.Clamp(points[i]);
        return clamped;
    }
}
