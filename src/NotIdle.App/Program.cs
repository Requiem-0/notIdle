using System.IO;
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
        string appName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "NotIdle";

        string eventName = $"{appName}.ShowEvent.9A8D51C0";

        // Single-instance guard: a second launch signals the running instance to pop up and exits.
        using var mutex = new Mutex(initiallyOwned: true, $"{appName}.SingleInstance.9A8D51C0", out bool isNew);
        if (!isNew)
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var existingEvent))
            {
                existingEvent.Set();
            }
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        ApplicationConfiguration.Initialize();

        // ── Composition root ─────────────────────────────────────────────────
        // Store settings in %APPDATA%\<AppName>\settings.json to match disguised identity.
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string newDir = Path.Combine(appData, appName);
        string settingsPath = Path.Combine(newDir, "settings.json");

        if (!File.Exists(settingsPath) && !string.Equals(appName, "NotIdle", StringComparison.OrdinalIgnoreCase))
        {
            string oldSettings = Path.Combine(appData, "NotIdle", "settings.json");
            if (File.Exists(oldSettings))
            {
                try
                {
                    Directory.CreateDirectory(newDir);
                    File.Copy(oldSettings, settingsPath, overwrite: false);
                }
                catch { /* best effort migration */ }
            }
        }

        var settingsManager = new SettingsManager(new JsonSettingsStore(settingsPath));

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

        RegisteredWaitHandle waitHandle = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            context.SummonDashboard();
        }, null, -1, false);

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
