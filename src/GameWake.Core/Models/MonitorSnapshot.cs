namespace GameWake.Core.Models;

public enum MonitorStatus
{
    /// <summary>Monitoring has started but the first poll has not completed.</summary>
    Checking,

    /// <summary>Qualifying activity is present and sleep is being prevented.</summary>
    SleepPrevented,

    /// <summary>Activity has stopped; sleep stays prevented until the grace period ends.</summary>
    GracePeriod,

    /// <summary>No qualifying activity; Windows sleeps normally.</summary>
    Idle,

    /// <summary>The user paused monitoring; Windows sleeps normally.</summary>
    Paused,

    /// <summary>No enabled provider can currently detect anything.</summary>
    Unavailable,
}

/// <summary>The engine's combined view, published after every poll.</summary>
public sealed record MonitorSnapshot
{
    public required MonitorStatus Status { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public IReadOnlyList<LauncherSnapshot> Launchers { get; init; } = [];

    /// <summary>Operations that currently justify preventing sleep under the user's settings.</summary>
    public IReadOnlyList<GameOperation> QualifyingOperations { get; init; } = [];

    /// <summary>Working operations that do not qualify (e.g. probable activity while that option is off).</summary>
    public IReadOnlyList<GameOperation> ExcludedOperations { get; init; } = [];

    public bool SleepPrevented { get; init; }

    public DateTimeOffset? SleepPreventedSince { get; init; }

    public DateTimeOffset? GraceEndsAt { get; init; }

    /// <summary>Launchers keeping the PC awake (or that last did, during the grace period).</summary>
    public IReadOnlyList<LauncherKind> ResponsibleLaunchers { get; init; } = [];

    public bool IsPaused { get; init; }

    public bool IsSimulationActive { get; init; }

    public static MonitorSnapshot Initial(DateTimeOffset now) => new()
    {
        Status = MonitorStatus.Checking,
        UpdatedAt = now,
    };
}
