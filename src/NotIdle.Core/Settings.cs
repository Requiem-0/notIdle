using System.Text.Json.Serialization;
using NotIdle.Core.Actions;

namespace NotIdle.Core;

/// <summary>
/// User-configurable settings. Plain mutable POCO so it serializes with the
/// source-generated <see cref="System.Text.Json"/> context (no reflection).
/// Unknown properties in an older file are ignored, so upgrades are lossless.
///
/// <para>The three keep-awake actions — cursor pulse, path movement and
/// auto-click — each carry their own enable flag and their own interval, because
/// they run on independent schedules and are expected to overlap.</para>
/// </summary>
public sealed class Settings
{
    // Bounds shared with the action classes and the UI, so the clamp in
    // Normalize() and the spinner limits on screen cannot drift apart.
    public const int MinPathRadius = 10;
    public const int MaxPathRadius = 2000;
    public const int MinPathSpeed = 40;
    public const int MaxPathSpeed = 8000;
    public const int MinClickIntervalMs = 10;
    public const int MaxClickIntervalMs = 600_000;
    public const int MinScrollAmount = 1;
    public const int MaxScrollAmount = 20;
    public const int MinScrollInterval = 5;
    public const int MaxScrollInterval = 3600;

    /// <summary>Master on/off. When false the controller idles at zero CPU.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Genuine-idle time, in seconds, before keep-awake engages.</summary>
    public int IdleThresholdSeconds { get; set; } = 300; // 5 minutes

    // ── Action 1: the cursor pulse ───────────────────────────────────────────

    /// <summary>Emit the imperceptible 1 px keep-awake pulse. The original behaviour.</summary>
    public bool CursorPulseEnabled { get; set; } = true;

    /// <summary>Shortest gap, in seconds, between two keep-awake cursor pulses.</summary>
    public int MinIntervalSeconds { get; set; } = 45;

    /// <summary>Longest gap, in seconds, between two keep-awake cursor pulses.</summary>
    public int MaxIntervalSeconds { get; set; } = 120;

    /// <summary>Return the cursor to its exact starting position after every pulse.</summary>
    public bool RestoreCursor { get; set; } = true;

    // ── Action 2: path movement ──────────────────────────────────────────────

    /// <summary>Trace a path with the pointer on its own schedule, alongside pulses.</summary>
    public bool PathEnabled { get; set; }

    /// <summary>Shape to trace.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PathPattern>))]
    public PathPattern PathPattern { get; set; } = PathPattern.Circle;

    /// <summary>Whether generated shapes are centred on the pointer or on the desktop.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PathOrigin>))]
    public PathOrigin PathOrigin { get; set; } = PathOrigin.Cursor;

    /// <summary>Half-width of the generated shape, in pixels.</summary>
    public int PathRadius { get; set; } = 120;

    /// <summary>How fast the pointer travels along the path.</summary>
    public int PathSpeedPixelsPerSecond { get; set; } = 600;

    /// <summary>Shortest gap, in seconds, between two path runs.</summary>
    public int PathMinIntervalSeconds { get; set; } = 60;

    /// <summary>Longest gap, in seconds, between two path runs.</summary>
    public int PathMaxIntervalSeconds { get; set; } = 180;

    /// <summary>Put the pointer back where it started once a run finishes.</summary>
    public bool PathRestoreCursor { get; set; } = true;

    /// <summary>
    /// Absolute screen coordinates for <see cref="Actions.PathPattern.Custom"/>, as
    /// <c>x,y</c> pairs separated by anything convenient. Ignored for generated shapes.
    /// </summary>
    public string PathCustomPoints { get; set; } = string.Empty;

    // ── Action 3: auto-click ─────────────────────────────────────────────────

    /// <summary>
    /// Click on an interval while keep-awake is engaged. Off by default: unlike the
    /// other two actions, a click lands inside whatever application is under the
    /// pointer, so it is only ever switched on deliberately.
    /// </summary>
    public bool AutoClickEnabled { get; set; }

    /// <summary>Which button to press.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<MouseButton>))]
    public MouseButton AutoClickButton { get; set; } = MouseButton.Left;

    /// <summary>Gap between clicks, in milliseconds.</summary>
    public int AutoClickIntervalMs { get; set; } = 1000;

    /// <summary>Send a double click rather than a single one.</summary>
    public bool AutoClickDoubleClick { get; set; }

    /// <summary>
    /// Screen positions to click, as <c>x,y</c> pairs in the same free-form format
    /// the path accepts. The clicker cycles through them: travel to the next one,
    /// click, wait the interval, repeat. Empty means never move the pointer and
    /// simply click wherever it is resting.
    /// </summary>
    public string AutoClickPoints { get; set; } = string.Empty;

