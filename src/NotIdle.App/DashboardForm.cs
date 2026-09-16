using System.Drawing;
using System.Windows.Forms;
using NotIdle.Core;
using NotIdle.Core.Actions;

namespace NotIdle.App;

/// <summary>
/// The one window: a live dashboard, a pane for the three keep-awake actions, and
/// a pane for everything else.
///
/// It holds no logic of its own — it renders <see cref="NotIdleController.GetStatus"/>
/// snapshots and routes edits through <see cref="SettingsManager"/>. The refresh
/// timer only runs while the window is visible, so the app stays quiet in the tray.
/// </summary>
internal sealed class DashboardForm : Form
{
    private const int RefreshIntervalMs = 500;

    /// <summary>
    /// Fixed width for the cards inside the flow panel, which gives its children no
    /// width of their own. Sized to clear the vertical scrollbar the pane always has.
    /// </summary>
    private const int CardWidth = 478;

    private static readonly PathPattern[] Patterns =
        [PathPattern.Circle, PathPattern.Square, PathPattern.FigureEight, PathPattern.RandomWalk, PathPattern.Custom];

    private static readonly string[] PatternNames =
        ["Circle", "Square", "Figure eight", "Random walk", "Custom points"];

    private static readonly PathOrigin[] Origins = [PathOrigin.Cursor, PathOrigin.ScreenCenter];
    private static readonly string[] OriginNames = ["Around the cursor", "Around screen centre"];

    private static readonly MouseButton[] Buttons = [MouseButton.Left, MouseButton.Right, MouseButton.Middle];
    private static readonly string[] ButtonNames = ["Left", "Right", "Middle"];

    private readonly NotIdleController _controller;
    private readonly SettingsManager _settingsManager;
    private readonly System.Windows.Forms.Timer _refresh = new();
    private readonly System.Windows.Forms.Timer _confirmTimer = new();

    // Dashboard
    private readonly StatePill _statePill = new();
    private readonly Label _stateDetail = new();
    private readonly Label _adaptiveNote = new();
    private readonly Button _toggleButton = new();
    private readonly Label _sinceInteraction = NewValue();
    private readonly Label _totalKeepAwake = NewValue();
    private readonly Label _eventCount = NewValue();
    private readonly Label _interval = NewValue();
    private readonly Label _nextAction = NewValue();
    private readonly Label _activeActions = NewValue();
    private readonly Label _pathStatus = NewValue();
    private readonly Label _clickStatus = NewValue();
    private readonly Label _foregroundApp = NewValue();
    private readonly Label _wakeLock = NewValue();
    private readonly Label _autoOff = NewValue();

    // Actions — cursor pulse
    private readonly CheckBox _pulseEnabled = new();
    private readonly NumericUpDown _minInterval = new();
    private readonly NumericUpDown _maxInterval = new();
    private readonly CheckBox _restoreCursor = new();

    // Actions — path movement
    private readonly CheckBox _pathEnabled = new();
    private readonly ComboBox _pathPattern = new();
    private readonly ComboBox _pathOrigin = new();
    private readonly NumericUpDown _pathRadius = new();
    private readonly NumericUpDown _pathSpeed = new();
    private readonly NumericUpDown _pathMinInterval = new();
    private readonly NumericUpDown _pathMaxInterval = new();
    private readonly CheckBox _pathRestoreCursor = new();
    private readonly TextBox _pathPoints = new();
    private readonly Label _pathPointsCaption = new();

    // Actions — auto-click
    private readonly CheckBox _autoClickEnabled = new();
    private readonly ComboBox _autoClickButton = new();
    private readonly NumericUpDown _autoClickInterval = new();
    private readonly CheckBox _autoClickDouble = new();
    private readonly TextBox _autoClickPoints = new();
    private readonly Label _autoClickPointsCaption = new();
    private readonly NumericUpDown _autoClickTravelSpeed = new();

    // Settings
    private readonly CheckBox _adaptiveMode = new();
    private readonly NumericUpDown _threshold = new();
    private readonly ComboBox _thresholdUnit = new();
    private readonly NumericUpDown _autoOffMinutes = new();
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
        // Tall enough for the dashboard's eleven stat rows *and* the test buttons
        // beneath them; the Actions pane scrolls, so it sets no floor of its own.
        ClientSize = new Size(544, 724);
        BackColor = Theme.Canvas;
        Font = Theme.Body;

