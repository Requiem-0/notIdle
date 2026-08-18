using NotIdle.Core;
using NotIdle.Core.Actions;
using Xunit;

namespace NotIdle.Core.Tests;

public class AdaptiveTunerTests
{
    private static void Feed(AdaptiveTuner tuner, bool landed, int times)
    {
        for (int i = 0; i < times; i++) tuner.Observe(landed);
    }

    [Fact]
    public void StartsAtTheConfiguredCadenceWithoutPathMovement()
    {
        var tuner = new AdaptiveTuner();

        Assert.Equal(AdaptiveLevel.Standard, tuner.Level);
        Assert.False(tuner.PlanFor(new Settings()).PathEnabled);
    }

    [Fact]
    public void EscalatesWhenOurInputIsBeingSwallowed()
    {
        var tuner = new AdaptiveTuner();

        Feed(tuner, landed: false, times: 2);

        Assert.Equal(AdaptiveLevel.Assertive, tuner.Level);
        Assert.True(tuner.PlanFor(new Settings()).PathEnabled);
    }

    [Fact]
    public void OneFlakeIsNotEnoughToEscalate()
    {
        var tuner = new AdaptiveTuner();

        tuner.Observe(false);
        tuner.Observe(true);
        tuner.Observe(false);

        Assert.Equal(AdaptiveLevel.Standard, tuner.Level);
    }

    [Fact]
    public void EscalationShortensTheIntervalWithoutGoingBelowTheUsersMinimum()
    {
        var tuner = new AdaptiveTuner();
        var settings = new Settings { MinIntervalSeconds = 40, MaxIntervalSeconds = 200 };

        Feed(tuner, landed: false, times: 2);
        AdaptivePlan plan = tuner.PlanFor(settings);

        Assert.Equal(40, plan.MinIntervalSeconds);
        Assert.InRange(plan.MaxIntervalSeconds, 40, 200);
        Assert.True(plan.MaxIntervalSeconds < settings.MaxIntervalSeconds);
    }

    [Fact]
    public void QuietensDownAgainOnceEverythingIsLanding()
    {
        var tuner = new AdaptiveTuner();
        var settings = new Settings { MinIntervalSeconds = 40, MaxIntervalSeconds = 200 };

        Feed(tuner, landed: false, times: 2);
        Assert.Equal(AdaptiveLevel.Assertive, tuner.Level);

        Feed(tuner, landed: true, times: 12);
        Assert.Equal(AdaptiveLevel.Standard, tuner.Level);

        Feed(tuner, landed: true, times: 12);
        Assert.Equal(AdaptiveLevel.Minimal, tuner.Level);

        AdaptivePlan plan = tuner.PlanFor(settings);
        Assert.Equal(settings.MaxIntervalSeconds, plan.MinIntervalSeconds);
        Assert.False(plan.PathEnabled);
    }

    [Fact]
    public void RelaxingIsSlowerThanEscalating()
    {
        var tuner = new AdaptiveTuner();

        Feed(tuner, landed: false, times: 2);
        Feed(tuner, landed: true, times: 5);

        // Still assertive: a handful of successes is not proof the problem is gone.
        Assert.Equal(AdaptiveLevel.Assertive, tuner.Level);
    }

    [Fact]
    public void ReportsWhenEvenTheAssertiveLevelIsGettingNowhere()
    {
        var tuner = new AdaptiveTuner();

        Feed(tuner, landed: false, times: 2);
        Assert.False(tuner.InputBlocked);

        Feed(tuner, landed: false, times: 6);
        Assert.True(tuner.InputBlocked);
        Assert.Contains("not registering", tuner.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneLandedActionClearsTheBlockedVerdict()
    {
        var tuner = new AdaptiveTuner();
        Feed(tuner, landed: false, times: 8);

        tuner.Observe(true);

        Assert.False(tuner.InputBlocked);
    }

    [Fact]
    public void ResetReturnsToTheQuietDefault()
    {
        var tuner = new AdaptiveTuner();
        Feed(tuner, landed: false, times: 8);

        tuner.Reset();

        Assert.Equal(AdaptiveLevel.Standard, tuner.Level);
        Assert.False(tuner.InputBlocked);
    }

    [Fact]
    public void NeverAsksForClicking()
    {
        // Escalation has no rung that enables the clicker; only the user does.
        var tuner = new AdaptiveTuner();
        var settings = new Settings();

        for (int i = 0; i < 50; i++)
        {
            tuner.Observe(i % 3 == 0);
            Assert.False(settings.AutoClickEnabled);
        }
    }
}
