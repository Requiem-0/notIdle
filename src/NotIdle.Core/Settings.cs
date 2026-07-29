namespace NotIdle.Core;

/// <summary>
/// User-configurable settings. Plain mutable POCO so it serializes with the
/// source-generated <see cref="System.Text.Json"/> context (no reflection).
/// </summary>
public sealed class Settings
{
    /// <summary>Master on/off. When false the engine idles at zero CPU.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Idle time, in seconds, before the action fires.</summary>
    public int IdleThresholdSeconds { get; set; } = 300; // 5 minutes

    /// <summary>How long, in seconds, the mouse action runs once triggered.</summary>
    public int ActionDurationSeconds { get; set; } = 5;

    /// <summary>Launch on Windows sign-in (managed via the HKCU Run key).</summary>
    public bool StartWithWindows { get; set; }

    public TimeSpan IdleThreshold => TimeSpan.FromSeconds(Math.Max(1, IdleThresholdSeconds));
    public TimeSpan ActionDuration => TimeSpan.FromSeconds(Math.Max(1, ActionDurationSeconds));

    /// <summary>Defensive clamp so a hand-edited/corrupt file can't wedge the engine.</summary>
    public void Normalize()
    {
        IdleThresholdSeconds = Math.Clamp(IdleThresholdSeconds, 3, 24 * 60 * 60);
        ActionDurationSeconds = Math.Clamp(ActionDurationSeconds, 1, 60 * 60);
    }

    /// <summary>Human-friendly threshold, e.g. "45s", "5 min", or "1m 30s".</summary>
    public string DescribeThreshold() => FormatDuration(IdleThresholdSeconds);

    /// <summary>Human-friendly action duration.</summary>
    public string DescribeAction() => FormatDuration(ActionDurationSeconds);

    private static string FormatDuration(int seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        int m = seconds / 60, s = seconds % 60;
        return s == 0 ? $"{m} min" : $"{m}m {s}s";
    }

    public Settings Clone() => new()
    {
        Enabled = Enabled,
        IdleThresholdSeconds = IdleThresholdSeconds,
        ActionDurationSeconds = ActionDurationSeconds,
        StartWithWindows = StartWithWindows,
    };
}
