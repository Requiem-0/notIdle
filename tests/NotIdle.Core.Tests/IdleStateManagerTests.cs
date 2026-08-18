using NotIdle.Core;
using Xunit;

namespace NotIdle.Core.Tests;

public class IdleStateManagerTests
{
    private static StateInputs Inputs(
        double sinceSeconds,
        bool enabled = true,
        bool locked = false,
        bool standDown = false,
        int thresholdSeconds = 30) =>
        new(enabled, locked, standDown,
            TimeSpan.FromSeconds(sinceSeconds), TimeSpan.FromSeconds(thresholdSeconds));

    [Theory]
    [InlineData(0, ActivityState.Active)]
    [InlineData(2, ActivityState.Active)]
    [InlineData(10, ActivityState.Idle)]
    [InlineData(29.5, ActivityState.Idle)]
    [InlineData(30, ActivityState.KeepAwake)]
    [InlineData(3000, ActivityState.KeepAwake)]
    public void ClassifiesByGenuineIdleTime(double since, ActivityState expected)
    {
        var machine = new IdleStateManager();
        Assert.Equal(expected, machine.Evaluate(Inputs(since), 1000));
    }

    [Fact]
    public void DisabledBeatsEverything()
    {
        var machine = new IdleStateManager();
        Assert.Equal(ActivityState.Disabled, machine.Evaluate(Inputs(9999, enabled: false), 1000));
    }

    [Fact]
    public void LockedBeatsFullScreenAndIdle()
    {
        var machine = new IdleStateManager();
        Assert.Equal(ActivityState.Locked,
            machine.Evaluate(Inputs(9999, locked: true, standDown: true), 1000));
    }

    [Fact]
    public void FullScreenSuspendsEvenPastTheThreshold()
    {
        var machine = new IdleStateManager();
        Assert.Equal(ActivityState.Suspended, machine.Evaluate(Inputs(9999, standDown: true), 1000));
    }

    [Fact]
    public void ShortThreshold_StillReachesIdle()
    {
        var machine = new IdleStateManager();
        // With a 4s threshold the active window is capped at half, i.e. 2s.
        Assert.Equal(ActivityState.Active, machine.Evaluate(Inputs(1, thresholdSeconds: 4), 1000));
        Assert.Equal(ActivityState.Idle, machine.Evaluate(Inputs(3, thresholdSeconds: 4), 2000));
    }

    [Fact]
    public void AccumulatesKeepAwakeDuration()
    {
        var machine = new IdleStateManager();
        machine.Evaluate(Inputs(0), 0);
        machine.Evaluate(Inputs(60), 10_000);   // enters KeepAwake at t=10s
        machine.Evaluate(Inputs(60), 70_000);   // still holding at t=70s

        Assert.Equal(TimeSpan.FromSeconds(60), machine.TotalKeepAwakeAt(70_000));

        machine.Evaluate(Inputs(0), 70_000);    // user returns; run closes
        Assert.Equal(TimeSpan.FromSeconds(60), machine.TotalKeepAwakeAt(200_000));
    }

    [Fact]
    public void LockedTimeCountsAsKeepAwake_AndDoesNotStartASecondSession()
    {
        var machine = new IdleStateManager();
        machine.Evaluate(Inputs(0), 0);
        machine.Evaluate(Inputs(60), 10_000);                    // KeepAwake
        machine.Evaluate(Inputs(60, locked: true), 20_000);      // -> Locked, same run

        Assert.Equal(1, machine.KeepAwakeSessions);
        Assert.Equal(TimeSpan.FromSeconds(50), machine.TotalKeepAwakeAt(60_000));
    }

    [Fact]
    public void ActiveAndIdleTimeDoNotCountAsKeepAwake()
    {
        var machine = new IdleStateManager();
        machine.Evaluate(Inputs(0), 0);
        machine.Evaluate(Inputs(10), 60_000); // Idle for a minute

        Assert.Equal(TimeSpan.Zero, machine.TotalKeepAwakeAt(60_000));
        Assert.Equal(0, machine.KeepAwakeSessions);
    }

    [Fact]
    public void RaisesStateChangedOnlyOnTransitions()
    {
        var machine = new IdleStateManager();
        var seen = new List<ActivityState>();
        machine.StateChanged += (_, next) => seen.Add(next);

        machine.Evaluate(Inputs(0), 0);
        machine.Evaluate(Inputs(0), 1000);   // no change
        machine.Evaluate(Inputs(10), 2000);  // -> Idle
        machine.Evaluate(Inputs(11), 3000);  // no change
        machine.Evaluate(Inputs(60), 4000);  // -> KeepAwake

        Assert.Equal([ActivityState.Active, ActivityState.Idle, ActivityState.KeepAwake], seen);
    }

    [Fact]
    public void OnlyKeepAwakeAllowsACursorPulse()
    {
        Assert.True(ActivityState.KeepAwake.AllowsCursorPulse());
        Assert.False(ActivityState.Locked.AllowsCursorPulse());
        Assert.False(ActivityState.Suspended.AllowsCursorPulse());
        Assert.False(ActivityState.Active.AllowsCursorPulse());
        Assert.False(ActivityState.Idle.AllowsCursorPulse());
        Assert.False(ActivityState.Disabled.AllowsCursorPulse());
    }
}
