using System.Drawing;
using System.Windows.Forms;
using NotIdle.Core;

namespace NotIdle.App;

internal sealed class ControlPanelForm : Form
{
    private readonly IdleEngine _engine;
    private readonly ISettingsStore _store;
    private readonly Control _marshaller = new();
    private readonly System.Windows.Forms.Timer _confirmTimer = new();

    private readonly Label _statusLabel;
    private readonly Label _confirmLabel;
    private readonly NumericUpDown _threshold;
    private readonly ComboBox _thresholdUnit;
    private readonly NumericUpDown _duration;
    private readonly Button _startBtn;
    private readonly Button _stopBtn;
    private readonly Button _testBtn;

    private Settings _settings;

    public ControlPanelForm(IdleEngine engine, ISettingsStore store, Settings settings)
    {
        _engine = engine;
        _store = store;
        _settings = settings;

        _ = _marshaller.Handle;

        Text = "NotIdle";
        Icon = TrayIconFactory.LoadAppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(380, 240);

        _statusLabel = new Label
        {
            Text = $"Status: {FormatState(_engine.CurrentState)}",
            AutoSize = true,
            Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };

        _confirmLabel = new Label
        {
            Text = "",
            AutoSize = true,
            ForeColor = Color.Green,
            Margin = new Padding(0, 4, 0, 0),
        };

        _threshold = new NumericUpDown { Minimum = 1, Maximum = 86400, Width = 70 };
        _thresholdUnit = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 90,
        };
        _thresholdUnit.Items.AddRange(["seconds", "minutes"]);
        LoadThresholdInto(_settings.IdleThresholdSeconds);

        _duration = new NumericUpDown
        {
            Minimum = 1, Maximum = 3600,
            Value = Math.Clamp(_settings.ActionDurationSeconds, 1, 3600),
            Width = 80,
        };

        _startBtn = new Button { Text = "Start", AutoSize = true, FlatStyle = FlatStyle.Flat };
        _startBtn.Click += (_, _) => StartEngine();

        _stopBtn = new Button { Text = "Stop", AutoSize = true, FlatStyle = FlatStyle.Flat };
        _stopBtn.Click += (_, _) => StopEngine();

        _testBtn = new Button { Text = "Test", AutoSize = true, FlatStyle = FlatStyle.Flat };
        _testBtn.Click += (_, _) => { _engine.TestNudge(); ShowConfirm("Test nudge done"); };

        _confirmTimer.Interval = 3000;
        _confirmTimer.Tick += (_, _) => { _confirmLabel.Text = ""; _confirmTimer.Stop(); };

        BuildLayout();

        _engine.StateChanged += OnStateChanged;
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private void BuildLayout()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 6,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        table.Controls.Add(_statusLabel, 0, 0);
        table.SetColumnSpan(_statusLabel, 2);

        table.Controls.Add(new Label { Text = "Idle threshold", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        var thresholdFlow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = Padding.Empty,
        };
        thresholdFlow.Controls.Add(_threshold);
        thresholdFlow.Controls.Add(_thresholdUnit);
        table.Controls.Add(thresholdFlow, 1, 1);

        table.Controls.Add(new Label { Text = "Action duration (s)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        table.Controls.Add(_duration, 1, 2);

        var applyBtn = new Button { Text = "Apply", AutoSize = true, FlatStyle = FlatStyle.Flat };
        applyBtn.Click += (_, _) => SaveSettings();

        var btnFlow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        btnFlow.Controls.Add(_startBtn);
        btnFlow.Controls.Add(_stopBtn);
        btnFlow.Controls.Add(applyBtn);
        btnFlow.Controls.Add(_testBtn);

        table.Controls.Add(btnFlow, 0, 3);
        table.SetColumnSpan(btnFlow, 2);

        table.Controls.Add(_confirmLabel, 0, 4);
        table.SetColumnSpan(_confirmLabel, 2);

        Controls.Add(table);
    }

    private void StartEngine()
    {
        _settings.Enabled = true;
        _store.Save(_settings);
        _engine.ApplySettings(_settings);
        ShowConfirm("NotIdle started");
    }

    private void StopEngine()
    {
        _settings.Enabled = false;
        _store.Save(_settings);
        _engine.ApplySettings(_settings);
        ShowConfirm("NotIdle stopped");
    }

    private void SaveSettings()
    {
        _settings.IdleThresholdSeconds = ThresholdToSeconds();
        _settings.ActionDurationSeconds = (int)_duration.Value;
        _settings.Enabled = true;
        _settings.Normalize();
        _store.Save(_settings);
        _engine.ApplySettings(_settings);

        // Reflect back any clamping the engine applied.
        LoadThresholdInto(_settings.IdleThresholdSeconds);
        _duration.Value = _settings.ActionDurationSeconds;

        ShowConfirm($"Applied: idle after {_settings.DescribeThreshold()}, act {_settings.DescribeAction()}");
    }

    // Picks minutes when the stored value is a whole number of minutes, else seconds.
    private void LoadThresholdInto(int totalSeconds)
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
        _confirmLabel.Text = $"\u2713 {message}";
        _confirmTimer.Stop();
        _confirmTimer.Start();
    }

    private void OnStateChanged(AppState state)
    {
        if (_marshaller.IsHandleCreated)
            _marshaller.BeginInvoke(() => _statusLabel.Text = $"Status: {FormatState(state)}");
    }

    private static string FormatState(AppState state) => state switch
    {
        AppState.Monitoring => "Monitoring",
        AppState.Idle => "Idle (counting down)",
        AppState.ActionRunning => "Action running",
        _ => "Paused",
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.StateChanged -= OnStateChanged;
            _confirmTimer.Dispose();
            _marshaller.Dispose();
        }
        base.Dispose(disposing);
    }
}
