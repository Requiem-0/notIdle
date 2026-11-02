using NotIdle.Core;
using Xunit;

namespace NotIdle.Core.Tests;

public class KeepAwakeEngineTests
{
    private static (FakeDesktop desktop, KeepAwakeEngine engine, Settings settings) New(
        int min = 15, int max = 20, bool restoreCursor = true)
    {
        var desktop = new FakeDesktop();
        var settings = new Settings
        {
            MinIntervalSeconds = min,
            MaxIntervalSeconds = max,
            RestoreCursor = restoreCursor,
        };
        settings.Normalize();
        return (desktop, new KeepAwakeEngine(desktop, desktop, desktop), settings);
    }

    [Fact]
    public void FirstPulseIsDueImmediatelyAfterReset()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine, Settings settings) = New();
        engine.ResetSchedule(desktop.Now);

        Assert.True(engine.TryPulse(desktop.Now, settings, out _));
        Assert.Equal(1, desktop.PulseCount);
    }

    [Fact]
    public void DoesNotPulseBeforeTheIntervalElapses()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine, Settings settings) = New();
        engine.ResetSchedule(desktop.Now);
        engine.TryPulse(desktop.Now, settings, out _);

        // A second short of the minimum interval, nothing should fire.
        for (int i = 0; i < settings.MinIntervalSeconds - 1; i++)
        {
            desktop.Now += 1000;
            Assert.False(engine.TryPulse(desktop.Now, settings, out _));
        }

        Assert.Equal(1, desktop.PulseCount);
    }

    [Fact]
    public void IntervalsAreRandomizedWithinTheConfiguredRange()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine, Settings settings) = New(min: 15, max: 25);
        engine.ResetSchedule(desktop.Now);

        var gaps = new List<int>();
        long lastPulse = 0; // the first pulse is due immediately by design, so it sets the origin

        for (int i = 0; i < 3000; i++)
        {
            desktop.Now += 1000;
            if (!engine.TryPulse(desktop.Now, settings, out _)) continue;

            if (lastPulse != 0) gaps.Add((int)((desktop.Now - lastPulse) / 1000));
            lastPulse = desktop.Now;
        }

        Assert.True(gaps.Count > 50, $"expected many pulses, saw {gaps.Count}");
        // Not a fixed heartbeat.
        Assert.True(gaps.Distinct().Count() > 3, "intervals were not randomized");
        // The +1 absorbs sub-second jitter observed at one-second sampling.
        Assert.All(gaps, g => Assert.InRange(g, settings.MinIntervalSeconds, settings.MaxIntervalSeconds + 1));
    }

    [Fact]
    public void RestoresTheCursorWhenAsked()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine, Settings settings) = New(restoreCursor: true);
        CursorPoint parked = desktop.Cursor;

        engine.ResetSchedule(desktop.Now);
        for (int i = 0; i < 500; i++)
        {
            desktop.Now += 1000;
            engine.TryPulse(desktop.Now, settings, out _);
        }

        Assert.True(desktop.PulseCount > 10);
        Assert.Equal(parked, desktop.Cursor);
    }

    [Fact]
    public void WakeLockIsIdempotentAndReleasable()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine, _) = New();

        engine.HoldWakeLock(keepDisplayOn: true);
        engine.HoldWakeLock(keepDisplayOn: true);
        Assert.True(desktop.PowerHeld);
        Assert.True(desktop.DisplayHeld);
        Assert.True(engine.WakeLockHeld);

        engine.HoldWakeLock(keepDisplayOn: false);
        Assert.False(desktop.DisplayHeld);

        engine.ReleaseWakeLock();
        Assert.False(desktop.PowerHeld);
        Assert.False(engine.WakeLockHeld);
    }

    [Fact]
    public void DisposeReleasesTheWakeLock()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine, _) = New();
        engine.HoldWakeLock(keepDisplayOn: true);

        engine.Dispose();

        Assert.False(desktop.PowerHeld);
    }

    [Fact]
    public void PulseNowIgnoresTheSchedule()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine, Settings settings) = New();
        engine.ResetSchedule(desktop.Now);
        engine.TryPulse(desktop.Now, settings, out _);

        engine.PulseNow(desktop.Now, settings);

        Assert.Equal(2, desktop.PulseCount);
        Assert.Equal(2, engine.PulseCount);
    }

    [Fact]
    public void ScrollNowEmitsScrollAndCountsEvent()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine, Settings settings) = New();
        settings.ScrollEnabled = true;
        settings.ScrollMode = ScrollMode.Down;
        settings.ScrollAmount = 3;

        engine.ScrollNow(desktop.Now, settings);

        Assert.Equal(1, desktop.ScrollCount);
        Assert.Equal(-3, desktop.LastScrollNotches);
        Assert.Equal(1, engine.ScrollCount);
    }

    [Fact]
    public void TryScrollRespectsScheduleAndModes()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine, Settings settings) = New();
        settings.ScrollEnabled = true;
        settings.ScrollMode = ScrollMode.Up;
        settings.ScrollAmount = 4;
        settings.ScrollMinIntervalSeconds = 30;
        settings.ScrollMaxIntervalSeconds = 60;

        engine.ResetSchedule(desktop.Now);

        // First scroll is due immediately after reset
        Assert.True(engine.TryScroll(desktop.Now, settings));
        Assert.Equal(1, desktop.ScrollCount);
        Assert.Equal(4, desktop.LastScrollNotches);

        // Before interval passes, should not scroll
        desktop.Now += 1000;
        Assert.False(engine.TryScroll(desktop.Now, settings));
        Assert.Equal(1, desktop.ScrollCount);

        // Fast forward 61 seconds
        desktop.Now += 61_000;
        Assert.True(engine.TryScroll(desktop.Now, settings));
        Assert.Equal(2, desktop.ScrollCount);
    }

    [Fact]
    public void BurstModeStartsAndStopsWithEngine()
    {
        (FakeDesktop desktop, KeepAwakeEngine engine, Settings settings) = New();
        using (engine)
        {
            settings.BurstModeEnabled = true;
            settings.TargetActivityPercent = 85;

            engine.RunDueActions(desktop.Now, settings);
            Assert.True(engine.IsBurstRunning);

            // Stand down via ResetSchedule
            engine.ResetSchedule(desktop.Now);
            Assert.False(engine.IsBurstRunning);

            // Stand down via settings
            settings.BurstModeEnabled = false;
            engine.RunDueActions(desktop.Now, settings);
            Assert.False(engine.IsBurstRunning);
        }
    }

    [Fact]
    public void TargetActivityPercentNormalization()
    {
        var s = new Settings { TargetActivityPercent = 10 };
        s.Normalize();
        Assert.Equal(Settings.MinTargetActivityPercent, s.TargetActivityPercent);

        s.TargetActivityPercent = 120;
        s.Normalize();
        Assert.Equal(Settings.MaxTargetActivityPercent, s.TargetActivityPercent);
    }
}
