namespace GameWake.Core.Engine;

public enum NotificationKind
{
    DownloadStarted,
    DownloadCompleted,
    DownloadPaused,
    SleepPreventionActivated,
    SleepPreventionReleased,
    DetectionError,
}

public sealed record MonitorNotification(NotificationKind Kind, string Title, string Message);
