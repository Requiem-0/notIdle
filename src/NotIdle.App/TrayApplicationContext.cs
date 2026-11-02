using System.Windows.Forms;
using Microsoft.Win32;
using NotIdle.App.Startup;
using NotIdle.Core;

namespace NotIdle.App;

/// <summary>
/// The tray shell. Owns the notify icon and menu, marshals controller callbacks
/// onto the UI thread, and reconciles "start with Windows" with the registry.
/// Holds no business logic — <see cref="NotIdleController"/> is the single source
/// of truth for state.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotIdleController _controller;
    private readonly SettingsManager _settingsManager;
    private readonly TrayIconFactory _icons = new();
    private readonly NotifyIcon _tray;
    private readonly Control _marshaller;
    private readonly DashboardForm _dashboard;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _timerSubmenu;
    private readonly ToolStripMenuItem _startupItem;
    private readonly GlobalHotkeyHook _hotkey;

    private Settings _settings;

    public TrayApplicationContext(NotIdleController controller, SettingsManager settingsManager)
    {
        _controller = controller;
        _settingsManager = settingsManager;
        _settings = settingsManager.Current;

        _marshaller = new Control();
        _ = _marshaller.Handle; // Realize the handle now so BeginInvoke works once the loop runs.

        _statusItem = new ToolStripMenuItem("Status: —") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Pause keep-awake", null, (_, _) => ToggleEnabled());
        _timerSubmenu = BuildTimerSubmenu();
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = _settings.StartWithWindows,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_timerSubmenu);
        menu.Items.Add(new ToolStripMenuItem("Open NotIdle…", null, (_, _) => ShowDashboard()));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _dashboard = new DashboardForm(_controller, _settingsManager);

        _tray = new NotifyIcon
        {
            Icon = _icons.For(ActivityState.Disabled),
            Text = _settings.StealthMode ? "System Audio Host" : "NotIdle",
            Visible = !_settings.GhostMode,
            ContextMenuStrip = menu,
        };
        _tray.Click += (_, _) => ShowDashboard();

        _hotkey = new GlobalHotkeyHook(
            onToggle: () =>
            {
                ToggleEnabled();
                bool enabled = _settingsManager.Current.Enabled;
                if (!_settings.StealthMode && !_settings.GhostMode)
                {
                    _tray.ShowBalloonTip(1500, "System Audio Host",
                        enabled ? "Audio endpoint resumed" : "Audio endpoint paused",
                        ToolTipIcon.Info);
                }
            },
            onDashboard: ShowDashboard);

        if (_settings.GlobalHotkeyEnabled) _hotkey.Register();

        _controller.StateChanged += OnStateChanged;
        _settingsManager.Changed += OnSettingsChanged;

        StartupManager.Apply(_settings.StartWithWindows); // Reconcile registry with saved setting.

        // Lock/unlock and sleep/resume are the two moments the polling loop cannot
        // infer on its own: no input arrives while locked or asleep, yet a person
        // is unmistakably present the instant either one ends.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _controller.Start();
        Render(_controller.CurrentState);
    }

    // ── Controller / settings → UI (marshaled) ───────────────────────────────

    private void OnStateChanged(ActivityState state)
    {
        if (_marshaller.IsHandleCreated)
            _marshaller.BeginInvoke(() => Render(state));
    }

    private void OnSettingsChanged(Settings settings)
    {
        // The registry is reconciled here, in one place, however the setting changed.
        StartupManager.Apply(settings.StartWithWindows);

        if (settings.GlobalHotkeyEnabled) _hotkey.Register();
        else _hotkey.Unregister();

        if (!_marshaller.IsHandleCreated) return;
        _marshaller.BeginInvoke(() =>
        {
            _settings = settings;
            _tray.Visible = !settings.GhostMode;
            _startupItem.Checked = settings.StartWithWindows;
            UpdateTimerChecks();
            Render(_controller.CurrentState);
        });
    }

    private ToolStripMenuItem BuildTimerSubmenu()
    {
        var menu = new ToolStripMenuItem("Keep awake for…");
        menu.DropDownItems.Add(CreateTimerItem("15 minutes", 15));
        menu.DropDownItems.Add(CreateTimerItem("30 minutes", 30));
        menu.DropDownItems.Add(CreateTimerItem("1 hour", 60));
        menu.DropDownItems.Add(CreateTimerItem("2 hours", 120));
        menu.DropDownItems.Add(CreateTimerItem("4 hours", 240));
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(CreateTimerItem("Indefinitely (No timer)", 0));
        return menu;
    }

    private ToolStripMenuItem CreateTimerItem(string text, int minutes)
    {
        return new ToolStripMenuItem(text, null, (_, _) => SetAutoOff(minutes))
        {
            Tag = minutes,
            Checked = _settings.AutoOffMinutes == minutes,
        };
    }

    private void SetAutoOff(int minutes)
    {
        _settingsManager.Update(s =>
        {
            s.AutoOffMinutes = minutes;
            s.Enabled = true;
        });
        string duration = minutes == 0 ? "Indefinite" : Settings.FormatDuration(minutes * 60);
        if (!_settings.StealthMode)
        {
            _tray.ShowBalloonTip(1500, "NotIdle", $"Keep-awake timer: {duration}", ToolTipIcon.Info);
        }
    }

    private void UpdateTimerChecks()
    {
        foreach (ToolStripItem item in _timerSubmenu.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem && menuItem.Tag is int mins)
            {
                menuItem.Checked = _settings.AutoOffMinutes == mins;
            }
        }
    }

    private void Render(ActivityState state)
    {
        string label = state.ToDisplayString();

        _statusItem.Text = $"Status: {label}";
        _toggleItem.Text = _settings.Enabled ? "Pause keep-awake" : "Resume keep-awake";
        _tray.Icon = _icons.For(state);
        _tray.Text = _settings.StealthMode
            ? "System Audio Host"
            : Truncate(
                $"NotIdle — {label}\n" +
                $"Idle after {_settings.DescribeThreshold()} · {_settings.DescribeActions()}\n" +
                $"Pulse every {_settings.DescribeInterval()}");
    }

    // ── Session / power events ───────────────────────────────────────────────
    // These arrive on the dedicated SystemEvents thread; the controller is
    // thread-safe, so they are forwarded straight through.

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.SessionLogoff:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _controller.NotifySessionLocked();
                break;

            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _controller.NotifySessionUnlocked();
                break;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _controller.NotifyResumedFromSleep();
    }

    // ── UI actions ───────────────────────────────────────────────────────────

    private void ToggleEnabled() => _settingsManager.Update(s => s.Enabled = !s.Enabled);

    private void ToggleStartup() => _settingsManager.Update(s => s.StartWithWindows = !s.StartWithWindows);

    public void SummonDashboard()
    {
        if (_marshaller.IsHandleCreated)
            _marshaller.BeginInvoke(ShowDashboard);
    }

    private void ShowDashboard()
    {
        if (_dashboard.WindowState == FormWindowState.Minimized)
            _dashboard.WindowState = FormWindowState.Normal;
        _dashboard.Show();
        _dashboard.TopMost = true;
        _dashboard.BringToFront();
        _dashboard.Activate();
        _dashboard.TopMost = false;
    }

    // Tray tooltips are capped at 127 chars by the shell.
    private static string Truncate(string s) => s.Length <= 127 ? s : s[..127];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _controller.StateChanged -= OnStateChanged;
            _settingsManager.Changed -= OnSettingsChanged;
            _dashboard.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _icons.Dispose();
            _hotkey.Dispose();
            _controller.Dispose();
            _marshaller.Dispose();
        }
        base.Dispose(disposing);
    }
}