        Controls.Add(BuildRoot());

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

    // Save lives outside the tabs, because settings from both panes are written in
    // one go and a per-tab button would imply otherwise.
    private Control BuildRoot()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Canvas,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildTabs(), 0, 0);
        root.Controls.Add(BuildSaveBar(), 0, 1);
        return root;
    }

    private TabControl BuildTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };

        var dashboard = new TabPage("Dashboard") { BackColor = Theme.Canvas, Padding = new Padding(14) };
        dashboard.Controls.Add(BuildDashboard());

        var actions = new TabPage("Actions") { BackColor = Theme.Canvas, Padding = new Padding(14) };
        actions.Controls.Add(BuildActions());

        var settings = new TabPage("Settings") { BackColor = Theme.Canvas, Padding = new Padding(14) };
        settings.Controls.Add(BuildSettings());

        tabs.TabPages.Add(dashboard);
        tabs.TabPages.Add(actions);
        tabs.TabPages.Add(settings);
        return tabs;
    }

    private Control BuildSaveBar()
    {
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
            Margin = new Padding(14, 8, 10, 10),
        };
        saveBtn.FlatAppearance.BorderSize = 0;
        saveBtn.Click += (_, _) => SaveSettings();

        _confirmLabel.AutoSize = true;
        _confirmLabel.MaximumSize = new Size(370, 0);
        _confirmLabel.ForeColor = Theme.ColorFor(ActivityState.Active);
        _confirmLabel.Margin = new Padding(0, 12, 0, 0);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Canvas,
            Margin = Padding.Empty,
            WrapContents = false,
        };
        bar.Controls.Add(saveBtn);
        bar.Controls.Add(_confirmLabel);
        return bar;
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
        _stateDetail.MaximumSize = new Size(CardWidth - 40, 0);
        _stateDetail.ForeColor = Theme.Muted;
        _stateDetail.Margin = new Padding(2, 0, 0, 2);

        _adaptiveNote.AutoSize = true;
        _adaptiveNote.MaximumSize = new Size(CardWidth - 40, 0);
        _adaptiveNote.ForeColor = Theme.Accent;
        _adaptiveNote.Margin = new Padding(2, 0, 0, 10);

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
        stack.Controls.Add(_adaptiveNote);
        stack.Controls.Add(_toggleButton);
        return Card(stack);
    }

    private Control BuildStatsCard()
    {
        // A wider value column than the settings panes use: these rows carry live
        // sentences ("3 runs, 1 cut short · next in 40s"), not single numbers.
        TableLayoutPanel table = TwoColumnTable(captionPercent: 44);

        AddRow(table, "Since genuine interaction", _sinceInteraction);
        AddRow(table, "Total keep-awake time", _totalKeepAwake);
        AddRow(table, "Active actions", _activeActions);
        AddRow(table, "Cursor pulses", _eventCount);
        AddRow(table, "Pulse interval (randomized)", _interval);
        AddRow(table, "Next pulse in", _nextAction);
        AddRow(table, "Path movement", _pathStatus);
        AddRow(table, "Auto-click", _clickStatus);
        AddRow(table, "Focused app", _foregroundApp);
        AddRow(table, "System wake request", _wakeLock);
        AddRow(table, "Auto-off in", _autoOff);

        return Card(table);
    }

    private Control BuildActionsRow()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            BackColor = Theme.Canvas,
            Margin = Padding.Empty,
        };

        flow.Controls.Add(SecondaryButton("Test pulse", () =>
        {
            _controller.TriggerTestAction();
            RenderStatus();
        }));

        flow.Controls.Add(SecondaryButton("Test path", () =>
        {
            // Runs against the saved configuration, not the unsaved edits — testing
            // something other than what the app will actually do is worse than useless.
            if (!_controller.TriggerTestPath())
                ShowConfirm("No path to run — enable a pattern in Actions and save first.");
            RenderStatus();
        }));

        flow.Controls.Add(SecondaryButton("Test click", () =>
        {
            _controller.TriggerTestClick();
            RenderStatus();
        }));

        flow.Controls.Add(SecondaryButton("Reset stats", () =>
        {
            _controller.ResetStatistics();
            RenderStatus();
        }));

        return flow;
    }

    private Control BuildActions()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoScroll = true,
            WrapContents = false,
            BackColor = Theme.Canvas,
            Margin = Padding.Empty,
        };

        flow.Controls.Add(BuildPulseCard());
        flow.Controls.Add(BuildPathCard());
        flow.Controls.Add(BuildClickCard());
        flow.Controls.Add(new Label
        {
            Text = "The three actions run on independent schedules, so any combination " +
                   "of them can be in flight at the same time. All of them stand down " +
                   "the moment you touch the mouse or keyboard.",
            AutoSize = true,
            MaximumSize = new Size(CardWidth, 0),
            ForeColor = Theme.Muted,
            Margin = new Padding(2, 4, 2, 8),
        });
        return flow;
    }

    private Control BuildPulseCard()
    {
        Configure(_pulseEnabled, "Cursor pulse — one imperceptible 1 px move");
        _pulseEnabled.Font = Theme.BodyStrong;
        _pulseEnabled.CheckedChanged += (_, _) => SyncEnabledState();

        ConfigureSpinner(_minInterval, 15, 3600, 70);
        ConfigureSpinner(_maxInterval, 15, 3600, 70);
        _minInterval.ValueChanged += (_, _) =>
        {
            if (_maxInterval.Value < _minInterval.Value) _maxInterval.Value = _minInterval.Value;
        };
        _maxInterval.ValueChanged += (_, _) =>
        {
            if (_minInterval.Value > _maxInterval.Value) _minInterval.Value = _maxInterval.Value;
        };

        Configure(_restoreCursor, "Return the cursor to its original position");

        TableLayoutPanel table = TwoColumnTable();
        AddFullWidth(table, _pulseEnabled);
        AddRow(table, "Minimum interval (s)", _minInterval);
        AddRow(table, "Maximum interval (s)", _maxInterval);
        AddFullWidth(table, _restoreCursor);
        return SizedCard(table);
    }

    private Control BuildPathCard()
    {
        Configure(_pathEnabled, "Path movement — trace a shape with the pointer");
        _pathEnabled.Font = Theme.BodyStrong;
        _pathEnabled.CheckedChanged += (_, _) => SyncEnabledState();

        ConfigureCombo(_pathPattern, PatternNames, 150);
        _pathPattern.SelectedIndexChanged += (_, _) => SyncEnabledState();
        ConfigureCombo(_pathOrigin, OriginNames, 170);

        ConfigureSpinner(_pathRadius, Settings.MinPathRadius, Settings.MaxPathRadius, 70);
        ConfigureSpinner(_pathSpeed, Settings.MinPathSpeed, Settings.MaxPathSpeed, 70);
        _pathSpeed.Increment = 50;
        ConfigureSpinner(_pathMinInterval, 10, 3600, 70);
        ConfigureSpinner(_pathMaxInterval, 10, 3600, 70);
        _pathMinInterval.ValueChanged += (_, _) =>
        {
            if (_pathMaxInterval.Value < _pathMinInterval.Value) _pathMaxInterval.Value = _pathMinInterval.Value;
        };
        _pathMaxInterval.ValueChanged += (_, _) =>
        {
            if (_pathMinInterval.Value > _pathMaxInterval.Value) _pathMinInterval.Value = _pathMaxInterval.Value;
        };

        Configure(_pathRestoreCursor, "Put the cursor back when the run finishes");

        _pathPoints.Multiline = true;
        _pathPoints.Height = 52;
        _pathPoints.Width = 210;
        _pathPoints.ScrollBars = ScrollBars.Vertical;
        _pathPoints.Margin = new Padding(0, 4, 0, 4);

        _pathPointsCaption.Text = "Points (x,y per pair)";
        _pathPointsCaption.AutoSize = true;
        _pathPointsCaption.ForeColor = Theme.Muted;
        _pathPointsCaption.Anchor = AnchorStyles.Left;
        _pathPointsCaption.Margin = new Padding(0, 5, 0, 5);

        TableLayoutPanel table = TwoColumnTable();
        AddFullWidth(table, _pathEnabled);
        AddRow(table, "Pattern", _pathPattern);
        AddRow(table, "Centred on", _pathOrigin);
        AddRow(table, "Size / radius (px)", _pathRadius);
        AddRow(table, "Speed (px per second)", _pathSpeed);
        AddRow(table, "Minimum interval (s)", _pathMinInterval);
        AddRow(table, "Maximum interval (s)", _pathMaxInterval);
        AddRow(table, _pathPointsCaption, _pathPoints);
        AddFullWidth(table, _pathRestoreCursor);
        AddFullWidth(table, Hint("Custom points are absolute screen coordinates, e.g. " +
                                 "\"200,300  900,450  600,800\". Anything outside the desktop is clamped."));
        return SizedCard(table);
    }

    private Control BuildClickCard()
    {
        Configure(_autoClickEnabled, "Auto-click — press a button on an interval");
        _autoClickEnabled.Font = Theme.BodyStrong;
        _autoClickEnabled.CheckedChanged += (_, _) => SyncEnabledState();

        ConfigureCombo(_autoClickButton, ButtonNames, 100);
        ConfigureSpinner(_autoClickInterval, Settings.MinClickIntervalMs, Settings.MaxClickIntervalMs, 90);
        _autoClickInterval.Increment = 100;

        Configure(_autoClickDouble, "Send a double click");

        _autoClickPoints.Multiline = true;
        _autoClickPoints.Height = 52;
        _autoClickPoints.Width = 210;
        _autoClickPoints.ScrollBars = ScrollBars.Vertical;
        _autoClickPoints.Margin = new Padding(0, 4, 0, 4);
        _autoClickPoints.TextChanged += (_, _) => SyncEnabledState();

        _autoClickPointsCaption.Text = "Click points (x,y per pair)";
        _autoClickPointsCaption.AutoSize = true;
        _autoClickPointsCaption.ForeColor = Theme.Muted;
        _autoClickPointsCaption.Anchor = AnchorStyles.Left;
        _autoClickPointsCaption.Margin = new Padding(0, 5, 0, 5);

        ConfigureSpinner(_autoClickTravelSpeed, Settings.MinPathSpeed, Settings.MaxPathSpeed, 70);
        _autoClickTravelSpeed.Increment = 50;

        TableLayoutPanel table = TwoColumnTable();
        AddFullWidth(table, _autoClickEnabled);
        AddRow(table, "Button", _autoClickButton);
        AddRow(table, "Interval (ms)", _autoClickInterval);
        AddRow(table, _autoClickPointsCaption, _autoClickPoints);
        AddRow(table, "Travel speed (px per second)", _autoClickTravelSpeed);
        AddFullWidth(table, _autoClickDouble);
        AddFullWidth(table, Hint("Leave the points empty to click wherever the pointer already is. " +
                                 "Give it a list and it cycles them: travel to the next point at the " +
                                 "speed above, click, wait the interval, move on."));
        AddFullWidth(table, Hint("Clicks land in whatever is under the pointer, so this one only " +
                                 "runs once the idle threshold has passed, and stops the instant " +
                                 "you move the mouse yourself. NotIdle never turns it on by itself."));
        return SizedCard(table);
    }

    private Control BuildSettings()
    {
        ConfigureSpinner(_threshold, 1, 1440, 64);
        ConfigureCombo(_thresholdUnit, ["seconds", "minutes"], 86);

        ConfigureSpinner(_autoOffMinutes, 0, 1440, 70);
        _autoOffMinutes.Increment = 30;

        Configure(_adaptiveMode, "Let NotIdle choose the actions and cadence for itself");
        Configure(_keepDisplayOn, "Keep the display on, not just the machine");
        Configure(_pauseFullScreen, "Pause during full-screen, game, or presentation apps");
        Configure(_startWithWindows, "Start with Windows");

        TableLayoutPanel table = TwoColumnTable();

        var thresholdFlow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        thresholdFlow.Controls.Add(_threshold);
        thresholdFlow.Controls.Add(_thresholdUnit);
        AddRow(table, "Idle before keep-awake", thresholdFlow);
        AddRow(table, "Auto-off after (min, 0 = never)", _autoOffMinutes);

        AddFullWidth(table, _adaptiveMode);
        AddFullWidth(table, Hint("Adaptive mode checks whether NotIdle's own input actually " +
                                 "reaches the OS. If pulses are being swallowed it adds path " +
                                 "movement and shortens the interval; if they land reliably it " +
                                 "quietens down again. It never enables clicking."));
        AddFullWidth(table, _keepDisplayOn);
        AddFullWidth(table, _pauseFullScreen);
        AddFullWidth(table, _startWithWindows);

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
            Text = "NotIdle keeps the session awake using the Windows power API, plus " +
                   "whichever cursor actions you enable. It never synthesizes keystrokes.",
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
        _adaptiveNote.Text = s.AdaptiveMode && s.State == ActivityState.KeepAwake ? s.AdaptiveNote : string.Empty;

        _toggleButton.Text = s.Enabled ? "Turn keep-awake OFF" : "Turn keep-awake ON";
        _toggleButton.BackColor = s.Enabled ? Theme.Muted : Theme.Accent;

        _sinceInteraction.Text = FormatSpan(s.TimeSinceGenuineInput);
        _totalKeepAwake.Text = FormatSpan(s.TotalKeepAwakeDuration);
        _activeActions.Text = s.ActionsDescription;
        _eventCount.Text = s.KeepAwakeSessions == 0
            ? $"{s.KeepAwakeEvents}"
            : $"{s.KeepAwakeEvents} in {s.KeepAwakeSessions} session{(s.KeepAwakeSessions == 1 ? "" : "s")}";
        _interval.Text = s.IntervalDescription;
        _nextAction.Text = s.State == ActivityState.KeepAwake
            ? FormatSpan(s.TimeUntilNextAction)
            : "—";
        _pathStatus.Text = DescribePath(s);
        _clickStatus.Text = DescribeClicks(s);
        _foregroundApp.Text = s.ForegroundApp;
        _wakeLock.Text = s.WakeLockHeld ? "Held" : "Released";
        _autoOff.Text = s.AutoOffArmed ? FormatSpan(s.TimeUntilAutoOff) : "never";
    }

    // These two rows report live state; the configuration behind them lives on the
    // Actions tab, so naming it again here would only cost the column its width.
    private static string DescribePath(NotIdleStatus s)
    {
        bool configured = s.PathDescription != "off";
        if (!configured && s.PathRuns == 0) return "off";

        string runs = $"{s.PathRuns} run{Plural(s.PathRuns)}";
        if (s.PathAbortedRuns > 0) runs += $", {s.PathAbortedRuns} cut short";

        if (s.PathRunning) return $"{runs} · tracing now";
        if (!configured) return runs;

        return s.State == ActivityState.KeepAwake
            ? $"{runs} · next in {FormatSpan(s.TimeUntilNextPath)}"
            : $"{runs} · armed";
    }

    private static string DescribeClicks(NotIdleStatus s)
    {
        bool configured = s.AutoClickDescription != "off";
        if (!configured && s.ClickCount == 0) return "off";

        string clicks = $"{s.ClickCount} click{Plural(s.ClickCount)}";
        if (s.ClickInterruptions > 0) clicks += $", stood down {s.ClickInterruptions}×";

        if (s.AutoClickActive) return $"{clicks} · firing";
        return configured ? $"{clicks} · armed" : clicks;
    }

    private static string Plural(long count) => count == 1 ? "" : "s";

    private static string DescribeState(NotIdleStatus s) => s.State switch
    {
        ActivityState.Active => "You're using the computer — nothing to do.",
        ActivityState.Idle => "No interaction yet; waiting for the idle threshold.",
        ActivityState.KeepAwake => s.CurrentIntervalSeconds > 0
            ? $"Holding the session awake · {s.ActionsDescription} · {s.CurrentIntervalSeconds}s interval"
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
        draft.AutoOffMinutes = (int)_autoOffMinutes.Value;
        draft.AdaptiveMode = _adaptiveMode.Checked;
        draft.KeepDisplayOn = _keepDisplayOn.Checked;
        draft.PauseWhenFullScreen = _pauseFullScreen.Checked;
        draft.StartWithWindows = _startWithWindows.Checked;

        draft.CursorPulseEnabled = _pulseEnabled.Checked;
        draft.MinIntervalSeconds = (int)_minInterval.Value;
        draft.MaxIntervalSeconds = (int)_maxInterval.Value;
        draft.RestoreCursor = _restoreCursor.Checked;

        draft.PathEnabled = _pathEnabled.Checked;
        draft.PathPattern = Patterns[Math.Max(0, _pathPattern.SelectedIndex)];
        draft.PathOrigin = Origins[Math.Max(0, _pathOrigin.SelectedIndex)];
        draft.PathRadius = (int)_pathRadius.Value;
        draft.PathSpeedPixelsPerSecond = (int)_pathSpeed.Value;
        draft.PathMinIntervalSeconds = (int)_pathMinInterval.Value;
        draft.PathMaxIntervalSeconds = (int)_pathMaxInterval.Value;
        draft.PathRestoreCursor = _pathRestoreCursor.Checked;
        draft.PathCustomPoints = _pathPoints.Text;

        draft.AutoClickEnabled = _autoClickEnabled.Checked;
        draft.AutoClickButton = Buttons[Math.Max(0, _autoClickButton.SelectedIndex)];
        draft.AutoClickIntervalMs = (int)_autoClickInterval.Value;
        draft.AutoClickDoubleClick = _autoClickDouble.Checked;
        draft.AutoClickPoints = _autoClickPoints.Text;
        draft.AutoClickTravelSpeed = (int)_autoClickTravelSpeed.Value;

        bool wantedPath = _pathEnabled.Checked;
        bool typedClickPoints = _autoClickPoints.Text.Trim().Length > 0;
        Settings applied = _settingsManager.Apply(draft);

        // Reflect back anything Normalize() clamped or refused.
        LoadSettingsInto(applied);

        ShowConfirm(Outcome(applied, wantedPath, typedClickPoints));
    }

    // Normalize() silently drops configuration it cannot use, which would otherwise
    // look like the save having been ignored. Say which part went and why.
    private static string Outcome(Settings applied, bool wantedPath, bool typedClickPoints)
    {
        if (wantedPath && !applied.PathEnabled)
            return "Saved, but path movement was switched off — the custom list needs at least two x,y points.";

        if (typedClickPoints && applied.AutoClickPoints.Length == 0)
            return "Saved, but the click points were dropped — none of them parsed as an x,y pair.";

        return $"Saved · {applied.DescribeActions()} after {applied.DescribeThreshold()} idle, auto-off {applied.DescribeAutoOff()}";
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
        SetValue(_autoOffMinutes, s.AutoOffMinutes);
        _adaptiveMode.Checked = s.AdaptiveMode;
        _keepDisplayOn.Checked = s.KeepDisplayOn;
        _pauseFullScreen.Checked = s.PauseWhenFullScreen;
        _startWithWindows.Checked = s.StartWithWindows;

        _pulseEnabled.Checked = s.CursorPulseEnabled;
        SetValue(_minInterval, s.MinIntervalSeconds);
        SetValue(_maxInterval, s.MaxIntervalSeconds);
        _restoreCursor.Checked = s.RestoreCursor;

        _pathEnabled.Checked = s.PathEnabled;
        _pathPattern.SelectedIndex = Math.Max(0, Array.IndexOf(Patterns, s.PathPattern));
        _pathOrigin.SelectedIndex = Math.Max(0, Array.IndexOf(Origins, s.PathOrigin));
        SetValue(_pathRadius, s.PathRadius);
        SetValue(_pathSpeed, s.PathSpeedPixelsPerSecond);
        SetValue(_pathMinInterval, s.PathMinIntervalSeconds);
        SetValue(_pathMaxInterval, s.PathMaxIntervalSeconds);
        _pathRestoreCursor.Checked = s.PathRestoreCursor;
        _pathPoints.Text = s.PathCustomPoints;

        _autoClickEnabled.Checked = s.AutoClickEnabled;
        _autoClickButton.SelectedIndex = Math.Max(0, Array.IndexOf(Buttons, s.AutoClickButton));
        SetValue(_autoClickInterval, s.AutoClickIntervalMs);
        _autoClickDouble.Checked = s.AutoClickDoubleClick;
        _autoClickPoints.Text = s.AutoClickPoints;
        SetValue(_autoClickTravelSpeed, s.AutoClickTravelSpeed);

        SyncEnabledState();
    }

    /// <summary>Grey out the rows an unticked action owns, so the pane reads at a glance.</summary>
    private void SyncEnabledState()
    {
        bool pulse = _pulseEnabled.Checked;
        _minInterval.Enabled = _maxInterval.Enabled = _restoreCursor.Enabled = pulse;

        bool path = _pathEnabled.Checked;
        bool custom = _pathPattern.SelectedIndex == Array.IndexOf(Patterns, PathPattern.Custom);
        _pathPattern.Enabled = _pathOrigin.Enabled = _pathSpeed.Enabled = path;
        _pathMinInterval.Enabled = _pathMaxInterval.Enabled = _pathRestoreCursor.Enabled = path;
        // Radius and origin shape a generated pattern; a custom list carries its own.
        _pathRadius.Enabled = path && !custom;
        _pathOrigin.Enabled = path && !custom;
        _pathPoints.Enabled = _pathPointsCaption.Enabled = path && custom;

        bool click = _autoClickEnabled.Checked;
        _autoClickButton.Enabled = _autoClickInterval.Enabled = _autoClickDouble.Enabled = click;
        _autoClickPoints.Enabled = _autoClickPointsCaption.Enabled = click;
        // Travel speed only means anything when there is somewhere to travel to.
        _autoClickTravelSpeed.Enabled = click && _autoClickPoints.Text.Trim().Length > 0;
    }

    // Picks minutes when the stored value is a whole number of minutes, else seconds.
    private void LoadThreshold(int totalSeconds)
    {
        bool useMinutes = totalSeconds >= 60 && totalSeconds % 60 == 0;
        _thresholdUnit.SelectedIndex = useMinutes ? 1 : 0;
        SetValue(_threshold, useMinutes ? totalSeconds / 60 : totalSeconds);
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

    private static TableLayoutPanel TwoColumnTable(int captionPercent = 52)
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
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, captionPercent));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 - captionPercent));
        return table;
    }

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(CardWidth - 40, 0),
        ForeColor = Theme.Muted,
        Margin = new Padding(0, 6, 0, 2),
    };

    private static void AddRow(TableLayoutPanel table, string caption, Control value) =>
        AddRow(table, new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = Theme.Muted,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 0, 5),
        }, value);

    private static void AddRow(TableLayoutPanel table, Control caption, Control value)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(caption, 0, row);
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

    private static void ConfigureSpinner(NumericUpDown spinner, int min, int max, int width)
    {
        spinner.Minimum = min;
        spinner.Maximum = max;
        spinner.Width = width;
        spinner.Anchor = AnchorStyles.Left;
        spinner.Margin = new Padding(0, 4, 0, 4);
    }

    private static void ConfigureCombo(ComboBox combo, string[] items, int width)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Width = width;
        combo.Anchor = AnchorStyles.Left;
        combo.Margin = new Padding(0, 4, 0, 4);
        combo.Items.AddRange(items);
    }

    // Clamping here rather than at the call site keeps a settings file written by a
    // newer build from throwing when it lands in an older spinner's range.
    private static void SetValue(NumericUpDown spinner, int value) =>
        spinner.Value = Math.Clamp(value, (int)spinner.Minimum, (int)spinner.Maximum);

    private static Button SecondaryButton(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(102, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Surface,
            ForeColor = Theme.Ink,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 6, 0),
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

    /// <summary>A card for a flow panel, which gives its children no width of their own.</summary>
    private static Control SizedCard(Control content)
    {
        Control card = Card(content);
        card.Dock = DockStyle.None;
        // Pinning both bounds rather than Width: these panels auto-size, so a card
        // carrying a long hint would otherwise settle wider than one without, and
        // the column of cards would come out visibly ragged.
        card.MinimumSize = new Size(CardWidth, 0);
        card.MaximumSize = new Size(CardWidth, 0);
        return card;
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
