using System.Threading;

namespace NotIdle.Core.Actions;

/// <summary>How hard NotIdle is currently trying, when it is choosing for itself.</summary>
public enum AdaptiveLevel
{
    /// <summary>Pulsing at the slowest cadence it can get away with. The quiet default it drifts back to.</summary>
    Minimal,

    /// <summary>Pulsing on the configured interval range.</summary>
    Standard,

    /// <summary>Pulses plus path movement, on a shortened interval. Something is eating our input.</summary>
    Assertive,
}

/// <summary>What the tuner wants the engine to do this cycle.</summary>
/// <param name="PulseEnabled">Emit the 1 px keep-awake pulse.</param>
/// <param name="PathEnabled">Also trace the configured path.</param>
/// <param name="MinIntervalSeconds">Lower bound of the randomized pulse interval.</param>
/// <param name="MaxIntervalSeconds">Upper bound of the randomized pulse interval.</param>
public readonly record struct AdaptivePlan(
    bool PulseEnabled,
    bool PathEnabled,
    int MinIntervalSeconds,
    int MaxIntervalSeconds);

/// <summary>
/// Decides which keep-awake actions to run, so the user does not have to.
///
/// <para><b>The signal.</b> Injected input and real input reset the same OS counter,
/// which is normally a nuisance — but it also means we can check our own work: after
/// a pulse at tick T, the system's last-input timestamp should have moved to
/// approximately T. When it hasn't, our input is being swallowed (a remote session,
/// an input-blocking policy, a driver that coalesces 1 px moves away), and pulsing
/// harder is the correct response. When it lands reliably for a long stretch, the
/// correct response is to do less.</para>
///
/// <para>The ladder only ever escalates as far as path movement. Clicking is never
/// switched on automatically: a click lands <em>inside</em> whatever application is
/// under the pointer, and that is a decision the user makes explicitly, not one an
/// escalation heuristic makes for them.</para>
/// </summary>
public sealed class AdaptiveTuner
{
    /// <summary>Consecutive swallowed actions before climbing a rung. Two, so one flake is not enough.</summary>
    private const int FailuresBeforeEscalating = 2;

    /// <summary>Consecutive landed actions before dropping a rung. Deliberately slower than escalation.</summary>
    private const int SuccessesBeforeRelaxing = 12;

    /// <summary>Failures at the top rung before we admit cursor input is going nowhere.</summary>
    private const int FailuresBeforeGivingUp = 6;

    private readonly Lock _gate = new();

    private AdaptiveLevel _level = AdaptiveLevel.Standard;
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private bool _inputBlocked;

    /// <summary>The rung currently in force.</summary>
    public AdaptiveLevel Level
    {
        get { lock (_gate) return _level; }
    }

    /// <summary>
    /// True once even the most assertive movement has repeatedly failed to register
    /// with the OS. Nothing more to escalate to — worth telling the user, because at
    /// that point the wake lock is doing all the work.
    /// </summary>
    public bool InputBlocked
    {
        get { lock (_gate) return _inputBlocked; }
    }

    /// <summary>
    /// Record whether the previous cycle's action actually reset the system input
    /// clock, and move the ladder if that has now happened enough times in a row.
    /// </summary>
    public void Observe(bool actionLanded)
    {
        lock (_gate)
        {
            if (actionLanded)
            {
                _consecutiveFailures = 0;
                _inputBlocked = false;
                if (++_consecutiveSuccesses < SuccessesBeforeRelaxing) return;

                _consecutiveSuccesses = 0;
                _level = _level switch
                {
                    AdaptiveLevel.Assertive => AdaptiveLevel.Standard,
                    AdaptiveLevel.Standard => AdaptiveLevel.Minimal,
                    _ => AdaptiveLevel.Minimal,
                };
                return;
            }

            _consecutiveSuccesses = 0;
            _consecutiveFailures++;

            if (_level == AdaptiveLevel.Assertive)
            {
                if (_consecutiveFailures >= FailuresBeforeGivingUp) _inputBlocked = true;
                return;
            }

            if (_consecutiveFailures < FailuresBeforeEscalating) return;

            _consecutiveFailures = 0;
            _level = _level == AdaptiveLevel.Minimal ? AdaptiveLevel.Standard : AdaptiveLevel.Assertive;
        }
    }

    /// <summary>Return to the quiet default. Called whenever keep-awake disengages.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _level = AdaptiveLevel.Standard;
            _consecutiveFailures = 0;
            _consecutiveSuccesses = 0;
            _inputBlocked = false;
        }
    }

    /// <summary>
    /// Translate the current rung into concrete instructions, using the user's
    /// configured interval range as the reference point rather than replacing it.
    /// </summary>
    public AdaptivePlan PlanFor(Settings settings)
    {
        int min = settings.MinIntervalSeconds;
        int max = Math.Max(min, settings.MaxIntervalSeconds);

        return Level switch
        {
            // As slow as the user's own upper bound allows, and no path.
            AdaptiveLevel.Minimal => new AdaptivePlan(true, false, max, max),

            // Both actions, and lean towards the short end of the range.
            AdaptiveLevel.Assertive => new AdaptivePlan(true, true, min, Math.Max(min, (min + max) / 2)),

            _ => new AdaptivePlan(true, false, min, max),
        };
    }

    /// <summary>One line for the dashboard, explaining what it decided and why.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (_inputBlocked)
                return "Cursor input is not registering — holding the session awake with the power request alone.";

            return _level switch
            {
                AdaptiveLevel.Minimal => "Pulses are landing reliably — running at the quietest cadence.",
                AdaptiveLevel.Assertive => "Pulses were being swallowed — added path movement and shortened the interval.",
                _ => "Pulsing on your configured interval.",
            };
        }
    }
}
