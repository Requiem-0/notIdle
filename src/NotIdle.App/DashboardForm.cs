using System.Drawing;
using System.Windows.Forms;
using NotIdle.Core;

namespace NotIdle.App;

/// <summary>
/// The one window: a live dashboard plus a settings pane.
///
/// It holds no logic of its own — it renders <see cref="NotIdleController.GetStatus"/>
/// snapshots and routes edits through <see cref="SettingsManager"/>. The refresh
/// timer only runs while the window is visible, so the app stays quiet in the tray.
/// </summary>
internal sealed class DashboardForm : Form
{
    private const int RefreshIntervalMs = 500;

    private readonly NotIdleController _controller;
    private readonly SettingsManager _settingsManager;
    private readonly System.Windows.Forms.Timer _refresh = new();
    private readonly System.Windows.Forms.Timer _confirmTimer = new();

    // Dashboard
    private readonly StatePill _statePill = new();
    private readonly Label _stateDetail = new();
    private readonly Button _toggleButton = new();
    private readonly Label _sinceInteraction = NewValue();
    private readonly Label _totalKeepAwake = NewValue();
    private readonly Label _eventCount = NewValue();
    private readonly Label _interval = NewValue();
    private readonly Label _nextAction = NewValue();
    private readonly Label _foregroundApp = NewValue();
    private readonly Label _wakeLock = NewValue();
    private readonly Label _autoOff = NewValue();

    // Settings
    private readonly NumericUpDown _threshold = new();
    private readonly ComboBox _thresholdUnit = new();
    private readonly NumericUpDown _minInterval = new();
    private readonly NumericUpDown _maxInterval = new();
    private readonly NumericUpDown _autoOffMinutes = new();
    private readonly CheckBox _restoreCursor = new();
    private readonly CheckBox _keepDisplayOn = new();
    private readonly CheckBox _pauseFullScreen = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly Label _confirmLabel = new();

    public DashboardForm(NotIdleController controller, SettingsManager settingsManager)
    {
        _controller = controller;
        _settingsManager = settingsManager;

        Text = "NotIdle";
        Icon = TrayIconFactory.LoadAppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 524);
        BackColor = Theme.Canvas;
        Font = Theme.Body;

        Controls.Add(BuildTabs());

        _refresh.Interval = RefreshIntervalMs;
        _refresh.Tick += (_, _) => RenderStatus();

        _confirmTimer.Interval = 3000;
        _confirmTimer.Tick += (_, _) => { _confirmLabel.Text = string.Empty; _confirmTimer.Stop(); };

        _settingsManager.Changed += OnSettingsChanged;
        LoadSettingsInto(_settingsManager.Current);
        RenderStatus();