    /// <summary>How fast the pointer travels between click points.</summary>
    public int AutoClickTravelSpeed { get; set; } = 900;

    // ── Action 4: auto-scroll ────────────────────────────────────────────────

    /// <summary>
    /// Rotate the mouse wheel on an interval while keep-awake is engaged.
    /// Resets the input idle clock and keeps document/feed readers active.
    /// </summary>
    public bool ScrollEnabled { get; set; }

    /// <summary>Which scrolling pattern to use (Rebound, Down, Up, Alternating).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ScrollMode>))]
    public ScrollMode ScrollMode { get; set; } = ScrollMode.Rebound;

    /// <summary>Number of wheel notches per scroll action.</summary>
    public int ScrollAmount { get; set; } = 2;

    /// <summary>Shortest gap, in seconds, between scroll actions.</summary>
    public int ScrollMinIntervalSeconds { get; set; } = 30;

    /// <summary>Longest gap, in seconds, between scroll actions.</summary>
    public int ScrollMaxIntervalSeconds { get; set; } = 90;

    // ── Action 5: target activity & burst mode ───────────────────────────────

    public const int MinTargetActivityPercent = 20;
    public const int MaxTargetActivityPercent = 95;
    public const int DefaultTargetActivityPercent = 80;

    /// <summary>
    /// When true, NotIdle executes coordinated human-like activity bursts (organic wander,
    /// subtle rebound scrolls, and natural micro-pauses) to maintain a targeted tracking
    /// activity score on monitoring tools (Time Doctor, Hubstaff, Upwork, etc.).
    /// </summary>
    public bool BurstModeEnabled { get; set; }

    /// <summary>Target activity score percentage (20% to 95%, default 80%).</summary>
    public int TargetActivityPercent { get; set; } = DefaultTargetActivityPercent;

    // ── Global behaviour ─────────────────────────────────────────────────────

    /// <summary>
    /// Let NotIdle pick the actions and cadence for itself: it checks whether its
    /// own input is actually registering with the OS and escalates or backs off
    /// accordingly. See <see cref="AdaptiveTuner"/>. Never switches clicking on.
    /// </summary>
    public bool AdaptiveMode { get; set; } = true;

    /// <summary>Also keep the display on, not just the machine (ES_DISPLAY_REQUIRED).</summary>
    public bool KeepDisplayOn { get; set; } = true;

    /// <summary>Stand down while a full-screen / presentation / game app has focus.</summary>
    public bool PauseWhenFullScreen { get; set; } = true;

    /// <summary>Stand down when running on laptop battery to prevent draining power.</summary>
    public bool PauseOnBattery { get; set; }

    /// <summary>Stand down when laptop battery is low.</summary>
    public bool PauseOnLowBattery { get; set; } = true;

    /// <summary>Battery percentage threshold under which keep-awake stands down.</summary>
    public int LowBatteryThresholdPercent { get; set; } = 20;

    /// <summary>Enable global hotkey (Ctrl + Alt + K) to toggle keep-awake anywhere.</summary>
    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>Launch on Windows sign-in (managed via the HKCU Run key).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Switch keep-awake off automatically this many minutes after it is turned on.
    /// Zero means never. Guards against the real failure mode of these tools:
    /// leaving one running for weeks without noticing.
    /// </summary>
    public int AutoOffMinutes { get; set; }

    /// <summary>
    /// Stealth mode: suppresses all balloon notifications/popups and disguises the tray
    /// tooltip as a generic system service to ensure 100% clean screenshots and tracker invisibility.
    /// </summary>
    public bool StealthMode { get; set; } = true;

    /// <summary>
    /// Ghost mode: completely removes the system tray icon from the taskbar, running 100% invisibly
    /// as a background worker. Controlled via global hotkey (Ctrl + Alt + K to toggle, Ctrl + Alt + D to open settings).
    /// </summary>
    public bool GhostMode { get; set; }

    // Derived views for the engine. JsonIgnore keeps them out of settings.json —
    // they are ignored on read anyway, and persisting them just invites someone to
    // hand-edit the copy that has no effect.
    [JsonIgnore] public TimeSpan IdleThreshold => TimeSpan.FromSeconds(IdleThresholdSeconds);
    [JsonIgnore] public TimeSpan MinInterval => TimeSpan.FromSeconds(MinIntervalSeconds);
    [JsonIgnore] public TimeSpan MaxInterval => TimeSpan.FromSeconds(MaxIntervalSeconds);

    /// <summary>True when the configuration would have NotIdle do nothing at all while idle.</summary>
    [JsonIgnore]
    public bool HasAnyAction => CursorPulseEnabled || PathEnabled || AutoClickEnabled || ScrollEnabled || BurstModeEnabled;

