using System.Windows.Forms;
using NotIdle.App.Startup;
using NotIdle.Core;

namespace NotIdle.App;

/// <summary>
/// The UI layer and composition root. Owns the tray icon and menu, subscribes to
/// the engine, and marshals engine callbacks onto the UI thread. Holds no
/// business logic — the engine is the single source of truth for state.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly IdleEngine _engine;
    private readonly ISettingsStore _store;
    private readonly TrayIconFactory _icons = new();
    private readonly NotifyIcon _tray;
    private readonly Control _marshaller;
    private readonly ControlPanelForm _controlPanel;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _startupItem;

    private Settings _settings;

    public TrayApplicationContext(IdleEngine engine, ISettingsStore store, Settings settings)
    {
        _engine = engine;
        _store = store;
        _settings = settings;

        _marshaller = new Control();
        _ = _marshaller.Handle; // Realize the handle now so BeginInvoke works once the loop runs.

        _statusItem = new ToolStripMenuItem("Status: —") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Pause", null, (_, _) => ToggleEnabled());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = _settings.StartWithWindows,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("Open NotIdle…", null, (_, _) => ShowControlPanel()));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _controlPanel = new ControlPanelForm(_engine, _store, _settings);

        _tray = new NotifyIcon
        {
            Icon = _icons.For(AppState.Paused),
            Text = "NotIdle",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.Click += (_, _) => ShowControlPanel();

        _engine.StateChanged += OnEngineStateChanged;
        StartupManager.Apply(_settings.StartWithWindows); // Reconcile registry with saved setting.

        if (_settings.Enabled) _engine.Start();
        Render(_engine.CurrentState);
    }

    // ── Engine → UI (marshaled) ──────────────────────────────────────────────
    private void OnEngineStateChanged(AppState state)
    {
        if (_marshaller.IsHandleCreated)
            _marshaller.BeginInvoke(() => Render(state));
    }

    private void Render(AppState state)
    {
        string label = state switch
        {
            AppState.Monitoring => "Monitoring",
            AppState.Idle => "Idle (counting down)",
            AppState.ActionRunning => "Action running",
            _ => "Paused",
        };

        _statusItem.Text = $"Status: {label}";
        _toggleItem.Text = _settings.Enabled ? "Pause" : "Resume";
        _tray.Icon = _icons.For(state);
        _tray.Text = Truncate(
            $"NotIdle — {label}\n" +
            $"Idle after {_settings.DescribeThreshold()} · acts {_settings.DescribeAction()}");
    }

    // ── UI actions ───────────────────────────────────────────────────────────
    private void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        _store.Save(_settings);
        _engine.ApplySettings(_settings);
        Render(_engine.CurrentState);
    }

    private void ToggleStartup()
    {
        _settings.StartWithWindows = !_settings.StartWithWindows;
        StartupManager.Apply(_settings.StartWithWindows);
        _startupItem.Checked = _settings.StartWithWindows;
        _store.Save(_settings);
    }

    private void ShowControlPanel()
    {
        _controlPanel.Show();
        if (_controlPanel.WindowState == FormWindowState.Minimized)
            _controlPanel.WindowState = FormWindowState.Normal;
        _controlPanel.Activate();
        _controlPanel.BringToFront();
    }

    // Tray tooltips are capped at 127 chars by the shell.
    private static string Truncate(string s) => s.Length <= 127 ? s : s[..127];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.StateChanged -= OnEngineStateChanged;
            _controlPanel.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _icons.Dispose();
            _engine.Dispose();
            _marshaller.Dispose();
        }
        base.Dispose(disposing);
    }
}