        FormClosing += (_, e) =>
        {
            // Closing the window means "get out of my way", not "quit".
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
        };
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private TabControl BuildTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };

        var dashboard = new TabPage("Dashboard") { BackColor = Theme.Canvas, Padding = new Padding(14) };
        dashboard.Controls.Add(BuildDashboard());

        var settings = new TabPage("Settings") { BackColor = Theme.Canvas, Padding = new Padding(14) };
        settings.Controls.Add(BuildSettings());

        tabs.TabPages.Add(dashboard);
        tabs.TabPages.Add(settings);
        return tabs;
    }

    private Control BuildDashboard()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Canvas,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildHeaderCard(), 0, 0);
        root.Controls.Add(BuildStatsCard(), 0, 1);
        root.Controls.Add(BuildActionsRow(), 0, 2);
        return root;
    }

    private Control BuildHeaderCard()
    {
        _statePill.BackColor = Theme.Surface;
        _statePill.Width = 150;
        _statePill.Margin = new Padding(0, 0, 0, 6);

        _stateDetail.AutoSize = true;
        _stateDetail.ForeColor = Theme.Muted;
        _stateDetail.Margin = new Padding(2, 0, 0, 10);

        _toggleButton.AutoSize = false;
        _toggleButton.Size = new Size(170, 34);
        _toggleButton.FlatStyle = FlatStyle.Flat;
        _toggleButton.FlatAppearance.BorderSize = 0;
        _toggleButton.ForeColor = Color.White;
        _toggleButton.BackColor = Theme.Accent;
        _toggleButton.Font = Theme.BodyStrong;
        _toggleButton.Cursor = Cursors.Hand;
        _toggleButton.Margin = new Padding(0);
        _toggleButton.Click += (_, _) => ToggleKeepAwake();

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Padding = new Padding(14, 12, 14, 14),
            Margin = new Padding(0, 0, 0, 10),
            WrapContents = false,
        };
        stack.Controls.Add(_statePill);
        stack.Controls.Add(_stateDetail);
        stack.Controls.Add(_toggleButton);
        return Card(stack);
    }

    private Control BuildStatsCard()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Padding = new Padding(14, 10, 14, 12),
            Margin = new Padding(0, 0, 0, 10),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        AddRow(table, "Since genuine interaction", _sinceInteraction);
        AddRow(table, "Total keep-awake time", _totalKeepAwake);
        AddRow(table, "Keep-awake events", _eventCount);
        AddRow(table, "Interval (randomized)", _interval);
        AddRow(table, "Next action in", _nextAction);
        AddRow(table, "Focused app", _foregroundApp);
        AddRow(table, "System wake request", _wakeLock);
        AddRow(table, "Auto-off in", _autoOff);

        return Card(table);
    }

    private Control BuildActionsRow()
    {
        var testBtn = SecondaryButton("Test action", () =>
        {
            _controller.TriggerTestAction();
            RenderStatus();
        });

        var resetBtn = SecondaryButton("Reset stats", () =>
        {
            _controller.ResetStatistics();
            RenderStatus();
        });

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            BackColor = Theme.Canvas,
            Margin = Padding.Empty,
        };
        flow.Controls.Add(testBtn);
        flow.Controls.Add(resetBtn);
        return flow;
    }

    private Control BuildSettings()
    {
        _threshold.Minimum = 1;
        _threshold.Maximum = 1440;
        _threshold.Width = 64;
        _thresholdUnit.DropDownStyle = ComboBoxStyle.DropDownList;
        _thresholdUnit.Width = 86;
        _thresholdUnit.Items.AddRange(["seconds", "minutes"]);

        _minInterval.Minimum = 15;
        _minInterval.Maximum = 3600;
        _minInterval.Width = 70;
        _minInterval.ValueChanged += (_, _) =>
        {
            if (_maxInterval.Value < _minInterval.Value) _maxInterval.Value = _minInterval.Value;
        };

        _maxInterval.Minimum = 15;
        _maxInterval.Maximum = 3600;
        _maxInterval.Width = 70;
        _maxInterval.ValueChanged += (_, _) =>
        {
            if (_minInterval.Value > _maxInterval.Value) _minInterval.Value = _maxInterval.Value;
        };

        _autoOffMinutes.Minimum = 0;
        _autoOffMinutes.Maximum = 1440;
        _autoOffMinutes.Increment = 30;
        _autoOffMinutes.Width = 70;

        Configure(_restoreCursor, "Return the cursor to its original position");
        Configure(_keepDisplayOn, "Keep the display on, not just the machine");
        Configure(_pauseFullScreen, "Pause during full-screen, game, or presentation apps");
        Configure(_startWithWindows, "Start with Windows");

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Padding = new Padding(14, 12, 14, 14),
            Margin = new Padding(0, 0, 0, 10),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));

        var thresholdFlow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        thresholdFlow.Controls.Add(_threshold);
        thresholdFlow.Controls.Add(_thresholdUnit);
        AddRow(table, "Idle before keep-awake", thresholdFlow);

        AddRow(table, "Minimum interval (s)", _minInterval);
        AddRow(table, "Maximum interval (s)", _maxInterval);
        AddRow(table, "Auto-off after (min, 0 = never)", _autoOffMinutes);

        AddFullWidth(table, _restoreCursor);
        AddFullWidth(table, _keepDisplayOn);
        AddFullWidth(table, _pauseFullScreen);
        AddFullWidth(table, _startWithWindows);

        var saveBtn = new Button
        {
            Text = "Save settings",
            AutoSize = false,
            Size = new Size(130, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Font = Theme.BodyStrong,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 10, 0, 0),
        };
        saveBtn.FlatAppearance.BorderSize = 0;
        saveBtn.Click += (_, _) => SaveSettings();
        AddFullWidth(table, saveBtn);

        _confirmLabel.AutoSize = true;
        _confirmLabel.ForeColor = Theme.ColorFor(ActivityState.Active);
        _confirmLabel.Margin = new Padding(0, 6, 0, 0);
        AddFullWidth(table, _confirmLabel);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Canvas,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(Card(table), 0, 0);
        root.Controls.Add(new Label
        {
            Text = "NotIdle keeps the session awake using the Windows power API, plus a " +
                   "single 1-pixel cursor move on a randomized interval. It never clicks, " +
                   "types, or interacts with your applications.",
            AutoSize = true,
            MaximumSize = new Size(460, 0), // wrap instead of clipping
            ForeColor = Theme.Muted,
            Margin = new Padding(2, 6, 2, 0),
        }, 0, 1);
        return root;
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private void RenderStatus()
    {
        NotIdleStatus s = _controller.GetStatus();

        _statePill.Text = s.State.ToDisplayString();
        _statePill.PillColor = Theme.ColorFor(s.State);
        _stateDetail.Text = DescribeState(s);

        _toggleButton.Text = s.Enabled ? "Turn keep-awake OFF" : "Turn keep-awake ON";
        _toggleButton.BackColor = s.Enabled ? Theme.Muted : Theme.Accent;

        _sinceInteraction.Text = FormatSpan(s.TimeSinceGenuineInput);
        _totalKeepAwake.Text = FormatSpan(s.TotalKeepAwakeDuration);
        _eventCount.Text = s.KeepAwakeSessions == 0
            ? $"{s.KeepAwakeEvents}"
            : $"{s.KeepAwakeEvents} in {s.KeepAwakeSessions} session{(s.KeepAwakeSessions == 1 ? "" : "s")}";
        _interval.Text = s.IntervalDescription;
        _nextAction.Text = s.State == ActivityState.KeepAwake
            ? FormatSpan(s.TimeUntilNextAction)
            : "—";
        _foregroundApp.Text = s.ForegroundApp;
        _wakeLock.Text = s.WakeLockHeld ? "Held" : "Released";
        _autoOff.Text = s.AutoOffArmed ? FormatSpan(s.TimeUntilAutoOff) : "never";
    }

    private static string DescribeState(NotIdleStatus s) => s.State switch
    {
        ActivityState.Active => "You're using the computer — nothing to do.",
        ActivityState.Idle => "No interaction yet; waiting for the idle threshold.",
        ActivityState.KeepAwake => s.CurrentIntervalSeconds > 0
            ? $"Holding the session awake · {s.CurrentIntervalSeconds}s interval"
            : "Holding the session awake.",
        ActivityState.Suspended => "Full-screen app in focus — display held on, no cursor action.",
        ActivityState.Locked => "Session locked — display held on, no cursor action.",
        _ => "Keep-awake is off. Normal power settings apply.",
    };

    // ── Actions ──────────────────────────────────────────────────────────────

    private void ToggleKeepAwake()
    {
        // Flip the persisted setting rather than the reported status, so the button
        // can't wedge if the two ever disagree.
        _settingsManager.Update(s => s.Enabled = !s.Enabled);
        RenderStatus();
    }

    private void SaveSettings()
    {
        Settings draft = _settingsManager.Current;
        draft.IdleThresholdSeconds = ThresholdToSeconds();
        draft.MinIntervalSeconds = (int)_minInterval.Value;
        draft.MaxIntervalSeconds = (int)_maxInterval.Value;
        draft.RestoreCursor = _restoreCursor.Checked;
        draft.KeepDisplayOn = _keepDisplayOn.Checked;
        draft.PauseWhenFullScreen = _pauseFullScreen.Checked;
        draft.StartWithWindows = _startWithWindows.Checked;
        draft.AutoOffMinutes = (int)_autoOffMinutes.Value;

        Settings applied = _settingsManager.Apply(draft);

        // Reflect back anything Normalize() clamped.
        LoadSettingsInto(applied);
        ShowConfirm($"Saved · idle after {applied.DescribeThreshold()}, every {applied.DescribeInterval()}, auto-off {applied.DescribeAutoOff()}");
    }

    private void OnSettingsChanged(Settings settings)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            LoadSettingsInto(settings);
            RenderStatus();
        });
    }

    private void LoadSettingsInto(Settings s)
    {
        LoadThreshold(s.IdleThresholdSeconds);
        _minInterval.Value = Math.Clamp(s.MinIntervalSeconds, (int)_minInterval.Minimum, (int)_minInterval.Maximum);
        _maxInterval.Value = Math.Clamp(s.MaxIntervalSeconds, (int)_maxInterval.Minimum, (int)_maxInterval.Maximum);
        _restoreCursor.Checked = s.RestoreCursor;
        _keepDisplayOn.Checked = s.KeepDisplayOn;
        _pauseFullScreen.Checked = s.PauseWhenFullScreen;
        _startWithWindows.Checked = s.StartWithWindows;
        _autoOffMinutes.Value = Math.Clamp(s.AutoOffMinutes, (int)_autoOffMinutes.Minimum, (int)_autoOffMinutes.Maximum);
    }

    // Picks minutes when the stored value is a whole number of minutes, else seconds.
    private void LoadThreshold(int totalSeconds)
    {
        bool useMinutes = totalSeconds >= 60 && totalSeconds % 60 == 0;
        _thresholdUnit.SelectedIndex = useMinutes ? 1 : 0;
        int value = useMinutes ? totalSeconds / 60 : totalSeconds;
        _threshold.Value = Math.Clamp(value, (int)_threshold.Minimum, (int)_threshold.Maximum);
    }

    private int ThresholdToSeconds()
    {
        int value = (int)_threshold.Value;
        return _thresholdUnit.SelectedIndex == 1 ? value * 60 : value;
    }

    private void ShowConfirm(string message)
    {
        _confirmLabel.Text = $"✓ {message}";
        _confirmTimer.Stop();
        _confirmTimer.Start();
    }

    // ── Small layout helpers ─────────────────────────────────────────────────

    private static Label NewValue() => new()
    {
        AutoSize = true,
        Font = Theme.BodyStrong,
        ForeColor = Theme.Ink,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 5, 0, 5),
        Text = "—",
    };

    private static void AddRow(TableLayoutPanel table, string caption, Control value)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = Theme.Muted,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 0, 5),
        }, 0, row);
        table.Controls.Add(value, 1, row);
    }

    private static void AddFullWidth(TableLayoutPanel table, Control control)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    private static void Configure(CheckBox box, string text)
    {
        box.Text = text;
        box.AutoSize = true;
        box.ForeColor = Theme.Ink;
        box.Margin = new Padding(0, 6, 0, 0);
    }

    private static Button SecondaryButton(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(110, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Surface,
            ForeColor = Theme.Ink,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 8, 0),
        };
        button.FlatAppearance.BorderColor = Theme.Border;
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Gives a panel the hairline border that makes it read as a card.</summary>
    private static Control Card(Control content)
    {
        content.Dock = DockStyle.Fill;
        content.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, content.Width - 1, content.Height - 1);
        };
        return content;
    }

    /// <summary>Compact duration: "42s", "3m 05s", "2h 14m".</summary>
    private static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes:00}m";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds:00}s";
        return $"{span.Seconds}s";
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        // Only tick while someone is looking.
        if (Visible)
        {
            RenderStatus();
            _refresh.Start();
        }
        else
        {
            _refresh.Stop();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _settingsManager.Changed -= OnSettingsChanged;
            _refresh.Dispose();
            _confirmTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
