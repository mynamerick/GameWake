using GameWake.Core.Models;

namespace GameWake.App.Services;

public sealed record TrayPresentation(TrayState State, string ToolTip);

/// <summary>Maps the engine's snapshot to the tray icon colour and tooltip.</summary>
public static class TrayPresenter
{
    /// <summary>NotifyIcon tooltips are limited to 127 characters.</summary>
    public const int MaxToolTipLength = 127;

    private const string Prefix = "GameWake — ";

    public static TrayPresentation Describe(MonitorSnapshot snapshot, TrayState lastActiveState)
    {
        var problem = snapshot.Launchers.FirstOrDefault(l => l.IsEnabled && !l.IsSimulated && l.HasError);
        var (state, text) = snapshot.Status switch
        {
            MonitorStatus.Paused => (TrayState.Paused, "Monitoring paused"),
            MonitorStatus.Checking => (TrayState.Idle, "Checking activity"),
            MonitorStatus.Unavailable => (TrayState.Issue, "Detection unavailable"),
            MonitorStatus.SleepPrevented => (ActiveState(snapshot.QualifyingOperations), DescribeActive(snapshot.QualifyingOperations)),
            MonitorStatus.GracePeriod => (lastActiveState, DescribeGrace(snapshot)),
            _ when problem is not null => (TrayState.Issue, $"{problem.DisplayName} detection problem"),
            _ => (TrayState.Idle, "No active downloads"),
        };

        if (snapshot.IsSimulationActive)
        {
            text += " (simulation mode)";
        }

        return new TrayPresentation(state, Truncate(Prefix + text));
    }

    public static TrayState ActiveState(IReadOnlyList<GameOperation> qualifying) =>
        qualifying.Any(o => o.State.IsTransfer()) ? TrayState.Downloading : TrayState.Installing;

    private static string DescribeActive(IReadOnlyList<GameOperation> qualifying)
    {
        if (qualifying.Count == 1)
        {
            var operation = qualifying[0];
            return $"{operation.Launcher.ToDisplayName()} {operation.State.ToDisplayText().ToLowerInvariant()}";
        }

        return qualifying.All(o => o.State.IsTransfer())
            ? $"{qualifying.Count} active downloads"
            : $"{qualifying.Count} active tasks";
    }

    private static string DescribeGrace(MonitorSnapshot snapshot) =>
        snapshot.GraceEndsAt is { } ends
            ? $"Activity finished; normal sleep returns at {ends.LocalDateTime:t}"
            : "Activity finished; grace period";

    private static string Truncate(string text) =>
        text.Length <= MaxToolTipLength ? text : text[..(MaxToolTipLength - 1)] + "…";
}
