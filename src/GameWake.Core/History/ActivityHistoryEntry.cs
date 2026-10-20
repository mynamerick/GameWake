using System.Text.Json.Serialization;
using GameWake.Core.Models;

namespace GameWake.Core.History;

public enum HistoryOutcome
{
    InProgress,
    Completed,
    Paused,
    Stopped,
    LauncherClosed,
    MonitoringStopped,
}

public sealed class ActivityHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public LauncherKind Launcher { get; set; }

    public string? GameId { get; set; }

    public string? GameTitle { get; set; }

    /// <summary>The first working state observed, e.g. Downloading or Verifying.</summary>
    public ActivityState Operation { get; set; }

    /// <summary>Distinct working states in the order they were observed.</summary>
    public List<ActivityState> Phases { get; set; } = [];

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public HistoryOutcome Outcome { get; set; }

    /// <summary>Whether GameWake held a sleep-prevention request because of this operation.</summary>
    public bool SleepPrevented { get; set; }

    public DetectionConfidence Confidence { get; set; }

    [JsonIgnore]
    public bool IsSimulated { get; set; }

    [JsonIgnore]
    public string? OperationKey { get; set; }

    [JsonIgnore]
    public DateTimeOffset LastWorkingAt { get; set; }

    [JsonIgnore]
    public TimeSpan? Duration => FinishedAt is { } finished ? finished - StartedAt : null;

    public ActivityHistoryEntry Clone()
    {
        var copy = (ActivityHistoryEntry)MemberwiseClone();
        copy.Phases = [.. Phases];
        return copy;
    }
}
