namespace GameWake.Core.Models;

/// <summary>
/// One download, update, install or verification observed for a single game.
/// Optional values are null whenever the launcher does not expose them; they are never estimated.
/// </summary>
public sealed record GameOperation
{
    /// <summary>Stable identity across polls, e.g. <c>steam:570</c>.</summary>
    public required string Key { get; init; }

    public required LauncherKind Launcher { get; init; }

    public required ActivityState State { get; init; }

    public required DetectionConfidence Confidence { get; init; }

    public EvidenceKind EvidenceKind { get; init; } = EvidenceKind.Direct;

    public string? GameId { get; init; }

    public string? GameTitle { get; init; }

    /// <summary>Explains where the title came from, or why it is unavailable.</summary>
    public string? TitleNote { get; init; }

    /// <summary>Launcher-specific refinement of the state, e.g. "Workshop content" or "Suspended by Steam".</summary>
    public string? StateDetail { get; init; }

    /// <summary>0..1, only when the launcher reports it.</summary>
    public double? Progress { get; init; }

    /// <summary>Where the progress came from, or why it is unavailable.</summary>
    public string? ProgressNote { get; init; }

    public long? TotalBytes { get; init; }

    public double? BytesPerSecond { get; init; }

    public string? SpeedNote { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];

    public bool IsSimulated { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(GameTitle) ? "Unknown game" : GameTitle;
}
