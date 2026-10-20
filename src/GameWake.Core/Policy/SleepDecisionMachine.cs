namespace GameWake.Core.Policy;

public enum SleepPhase
{
    Released,
    Holding,
    Grace,
}

public readonly record struct SleepDecision(SleepPhase Phase, DateTimeOffset? GraceEndsAt)
{
    public bool ShouldHold => Phase != SleepPhase.Released;
}

/// <summary>
/// Hysteresis between detection and the power request: activity acquires immediately, but
/// release waits until no qualifying activity has been seen for the whole grace period.
/// Brief detection gaps or failures therefore never toggle the power request.
/// </summary>
public sealed class SleepDecisionMachine
{
    private SleepPhase _phase = SleepPhase.Released;
    private DateTimeOffset _lastActivity;

    public SleepPhase Phase => _phase;

    public SleepDecision Evaluate(bool hasQualifyingActivity, DateTimeOffset now, TimeSpan gracePeriod)
    {
        if (hasQualifyingActivity)
        {
            _lastActivity = now;
            _phase = SleepPhase.Holding;
            return new SleepDecision(_phase, null);
        }

        if (_phase == SleepPhase.Released)
        {
            return new SleepDecision(SleepPhase.Released, null);
        }

        var graceEndsAt = _lastActivity + gracePeriod;
        if (now >= graceEndsAt)
        {
            _phase = SleepPhase.Released;
            return new SleepDecision(SleepPhase.Released, null);
        }

        _phase = SleepPhase.Grace;
        return new SleepDecision(SleepPhase.Grace, graceEndsAt);
    }

    /// <summary>Releases immediately, e.g. when the user pauses monitoring.</summary>
    public void Reset() => _phase = SleepPhase.Released;
}
