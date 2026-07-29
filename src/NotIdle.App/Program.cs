using System.Windows.Forms;
using NotIdle.Core;
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

        // Composition root — wire concrete Win32 implementations into the engine.
        ISettingsStore store = new JsonSettingsStore();
        Settings settings = store.Load();

        var engine = new IdleEngine(new Win32InputMonitor(), new Win32MouseActuator(), settings);
        using var context = new TrayApplicationContext(engine, store, settings);

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
