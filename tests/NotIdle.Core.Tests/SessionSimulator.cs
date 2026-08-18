using NotIdle.Core;
using NotIdle.Core.Monitoring;

namespace NotIdle.Core.Tests;

/// <summary>
/// Wires the components together the same way <see cref="NotIdleController"/> does,
/// but on a virtual clock the test drives. This keeps the integration tests honest
/// about ordering without depending on a real timer.
/// </summary>
public sealed class SessionSimulator
{
    public FakeDesktop Desktop { get; } = new();
    public UserActivityMonitor Activity { get; }
    public IdleStateManager Machine { get; } = new();
    public KeepAwakeEngine Engine { get; }
    public Settings Settings { get; }

    /// <summary>Genuine idle as reported by the most recent sample.</summary>
    public TimeSpan SinceGenuineInput { get; private set; }

    public ActivityState State => Machine.State;
    public nint Window { get; set; } = 0x0BAD;

    public SessionSimulator(Settings? settings = null)
    {
        Activity = new UserActivityMonitor(Desktop);
        Engine = new KeepAwakeEngine(Desktop, Desktop, Desktop);
        Settings = settings ?? new Settings
        {
            IdleThresholdSeconds = 30,
            MinIntervalSeconds = 15,
            MaxIntervalSeconds = 20,
        };
        Settings.Normalize();
    }

    /// <summary>Advance one poll interval and run the controller's decision cycle.</summary>
    public ActivityState Tick(bool locked = false, bool standDown = false)
    {
        Desktop.Now += 1000;

        ForegroundWindow window = locked ? ForegroundWindow.Unknown : new ForegroundWindow(Window, "app", false, false);
        ActivitySample sample = Activity.Sample(window.Handle, Desktop.Now);
        SinceGenuineInput = sample.TimeSinceGenuineInput;

        var inputs = new StateInputs(
            Enabled: Settings.Enabled,
            Locked: locked,
            StandDown: standDown,
            SinceGenuineInput: sample.TimeSinceGenuineInput,
            IdleThreshold: Settings.IdleThreshold);

        ActivityState state = Machine.Evaluate(inputs, Desktop.Now);

        if (state == ActivityState.Disabled)
        {
            Engine.ReleaseWakeLock();
            Engine.ResetSchedule(Desktop.Now);
            return state;
        }

        Engine.HoldWakeLock(Settings.KeepDisplayOn);

        if (state.AllowsCursorPulse())
        {
            if (Engine.TryPulse(Desktop.Now, Settings, out CursorPoint after))
                Activity.NoteSyntheticInput(Desktop.Now, after);
        }
        else
        {
            Engine.ResetSchedule(Desktop.Now);
        }

        return state;
    }

    public void Tick(int seconds, bool locked = false, bool standDown = false)
    {
        for (int i = 0; i < seconds; i++) Tick(locked, standDown);
    }

    /// <summary>Run until keep-awake engages, so a test can start from that point.</summary>
    public void RunUntilKeepAwake()
    {
        for (int i = 0; i < Settings.IdleThresholdSeconds + 5; i++)
        {
            if (Tick() == ActivityState.KeepAwake) return;
        }
        throw new InvalidOperationException("keep-awake never engaged");
    }
}
