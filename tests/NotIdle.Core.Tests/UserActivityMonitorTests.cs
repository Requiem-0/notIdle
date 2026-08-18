using NotIdle.Core;
using NotIdle.Core.Monitoring;
using Xunit;

namespace NotIdle.Core.Tests;

/// <summary>
/// The load-bearing invariant of the whole app: NotIdle's own synthetic input must
/// never be mistaken for the user being present.
/// </summary>
public class UserActivityMonitorTests
{
    private static (FakeDesktop desktop, UserActivityMonitor monitor) NewMonitor()
    {
        var desktop = new FakeDesktop();
        var monitor = new UserActivityMonitor(desktop);
        monitor.Sample(0x1, desktop.Now); // establish a baseline
        return (desktop, monitor);
    }

    [Fact]
    public void QuietDesktop_ProducesNoGenuineEvents()
    {
        (FakeDesktop desktop, UserActivityMonitor monitor) = NewMonitor();
        long before = monitor.GenuineEventCount;

        for (int i = 0; i < 10; i++)
        {
            desktop.Now += 1000;
            monitor.Sample(0x1, desktop.Now);
        }

        Assert.Equal(before, monitor.GenuineEventCount);
    }

    [Fact]
    public void SyntheticPulse_DoesNotCountAsGenuineInput()
    {
        (FakeDesktop desktop, UserActivityMonitor monitor) = NewMonitor();
        long before = monitor.GenuineEventCount;

        for (int i = 0; i < 20; i++)
        {
            CursorPoint after = desktop.Pulse(restoreCursor: true);
            monitor.NoteSyntheticInput(desktop.Now, after);

            desktop.Now += 1000;
            ActivitySample sample = monitor.Sample(0x1, desktop.Now);
            Assert.False(sample.GenuineInputDetected);
        }

        Assert.Equal(before, monitor.GenuineEventCount);
    }

    [Fact]
    public void SyntheticPulse_DoesNotResetTheGenuineIdleClock()
    {
        (FakeDesktop desktop, UserActivityMonitor monitor) = NewMonitor();

        ActivitySample sample = default;
        for (int i = 0; i < 600; i++)
        {
            if (i % 20 == 0)
            {
                CursorPoint after = desktop.Pulse(restoreCursor: true);
                monitor.NoteSyntheticInput(desktop.Now, after);
            }
            desktop.Now += 1000;
            sample = monitor.Sample(0x1, desktop.Now);
        }

        // The OS counter is freshly reset by our pulses...
        Assert.True(desktop.GetIdleTime() < TimeSpan.FromSeconds(21));
        // ...while the genuine clock reflects the full ten minutes of absence.
        Assert.True(sample.TimeSinceGenuineInput > TimeSpan.FromMinutes(9.9),
            $"genuine idle was {sample.TimeSinceGenuineInput}");
    }

    [Fact]
    public void MouseMovement_CountsAsGenuine()
    {
        (FakeDesktop desktop, UserActivityMonitor monitor) = NewMonitor();

        desktop.RealInput();
        desktop.Now += 1000;

        Assert.True(monitor.Sample(0x1, desktop.Now).GenuineInputDetected);
    }

    [Fact]
    public void KeyboardOnly_CountsAsGenuine_EvenWithoutCursorMovement()
    {
        (FakeDesktop desktop, UserActivityMonitor monitor) = NewMonitor();

        // Let the desktop go quiet, so the keystroke is distinguishable from the
        // baseline input rather than landing on the very same tick.
        desktop.Now += 5000;
        monitor.Sample(0x1, desktop.Now);

        desktop.RealInput(moveMouse: false);
        desktop.Now += 1000;

        ActivitySample sample = monitor.Sample(0x1, desktop.Now);
        Assert.True(sample.GenuineInputDetected);
        Assert.True(sample.TimeSinceGenuineInput < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ForegroundWindowChange_CountsAsGenuine()
    {
        (FakeDesktop desktop, UserActivityMonitor monitor) = NewMonitor();

        desktop.Now += 1000;
        monitor.Sample(0x1, desktop.Now);

        desktop.Now += 1000;
        Assert.True(monitor.Sample(0x2, desktop.Now).GenuineInputDetected);
    }

    [Fact]
    public void RealInputImmediatelyAfterAPulse_IsStillCredited_BecauseTheCursorMoved()
    {
        (FakeDesktop desktop, UserActivityMonitor monitor) = NewMonitor();

        CursorPoint after = desktop.Pulse(restoreCursor: true);
        monitor.NoteSyntheticInput(desktop.Now, after);

        // The user grabs the mouse inside the attribution window. Timing alone would
        // discard this; the cursor having moved is what saves it.
        desktop.RealInput();
        desktop.Now += 100;

        Assert.True(monitor.Sample(0x1, desktop.Now).GenuineInputDetected);
    }

    [Fact]
    public void MarkActiveNow_ResetsTheGenuineClock()
    {
        (FakeDesktop desktop, UserActivityMonitor monitor) = NewMonitor();

        desktop.Now += 600_000;
        monitor.Sample(0x1, desktop.Now);
        monitor.MarkActiveNow();

        Assert.True(monitor.TimeSinceGenuineInput < TimeSpan.FromSeconds(2));
    }
}
