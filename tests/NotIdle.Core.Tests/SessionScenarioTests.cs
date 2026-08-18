using NotIdle.Core;
using Xunit;

namespace NotIdle.Core.Tests;

/// <summary>
/// End-to-end scenarios over the wired-up components, on a virtual clock.
/// </summary>
public class SessionScenarioTests
{
    [Fact]
    public void WalkingAway_EngagesKeepAwakeAtTheThreshold()
    {
        var sim = new SessionSimulator();

        for (int i = 0; i < 5; i++) { sim.Desktop.RealInput(); Assert.Equal(ActivityState.Active, sim.Tick()); }

        // The genuine clock reads 0 at the last active tick, so 29 more ticks lands
        // one second short of the 30s threshold.
        sim.Tick(seconds: 29);
        Assert.Equal(ActivityState.Idle, sim.State);
        Assert.Equal(0, sim.Desktop.PulseCount);

        Assert.Equal(ActivityState.KeepAwake, sim.Tick());
        Assert.Equal(1, sim.Desktop.PulseCount);
    }

    [Fact]
    public void TenMinutesUnattended_KeepsClimbingAndNeverLeaksGenuineEvents()
    {
        var sim = new SessionSimulator();
        sim.RunUntilKeepAwake();

        long genuineBefore = sim.Activity.GenuineEventCount;
        sim.Tick(seconds: 600);

        Assert.Equal(ActivityState.KeepAwake, sim.State);
        Assert.Equal(genuineBefore, sim.Activity.GenuineEventCount);
        Assert.True(sim.SinceGenuineInput > TimeSpan.FromMinutes(10),
            $"genuine idle was {sim.SinceGenuineInput}");
        Assert.True(sim.Desktop.PulseCount > 20);
    }

    [Fact]
    public void UserReturning_GoesStraightBackToActive()
    {
        var sim = new SessionSimulator();
        sim.RunUntilKeepAwake();
        sim.Tick(seconds: 120);

        sim.Desktop.RealInput();

        Assert.Equal(ActivityState.Active, sim.Tick());
        Assert.True(sim.SinceGenuineInput < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void FullScreen_HoldsTheWakeLockButStopsPulsing()
    {
        var sim = new SessionSimulator();
        sim.RunUntilKeepAwake();

        int pulsesBefore = sim.Desktop.PulseCount;
        sim.Tick(seconds: 300, standDown: true);

        Assert.Equal(ActivityState.Suspended, sim.State);
        Assert.Equal(pulsesBefore, sim.Desktop.PulseCount);
        // The whole point of the fix: a full-screen film should not let the display sleep.
        Assert.True(sim.Desktop.PowerHeld);
    }

    [Fact]
    public void Locked_HoldsTheWakeLockButStopsPulsing()
    {
        var sim = new SessionSimulator();
        sim.RunUntilKeepAwake();

        int pulsesBefore = sim.Desktop.PulseCount;
        sim.Tick(seconds: 300, locked: true);

        Assert.Equal(ActivityState.Locked, sim.State);
        Assert.Equal(pulsesBefore, sim.Desktop.PulseCount);
        Assert.True(sim.Desktop.PowerHeld);
    }

    [Fact]
    public void UnlockingReBaselinesTheGenuineClock()
    {
        var sim = new SessionSimulator();
        sim.RunUntilKeepAwake();
        sim.Tick(seconds: 3600, locked: true);

        // What NotifySessionUnlocked does: unlocking took a physical person.
        sim.Activity.MarkActiveNow();

        Assert.Equal(ActivityState.Active, sim.Tick());
    }

    [Fact]
    public void DisabledReleasesEverything()
    {
        var sim = new SessionSimulator();
        sim.RunUntilKeepAwake();
        Assert.True(sim.Desktop.PowerHeld);

        sim.Settings.Enabled = false;
        int pulsesBefore = sim.Desktop.PulseCount;
        sim.Tick(seconds: 120);

        Assert.Equal(ActivityState.Disabled, sim.State);
        Assert.False(sim.Desktop.PowerHeld);
        Assert.Equal(pulsesBefore, sim.Desktop.PulseCount);
    }

    [Fact]
    public void CursorNeverDriftsAcrossALongUnattendedRun()
    {
        var sim = new SessionSimulator();
        sim.RunUntilKeepAwake();

        CursorPoint parked = sim.Desktop.Cursor;
        sim.Tick(seconds: 1800);

        Assert.Equal(parked, sim.Desktop.Cursor);
    }
}
