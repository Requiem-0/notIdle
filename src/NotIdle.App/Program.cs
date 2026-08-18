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

        var inputSource = new Win32InputSource();
        var activity = new UserActivityMonitor(inputSource);
        var windows = new WindowMonitor(new Win32ForegroundWindowSource());
        var stateMachine = new IdleStateManager();

        using var executionState = new Win32ExecutionState();
        // The engine needs to read the cursor as well as move it: the path player and
        // the auto-clicker both abort the moment the pointer is somewhere they did
        // not put it, which is how the user always wins a tug of war over the mouse.
        var keepAwake = new KeepAwakeEngine(new Win32SyntheticInput(), executionState, inputSource);

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
