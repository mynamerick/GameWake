namespace GameWake.Core.Models;

/// <summary>Everything one provider knows about its launcher at a point in time.</summary>
public sealed record LauncherSnapshot
{
    public required LauncherKind Launcher { get; init; }

    public required DateTimeOffset CheckedAt { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool IsInstalled { get; init; }

    public bool IsRunning { get; init; }

    /// <summary>Short human-readable status, e.g. "Steam is running (session started 16:22)".</summary>
    public string? StatusDetail { get; init; }

    /// <summary>Launcher executable, used only to show its icon.</summary>
    public string? ExecutablePath { get; init; }

    public IReadOnlyList<GameOperation> Operations { get; init; } = [];

    /// <summary>Launcher-wide transfer rate when it cannot be attributed to a single game.</summary>
    public double? TotalBytesPerSecond { get; init; }

    public string? TotalSpeedNote { get; init; }

    public DateTimeOffset? LastSuccessfulDetection { get; init; }

    public string? Error { get; init; }

    /// <summary>Data this provider cannot obtain, shown to the user instead of guessing.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];

    /// <summary>Paths, counters and evidence for "Show provider detection details".</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public bool IsSimulated { get; init; }

    public string DisplayName => Launcher.ToDisplayName();

    public bool HasError => Error is not null;

    public ActivityState SummaryState
    {
        get
        {
            if (HasError)
            {
                return ActivityState.Unknown;
            }

            if (!IsEnabled || !IsInstalled || !IsRunning)
            {
                return ActivityState.NotRunning;
            }

            if (Operations.Count == 0)
            {
                return ActivityState.Idle;
            }

            return Operations.MaxBy(o => o.State.SummaryPriority())!.State;
        }
    }

    public DetectionConfidence SummaryConfidence
    {
        get
        {
            if (HasError)
            {
                return DetectionConfidence.Unknown;
            }

            var working = Operations.Where(o => o.State.IsWorking()).ToList();
            if (working.Count > 0)
            {
                return working.Max(o => o.Confidence);
            }

            return Operations.Any(o => o.State == ActivityState.Unknown)
                ? DetectionConfidence.Unknown
                : DetectionConfidence.Inactive;
        }
    }
}
