using NotIdle.Core;
using NotIdle.Core.Actions;
using Xunit;

namespace NotIdle.Core.Tests;

public class CursorPathTests
{
    private static readonly ScreenBounds Screen = new(0, 0, 1920, 1080);
    private static readonly CursorPoint Middle = new(960, 540);

    [Theory]
    [InlineData("100,200 300,400", 2)]
    [InlineData("100,200\n300,400\n500,600", 3)]
    [InlineData("100,200 -> 300,400", 2)]
    [InlineData("100,200; 300,400 | 500,600", 3)]
    public void ParsesPointsSeparatedByWhateverThePersonTyped(string text, int expected)
    {
        Assert.Equal(expected, CursorPath.ParsePoints(text).Count);
    }

    [Fact]
    public void ParsesNegativeCoordinatesForMonitorsLeftOfThePrimary()
    {
        IReadOnlyList<CursorPoint> points = CursorPath.ParsePoints("-800,300 100,100");

        Assert.Equal(new CursorPoint(-800, 300), points[0]);
        Assert.Equal(new CursorPoint(100, 100), points[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("100")]
    [InlineData("100,")]
    [InlineData("a,b")]
    public void SkipsMalformedInputInsteadOfThrowing(string text)
    {
        Assert.Empty(CursorPath.ParsePoints(text));
    }

    [Fact]
    public void KeepsTheGoodPairsFromAPartlyMalformedList()
    {
        IReadOnlyList<CursorPoint> points = CursorPath.ParsePoints("100,200 nonsense 300,400");

        Assert.Equal(2, points.Count);
    }

    [Fact]
    public void RefusesToBuildAnUnboundedPath()
    {
        string many = string.Join(' ', Enumerable.Range(0, CursorPath.MaxWaypoints * 2).Select(i => $"{i},{i}"));

        Assert.Equal(CursorPath.MaxWaypoints, CursorPath.ParsePoints(many).Count);
    }

    [Theory]
    [InlineData(PathPattern.Circle)]
    [InlineData(PathPattern.Square)]
    [InlineData(PathPattern.FigureEight)]
    [InlineData(PathPattern.RandomWalk)]
    [InlineData(PathPattern.FullScreen)]
    [InlineData(PathPattern.OrganicJiggle)]
    public void GeneratedShapesReturnToWhereTheyStarted(PathPattern pattern)
    {
        var settings = new Settings { PathPattern = pattern, PathRadius = 100, PathOrigin = PathOrigin.Cursor };

        IReadOnlyList<CursorPoint> points = CursorPath.Build(settings, Middle, Screen);

        Assert.True(points.Count >= 2);
        Assert.Equal(points[0], points[^1]);
    }

    [Fact]
    public void FullScreenRoamsTheWholeDesktopRegardlessOfRadius()
    {
        var settings = new Settings { PathPattern = PathPattern.FullScreen, PathRadius = Settings.MinPathRadius };

        IReadOnlyList<CursorPoint> points = CursorPath.Build(settings, Middle, Screen);

        Assert.All(points, p => Assert.Equal(p, Screen.Clamp(p)));
        int spanX = points.Max(p => p.X) - points.Min(p => p.X);
        int spanY = points.Max(p => p.Y) - points.Min(p => p.Y);
        Assert.True(spanX > Screen.Width / 2, $"x span {spanX}");
        Assert.True(spanY > Screen.Height / 2, $"y span {spanY}");
    }

    [Fact]
    public void CircleStaysOnItsRadius()
    {
        var settings = new Settings { PathPattern = PathPattern.Circle, PathRadius = 200 };

        IReadOnlyList<CursorPoint> points = CursorPath.Build(settings, Middle, Screen);

        Assert.All(points, p => Assert.InRange(CursorPath.Distance(Middle, p), 199, 201));
    }

    [Fact]
    public void ScreenCenterOriginIgnoresWhereTheCursorHappensToBe()
    {
        var settings = new Settings
        {
            PathPattern = PathPattern.Square,
            PathRadius = 50,
            PathOrigin = PathOrigin.ScreenCenter,
        };

        IReadOnlyList<CursorPoint> fromCorner = CursorPath.Build(settings, new CursorPoint(3, 7), Screen);
        IReadOnlyList<CursorPoint> fromMiddle = CursorPath.Build(settings, Middle, Screen);

        Assert.Equal(fromMiddle, fromCorner);
    }

    [Fact]
    public void WaypointsAreClampedToTheDesktop()
    {
        var settings = new Settings { PathPattern = PathPattern.Circle, PathRadius = 2000 };

        IReadOnlyList<CursorPoint> points = CursorPath.Build(settings, new CursorPoint(10, 10), Screen);

        Assert.All(points, p =>
        {
            Assert.InRange(p.X, Screen.Left, Screen.Right - 1);
            Assert.InRange(p.Y, Screen.Top, Screen.Bottom - 1);
        });
    }

    [Fact]
    public void CustomPatternUsesTheTypedCoordinatesVerbatim()
    {
        var settings = new Settings
        {
            PathPattern = PathPattern.Custom,
            PathCustomPoints = "100,100 800,400 400,900",
        };

        IReadOnlyList<CursorPoint> points = CursorPath.Build(settings, Middle, Screen);

        Assert.Equal([new(100, 100), new(800, 400), new(400, 900)], points);
    }

    [Fact]
    public void CustomPatternWithNothingUsableBuildsNoPath()
    {
        var settings = new Settings { PathPattern = PathPattern.Custom, PathCustomPoints = "100,100" };

        Assert.Empty(CursorPath.Build(settings, Middle, Screen));
    }

    [Fact]
    public void NormalizeDisablesAPathThatCouldNeverRun()
    {
        var settings = new Settings
        {
            PathEnabled = true,
            PathPattern = PathPattern.Custom,
            PathCustomPoints = "not coordinates",
        };

        settings.Normalize();

        Assert.False(settings.PathEnabled);
    }

    [Fact]
    public void NormalizeLeavesAUsableCustomPathAlone()
    {
        var settings = new Settings
        {
            PathEnabled = true,
            PathPattern = PathPattern.Custom,
            PathCustomPoints = "10,10 20,20",
        };

        settings.Normalize();

        Assert.True(settings.PathEnabled);
    }
}
