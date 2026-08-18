using System.Text.Json.Serialization;

namespace NotIdle.Core;

/// <summary>
/// User-configurable settings. Plain mutable POCO so it serializes with the
/// source-generated <see cref="System.Text.Json"/> context (no reflection).
/// Unknown properties in an older file are ignored, so upgrades are lossless.
/// </summary>
public sealed class Settings
{
    /// <summary>Master on/off. When false the controller idles at zero CPU.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Genuine-idle time, in seconds, before keep-awake engages.</summary>
    public int IdleThresholdSeconds { get; set; } = 300; // 5 minutes

    /// <summary>Shortest gap, in seconds, between two keep-awake cursor pulses.</summary>
    public int MinIntervalSeconds { get; set; } = 45;

    /// <summary>Longest gap, in seconds, between two keep-awake cursor pulses.</summary>
    public int MaxIntervalSeconds { get; set; } = 120;

    /// <summary>Return the cursor to its exact starting position after every pulse.</summary>
    public bool RestoreCursor { get; set; } = true;

    /// <summary>Also keep the display on, not just the machine (ES_DISPLAY_REQUIRED).</summary>
    public bool KeepDisplayOn { get; set; } = true;

    /// <summary>Stand down while a full-screen / presentation / game app has focus.</summary>
    public bool PauseWhenFullScreen { get; set; } = true;

    /// <summary>Launch on Windows sign-in (managed via the HKCU Run key).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Switch keep-awake off automatically this many minutes after it is turned on.
    /// Zero means never. Guards against the real failure mode of these tools:
    /// leaving one running for weeks without noticing.
    /// </summary>
    public int AutoOffMinutes { get; set; }

    // Derived views for the engine. JsonIgnore keeps them out of settings.json —
    // they are ignored on read anyway, and persisting them just invites someone to
    // hand-edit the copy that has no effect.
    [JsonIgnore] public TimeSpan IdleThreshold => TimeSpan.FromSeconds(IdleThresholdSeconds);
    [JsonIgnore] public TimeSpan MinInterval => TimeSpan.FromSeconds(MinIntervalSeconds);
    [JsonIgnore] public TimeSpan MaxInterval => TimeSpan.FromSeconds(MaxIntervalSeconds);

    /// <summary>Defensive clamp so a hand-edited or corrupt file can't wedge the controller.</summary>
    public void Normalize()
    {
        IdleThresholdSeconds = Math.Clamp(IdleThresholdSeconds, 10, 24 * 60 * 60);
        MinIntervalSeconds = Math.Clamp(MinIntervalSeconds, 15, 60 * 60);
        MaxIntervalSeconds = Math.Clamp(MaxIntervalSeconds, MinIntervalSeconds, 60 * 60);
        AutoOffMinutes = Math.Clamp(AutoOffMinutes, 0, 24 * 60);
    }

    /// <summary>Human-friendly threshold, e.g. "45s", "5 min", or "1m 30s".</summary>
    public string DescribeThreshold() => FormatDuration(IdleThresholdSeconds);

    /// <summary>Human-friendly interval range, e.g. "45–120s".</summary>
    public string DescribeInterval() => MinIntervalSeconds == MaxIntervalSeconds
        ? FormatDuration(MinIntervalSeconds)
        : $"{MinIntervalSeconds}–{MaxIntervalSeconds}s";

    /// <summary>Human-friendly auto-off setting, e.g. "2h", "90 min", or "never".</summary>
    public string DescribeAutoOff()
    {
        if (AutoOffMinutes <= 0) return "never";
        if (AutoOffMinutes % 60 != 0) return $"{AutoOffMinutes} min";
        int hours = AutoOffMinutes / 60;
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }

    public static string FormatDuration(int seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        int m = seconds / 60, s = seconds % 60;
        return s == 0 ? $"{m} min" : $"{m}m {s}s";
    }

    public Settings Clone() => new()
    {
        Enabled = Enabled,
        IdleThresholdSeconds = IdleThresholdSeconds,
        MinIntervalSeconds = MinIntervalSeconds,
        MaxIntervalSeconds = MaxIntervalSeconds,
        RestoreCursor = RestoreCursor,
        KeepDisplayOn = KeepDisplayOn,
        PauseWhenFullScreen = PauseWhenFullScreen,
        StartWithWindows = StartWithWindows,
        AutoOffMinutes = AutoOffMinutes,
    };
}
