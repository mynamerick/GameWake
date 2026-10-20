namespace GameWake.Core.Models;

public enum ActivityState
{
    NotRunning,
    Idle,
    Queued,
    Downloading,
    Paused,
    Updating,
    Installing,
    Unpacking,
    Verifying,
    Completed,
    Unknown,
}

public static class ActivityStateExtensions
{
    /// <summary>States in which the launcher is actively doing work that sleep would interrupt.</summary>
    public static bool IsWorking(this ActivityState state) => state is
        ActivityState.Downloading or
        ActivityState.Updating or
        ActivityState.Installing or
        ActivityState.Unpacking or
        ActivityState.Verifying;

    public static bool IsTransfer(this ActivityState state) =>
        state is ActivityState.Downloading or ActivityState.Updating;

    public static string ToDisplayText(this ActivityState state) => state switch
    {
        ActivityState.NotRunning => "Not running",
        ActivityState.Idle => "Idle",
        ActivityState.Queued => "Queued",
        ActivityState.Downloading => "Downloading",
        ActivityState.Paused => "Paused",
        ActivityState.Updating => "Updating",
        ActivityState.Installing => "Installing",
        ActivityState.Unpacking => "Unpacking",
        ActivityState.Verifying => "Verifying",
        ActivityState.Completed => "Completed",
        _ => "Unknown",
    };

    /// <summary>Ordering used when summarising several operations into one launcher state.</summary>
    public static int SummaryPriority(this ActivityState state) => state switch
    {
        ActivityState.Downloading => 10,
        ActivityState.Updating => 9,
        ActivityState.Installing => 8,
        ActivityState.Unpacking => 7,
        ActivityState.Verifying => 6,
        ActivityState.Unknown => 5,
        ActivityState.Paused => 4,
        ActivityState.Queued => 3,
        ActivityState.Completed => 2,
        ActivityState.Idle => 1,
        _ => 0,
    };
}
