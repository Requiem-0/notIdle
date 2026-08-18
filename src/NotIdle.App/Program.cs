using System.Windows.Forms;
using NotIdle.Core;
using NotIdle.Core.Monitoring;
using NotIdle.Core.Native;

namespace NotIdle.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single-instance guard: a second launch exits immediately.
        using var mutex = new Mutex(initiallyOwned: true, "NotIdle.SingleInstance.9A8D51C0", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();

        // ── Composition root ─────────────────────────────────────────────────
        // Every Win32 dependency is injected here; nothing below this line knows
        // about the platform, which is what keeps the core unit-testable.
        var settingsManager = new SettingsManager(new JsonSettingsStore());

        var activity = new UserActivityMonitor(new Win32InputSource());
        var windows = new WindowMonitor(new Win32ForegroundWindowSource());
        var stateMachine = new IdleStateManager();

        using var executionState = new Win32ExecutionState();
        var keepAwake = new KeepAwakeEngine(new Win32SyntheticInput(), executionState);

        var controller = new NotIdleController(settingsManager, activity, windows, stateMachine, keepAwake);
        using var context = new TrayApplicationContext(controller, settingsManager);

        // Trim the startup working set once the UI has gone idle (init pages released).
        void OnFirstIdle(object? s, EventArgs e)
        {
            Application.Idle -= OnFirstIdle;
            MemoryOptimizer.TrimWorkingSet();
        }
        Application.Idle += OnFirstIdle;

        Application.Run(context);
    }
}