    /// <summary>Defensive clamp so a hand-edited or corrupt file can't wedge the controller.</summary>
    public void Normalize()
    {
        IdleThresholdSeconds = Math.Clamp(IdleThresholdSeconds, 10, 24 * 60 * 60);
        MinIntervalSeconds = Math.Clamp(MinIntervalSeconds, 15, 60 * 60);
        MaxIntervalSeconds = Math.Clamp(MaxIntervalSeconds, MinIntervalSeconds, 60 * 60);
        AutoOffMinutes = Math.Clamp(AutoOffMinutes, 0, 24 * 60);

        PathRadius = Math.Clamp(PathRadius, MinPathRadius, MaxPathRadius);
        PathSpeedPixelsPerSecond = Math.Clamp(PathSpeedPixelsPerSecond, MinPathSpeed, MaxPathSpeed);
        PathMinIntervalSeconds = Math.Clamp(PathMinIntervalSeconds, 10, 60 * 60);
        PathMaxIntervalSeconds = Math.Clamp(PathMaxIntervalSeconds, PathMinIntervalSeconds, 60 * 60);
        PathCustomPoints = PathCustomPoints?.Trim() ?? string.Empty;

        AutoClickIntervalMs = Math.Clamp(AutoClickIntervalMs, MinClickIntervalMs, MaxClickIntervalMs);
        AutoClickTravelSpeed = Math.Clamp(AutoClickTravelSpeed, MinPathSpeed, MaxPathSpeed);
        AutoClickPoints = AutoClickPoints?.Trim() ?? string.Empty;

        ScrollAmount = Math.Clamp(ScrollAmount, MinScrollAmount, MaxScrollAmount);
        ScrollMinIntervalSeconds = Math.Clamp(ScrollMinIntervalSeconds, MinScrollInterval, MaxScrollInterval);
        ScrollMaxIntervalSeconds = Math.Clamp(ScrollMaxIntervalSeconds, ScrollMinIntervalSeconds, MaxScrollInterval);

        TargetActivityPercent = Math.Clamp(TargetActivityPercent, MinTargetActivityPercent, MaxTargetActivityPercent);

        LowBatteryThresholdPercent = Math.Clamp(LowBatteryThresholdPercent, 5, 50);

        // A list that parses to nothing would silently mean "click in place", which
        // is not what someone who typed coordinates asked for. Drop it so the UI can
        // say so rather than quietly doing something else.
        if (AutoClickPoints.Length > 0 && CursorPath.ParsePoints(AutoClickPoints).Count == 0)
            AutoClickPoints = string.Empty;

        if (!Enum.IsDefined(PathPattern)) PathPattern = PathPattern.Circle;
        if (!Enum.IsDefined(PathOrigin)) PathOrigin = PathOrigin.Cursor;
        if (!Enum.IsDefined(AutoClickButton)) AutoClickButton = MouseButton.Left;
        if (!Enum.IsDefined(ScrollMode)) ScrollMode = ScrollMode.Rebound;

        // A custom path with nothing usable in it would silently do nothing every
        // run; fall back to the shape rather than pretending it is configured.
        if (PathPattern == PathPattern.Custom && CursorPath.ParsePoints(PathCustomPoints).Count < 2)
            PathEnabled = false;

        // The pulse is the cheapest way to keep a session awake, so an accidental
        // "nothing enabled" configuration falls back to it rather than to silence.
        if (!HasAnyAction) CursorPulseEnabled = true;
    }

    /// <summary>Human-friendly threshold, e.g. "45s", "5 min", or "1m 30s".</summary>
    public string DescribeThreshold() => FormatDuration(IdleThresholdSeconds);

    /// <summary>Human-friendly interval range, e.g. "45–120s".</summary>
    public string DescribeInterval() => MinIntervalSeconds == MaxIntervalSeconds
        ? FormatDuration(MinIntervalSeconds)
        : $"{MinIntervalSeconds}–{MaxIntervalSeconds}s";

    /// <summary>Human-friendly path cadence, e.g. "Circle · 120px · every 60–180s".</summary>
    public string DescribePath()
    {
        if (!PathEnabled) return "off";

        string cadence = PathMinIntervalSeconds == PathMaxIntervalSeconds
            ? $"every {FormatDuration(PathMinIntervalSeconds)}"
            : $"every {PathMinIntervalSeconds}–{PathMaxIntervalSeconds}s";

        return PathPattern switch
        {
            PathPattern.Custom => $"Custom · {CursorPath.ParsePoints(PathCustomPoints).Count} points · {cadence}",
            PathPattern.FullScreen => $"Full screen · {cadence}",
            PathPattern.OrganicJiggle => $"Organic jiggle · {cadence}",
            _ => $"{PathPattern} · {PathRadius}px · {cadence}",
        };
    }

