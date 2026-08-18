using NotIdle.Core;
using NotIdle.Core.Actions;
using Xunit;

namespace NotIdle.Core.Tests;

/// <summary>
/// The three actions are supposed to overlap rather than take turns, and every one
/// of them is supposed to lose to a real hand on the mouse. Both properties involve
/// real threads and real timers, so these tests wait on conditions rather than
/// assuming an ordering.
/// </summary>
public class SimultaneousActionTests
{
    private const int WaitTimeoutMs = 5000;

    private static (FakeDesktop desktop, KeepAwakeEngine engine) New()
    {
        var desktop = new FakeDesktop();
        return (desktop, new KeepAwakeEngine(desktop, desktop, desktop));
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = WaitTimeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    // A path short enough to finish inside a frame or two, for tests about
    // scheduling rather than about the animation itself.
    private static Settings Configured(int pulseSeconds, int pathSeconds) => Normalized(new Settings
    {
        AdaptiveMode = false,
        CursorPulseEnabled = true,
        MinIntervalSeconds = pulseSeconds,
        MaxIntervalSeconds = pulseSeconds,
        PathEnabled = true,
        PathPattern = PathPattern.Custom,
        PathCustomPoints = "500,500 502,500",
        PathSpeedPixelsPerSecond = 8000,
        PathMinIntervalSeconds = pathSeconds,
        PathMaxIntervalSeconds = pathSeconds,
    });

    private static Settings Normalized(Settings s)
    {
        s.Normalize();
        return s;
    }

    [Fact]
    public void PulseAndPathCanBothFireOnTheSameTick()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine) = New();
        using (engine)
        {
            Settings settings = Configured(pulseSeconds: 15, pathSeconds: 30);
            engine.ResetSchedule(desktop.Now); // both due immediately

            ActionsFired fired = engine.RunDueActions(desktop.Now, settings);

            Assert.True(fired.Pulsed, "the pulse should have fired");
            Assert.True(fired.PathStarted, "the path should have started in the same call");
        }
    }

    [Fact]
    public void EachActionKeepsItsOwnCadence()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine) = New();
        using (engine)
        {
            // Path runs a quarter as often as the pulse.
            Settings settings = Configured(pulseSeconds: 15, pathSeconds: 60);
            engine.ResetSchedule(desktop.Now);

            for (int second = 0; second < 600; second++)
            {
                desktop.Now += 1000;
                ActionsFired fired = engine.RunDueActions(desktop.Now, settings);

                // A run occupies the player for a frame or two of real time; let it
                // finish so the next due slot is not skipped for being busy.
                if (fired.PathStarted) WaitUntil(() => !engine.IsPathRunning);
            }

            Assert.InRange(engine.PulseCount, 35, 42);
            Assert.InRange(engine.PathRunCount, 8, 12);
            Assert.True(engine.PulseCount > engine.PathRunCount * 2,
                $"pulses ({engine.PulseCount}) should far outnumber path runs ({engine.PathRunCount})");
        }
    }

    [Fact]
    public void DisablingOneActionLeavesTheOtherRunning()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine) = New();
        using (engine)
        {
            Settings settings = Configured(pulseSeconds: 15, pathSeconds: 15);
            settings.CursorPulseEnabled = false;
            engine.ResetSchedule(desktop.Now);

            ActionsFired fired = engine.RunDueActions(desktop.Now, settings);

            Assert.False(fired.Pulsed);
            Assert.True(fired.PathStarted);
            Assert.Equal(0, desktop.PulseCount);
        }
    }

    [Fact]
    public void EveryActionReportsItsSyntheticInput()
    {
        // Without this the app's own movement would read as the user coming back.
        (FakeDesktop desktop, KeepAwakeEngine engine) = New();
        using (engine)
        {
            int reported = 0;
            engine.SyntheticInputEmitted += (_, _) => Interlocked.Increment(ref reported);

            Settings settings = Configured(pulseSeconds: 15, pathSeconds: 15);
            engine.PulseNow(desktop.Now, settings);
            Assert.Equal(1, Volatile.Read(ref reported));

            engine.ClickNow(desktop.Now, settings);
            Assert.Equal(2, Volatile.Read(ref reported));

            Assert.True(engine.RunPathNow(settings));
            Assert.True(WaitUntil(() => !engine.IsPathRunning));
            Assert.True(Volatile.Read(ref reported) > 2, "path frames should be reported too");
        }
    }

    [Fact]
    public void PathRunAbortsWhenTheUserTakesTheMouse()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine) = New();
        using (engine)
        {
            // Long and slow, so there are plenty of frames to interrupt.
            Settings settings = Normalized(new Settings
            {
                PathEnabled = true,
                PathPattern = PathPattern.Circle,
                PathRadius = 400,
                PathSpeedPixelsPerSecond = 200,
                PathOrigin = PathOrigin.ScreenCenter,
            });

            Assert.True(engine.RunPathNow(settings));
            Assert.True(WaitUntil(() => engine.IsPathRunning));

            // A real hand, somewhere the player did not put the pointer.
            desktop.Cursor = new CursorPoint(17, 23);

            Assert.True(WaitUntil(() => !engine.IsPathRunning));
            Assert.Equal(1, engine.PathAbortedRuns);
        }
    }

    [Fact]
    public void OnlyOnePathRunsAtATime()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine) = New();
        using (engine)
        {
            Settings settings = Normalized(new Settings
            {
                PathEnabled = true,
                PathPattern = PathPattern.Circle,
                PathRadius = 400,
                PathSpeedPixelsPerSecond = 200,
            });

            Assert.True(engine.RunPathNow(settings));
            Assert.True(WaitUntil(() => engine.IsPathRunning));
            Assert.False(engine.RunPathNow(settings));

            engine.ResetSchedule(desktop.Now);
            Assert.True(WaitUntil(() => !engine.IsPathRunning));
        }
    }

    [Fact]
    public void AutoClickerFiresOnItsOwnTimerFasterThanThePollInterval()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine) = New();
        using (engine)
        {
            Settings settings = Normalized(new Settings
            {
                AutoClickEnabled = true,
                AutoClickIntervalMs = 20,
                AutoClickButton = MouseButton.Right,
                AdaptiveMode = false,
            });

            engine.SetAutoClickActive(true, settings);

            // Several clicks well inside a single one-second controller poll.
            Assert.True(WaitUntil(() => engine.ClickCount >= 3, 2000));
            Assert.Equal(MouseButton.Right, desktop.LastButton);

            engine.SetAutoClickActive(false, settings);
            Assert.False(engine.IsAutoClickActive);
        }
    }

    [Fact]
    public void AutoClickerStandsDownTheMomentThePointerMoves()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine) = New();
        using (engine)
        {
            Settings settings = Normalized(new Settings
            {
                AutoClickEnabled = true,
                AutoClickIntervalMs = 20,
                AdaptiveMode = false,
            });

            engine.SetAutoClickActive(true, settings);
            Assert.True(WaitUntil(() => engine.ClickCount >= 2, 2000));

            desktop.Cursor = new CursorPoint(1, 1);

            Assert.True(WaitUntil(() => !engine.IsAutoClickActive, 2000));
            Assert.Equal(1, engine.ClickInterruptions);

            long settled = engine.ClickCount;
            Thread.Sleep(120);
            Assert.Equal(settled, engine.ClickCount);
        }
    }

    [Fact]
    public void AutoClickerIgnoresTheOnePixelWobbleOfItsOwnSiblingAction()
    {
        // A pulse caught mid-flight leaves the pointer a pixel out. That is our own
        // doing, not the user, and must not stand the clicker down.
        (FakeDesktop desktop, KeepAwakeEngine engine) = New();
        using (engine)
        {
            Settings settings = Normalized(new Settings
            {
                AutoClickEnabled = true,
                AutoClickIntervalMs = 20,
                AdaptiveMode = false,
            });

            engine.SetAutoClickActive(true, settings);
            Assert.True(WaitUntil(() => engine.ClickCount >= 2, 2000));

            desktop.Cursor = new CursorPoint(desktop.Cursor.X + 1, desktop.Cursor.Y);

            long before = engine.ClickCount;
            Assert.True(WaitUntil(() => engine.ClickCount > before + 2, 2000));
            Assert.True(engine.IsAutoClickActive);
            Assert.Equal(0, engine.ClickInterruptions);
        }
    }

    [Fact]
    public void AutoClickerIsNeverArmedWithoutTheSetting()
    {
        (FakeDesktop _, KeepAwakeEngine engine) = New();
        using (engine)
        {
            var settings = new Settings { AutoClickEnabled = false, AutoClickIntervalMs = 20 };

            engine.SetAutoClickActive(true, settings);

            Assert.False(engine.IsAutoClickActive);
        }
    }

    [Fact]
    public void ResetScheduleStandsEveryActionDown()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine) = New();
        using (engine)
        {
            Settings settings = Normalized(new Settings
            {
                AutoClickEnabled = true,
                AutoClickIntervalMs = 20,
                PathEnabled = true,
                PathPattern = PathPattern.Circle,
                PathRadius = 400,
                PathSpeedPixelsPerSecond = 200,
            });

            engine.SetAutoClickActive(true, settings);
            Assert.True(engine.RunPathNow(settings));
            Assert.True(WaitUntil(() => engine.IsPathRunning));

            engine.ResetSchedule(desktop.Now);

            Assert.False(engine.IsAutoClickActive);
            Assert.True(WaitUntil(() => !engine.IsPathRunning));
        }
    }

    [Fact]
    public void AdaptiveModeAddsPathMovementWhenPulsesAreBeingSwallowed()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine) = New();
        using (engine)
        {
            Settings settings = Normalized(new Settings
            {
                AdaptiveMode = true,
                CursorPulseEnabled = true,
                PathEnabled = false, // the user asked for pulses only
                MinIntervalSeconds = 15,
                MaxIntervalSeconds = 15,
                PathMinIntervalSeconds = 15,
                PathMaxIntervalSeconds = 15,
                PathPattern = PathPattern.Custom,
                PathCustomPoints = "500,500 502,500",
                PathSpeedPixelsPerSecond = 8000,
            });

            engine.ResetSchedule(desktop.Now);
            Assert.False(engine.RunDueActions(desktop.Now, settings).PathStarted);

            // Two cycles where the OS did not register our input.
            engine.NoteActionOutcome(settings, landed: false);
            engine.NoteActionOutcome(settings, landed: false);
            Assert.Equal(AdaptiveLevel.Assertive, engine.Tuner.Level);

            desktop.Now += 60_000;
            Assert.True(engine.RunDueActions(desktop.Now, settings).PathStarted);
            Assert.True(WaitUntil(() => !engine.IsPathRunning));
        }
    }

    [Fact]
    public void AdaptiveModeNeverStartsClickingOnItsOwn()
    {
        (FakeDesktop _, KeepAwakeEngine engine) = New();
        using (engine)
        {
            var settings = new Settings { AdaptiveMode = true, AutoClickEnabled = false, AutoClickIntervalMs = 20 };

            for (int i = 0; i < 20; i++) engine.NoteActionOutcome(settings, landed: false);
            engine.SetAutoClickActive(true, settings);

            Assert.False(engine.IsAutoClickActive);
            Assert.Equal(0, engine.ClickCount);
        }
    }
}