    /// <summary>Human-friendly click cadence, e.g. "Left · every 1.0s".</summary>
    public string DescribeAutoClick()
    {
        if (!AutoClickEnabled) return "off";

        string rate = AutoClickIntervalMs >= 1000
            ? $"every {AutoClickIntervalMs / 1000.0:0.#}s"
            : $"every {AutoClickIntervalMs}ms";

        int points = CursorPath.ParsePoints(AutoClickPoints).Count;
        string where = points == 0 ? "in place" : $"over {points} point{(points == 1 ? "" : "s")}";

        return $"{AutoClickButton}{(AutoClickDoubleClick ? " double" : "")} · {rate} · {where}";
    }

    /// <summary>Human-friendly scroll cadence, e.g. "Rebound (2 notches) · every 30–90s".</summary>
    public string DescribeScroll()
    {
        if (!ScrollEnabled) return "off";

        string cadence = ScrollMinIntervalSeconds == ScrollMaxIntervalSeconds
            ? $"every {FormatDuration(ScrollMinIntervalSeconds)}"
            : $"every {ScrollMinIntervalSeconds}–{ScrollMaxIntervalSeconds}s";

        return $"{ScrollMode} ({ScrollAmount} notch{(ScrollAmount == 1 ? "" : "es")}) · {cadence}";
    }

    /// <summary>Human-friendly auto-off setting, e.g. "2h", "90 min", or "never".</summary>
    public string DescribeAutoOff()
    {
        if (AutoOffMinutes <= 0) return "never";
        if (AutoOffMinutes % 60 != 0) return $"{AutoOffMinutes} min";
        int hours = AutoOffMinutes / 60;
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }

    /// <summary>Human-friendly burst setting, e.g. "Target 80% · Human Work Rhythm".</summary>
    public string DescribeBurst()
    {
        if (!BurstModeEnabled) return "off";
        return $"Target {TargetActivityPercent}% · Human Work Rhythm";
    }

    /// <summary>Which actions are switched on, e.g. "pulse + click".</summary>
    public string DescribeActions()
    {
        var parts = new List<string>(5);
        if (CursorPulseEnabled) parts.Add("pulse");
        if (PathEnabled) parts.Add("path");
        if (AutoClickEnabled) parts.Add("click");
        if (ScrollEnabled) parts.Add("scroll");
        if (BurstModeEnabled) parts.Add($"burst ({TargetActivityPercent}%)");
        return parts.Count == 0 ? "none" : string.Join(" + ", parts);
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

        CursorPulseEnabled = CursorPulseEnabled,
        MinIntervalSeconds = MinIntervalSeconds,
        MaxIntervalSeconds = MaxIntervalSeconds,
        RestoreCursor = RestoreCursor,

        PathEnabled = PathEnabled,
        PathPattern = PathPattern,
        PathOrigin = PathOrigin,
        PathRadius = PathRadius,
        PathSpeedPixelsPerSecond = PathSpeedPixelsPerSecond,
        PathMinIntervalSeconds = PathMinIntervalSeconds,
        PathMaxIntervalSeconds = PathMaxIntervalSeconds,
        PathRestoreCursor = PathRestoreCursor,
        PathCustomPoints = PathCustomPoints,

        AutoClickEnabled = AutoClickEnabled,
        AutoClickButton = AutoClickButton,
        AutoClickIntervalMs = AutoClickIntervalMs,
        AutoClickDoubleClick = AutoClickDoubleClick,
        AutoClickPoints = AutoClickPoints,
        AutoClickTravelSpeed = AutoClickTravelSpeed,

        ScrollEnabled = ScrollEnabled,
        ScrollMode = ScrollMode,
        ScrollAmount = ScrollAmount,
        ScrollMinIntervalSeconds = ScrollMinIntervalSeconds,
        ScrollMaxIntervalSeconds = ScrollMaxIntervalSeconds,

        BurstModeEnabled = BurstModeEnabled,
        TargetActivityPercent = TargetActivityPercent,

        AdaptiveMode = AdaptiveMode,
        KeepDisplayOn = KeepDisplayOn,
        PauseWhenFullScreen = PauseWhenFullScreen,
        PauseOnBattery = PauseOnBattery,
        PauseOnLowBattery = PauseOnLowBattery,
        LowBatteryThresholdPercent = LowBatteryThresholdPercent,
        GlobalHotkeyEnabled = GlobalHotkeyEnabled,
        StartWithWindows = StartWithWindows,
        AutoOffMinutes = AutoOffMinutes,
        StealthMode = StealthMode,
        GhostMode = GhostMode,
    };
}
