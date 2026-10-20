using GameWake.Core.Models;

namespace GameWake.Core.Settings;

public sealed record AppSettings
{
    public const int MinPollingIntervalSeconds = 2;
    public const int MaxPollingIntervalSeconds = 60;
    public const int MinGracePeriodSeconds = 0;
    public const int MaxGracePeriodSeconds = 1800;

    public int SchemaVersion { get; init; } = 1;

    // General
    public bool LaunchWithWindows { get; init; }

    public bool StartMinimized { get; init; }

    public bool MinimizeToTrayOnClose { get; init; } = true;

    public bool ShowNotifications { get; init; } = true;

    public bool StartMonitoringAutomatically { get; init; } = true;

    // Download monitoring
    public bool EnableSteam { get; init; } = true;

    public bool EnableXbox { get; init; } = true;

    public bool EnableEpic { get; init; } = true;

    public bool EnableGog { get; init; } = true;

    public int PollingIntervalSeconds { get; init; } = 5;

    public int GracePeriodSeconds { get; init; } = 120;

    public bool PreventSleepDuringDownloads { get; init; } = true;

    public bool PreventSleepDuringUpdates { get; init; } = true;

    public bool PreventSleepDuringInstallations { get; init; } = true;

    public bool PreventSleepDuringUnpacking { get; init; } = true;

    public bool PreventSleepDuringVerification { get; init; } = true;

    public bool IncludeProbableActivity { get; init; }

    // Notifications
    public bool NotifyDownloadStarted { get; init; } = true;

    public bool NotifyDownloadCompleted { get; init; } = true;

    public bool NotifyDownloadPaused { get; init; }

    public bool NotifySleepPreventionActivated { get; init; }

    public bool NotifySleepPreventionReleased { get; init; }

    public bool NotifyDetectionErrors { get; init; } = true;

    /// <summary>Shows paths, evidence and provider diagnostics on launcher cards.</summary>
    public bool ShowDetectionDetails { get; init; }

    public TimeSpan PollingInterval => TimeSpan.FromSeconds(PollingIntervalSeconds);

    public TimeSpan GracePeriod => TimeSpan.FromSeconds(GracePeriodSeconds);

    public bool IsLauncherEnabled(LauncherKind kind) => kind switch
    {
        LauncherKind.Steam => EnableSteam,
        LauncherKind.Xbox => EnableXbox,
        LauncherKind.Epic => EnableEpic,
        LauncherKind.Gog => EnableGog,
        _ => false,
    };

    public bool PreventsSleepDuring(ActivityState state) => state switch
    {
        ActivityState.Downloading => PreventSleepDuringDownloads,
        ActivityState.Updating => PreventSleepDuringUpdates,
        ActivityState.Installing => PreventSleepDuringInstallations,
        ActivityState.Unpacking => PreventSleepDuringUnpacking,
        ActivityState.Verifying => PreventSleepDuringVerification,
        _ => false,
    };

    /// <summary>Clamps values that may have been hand-edited into an unsafe range.</summary>
    public AppSettings Normalize() => this with
    {
        PollingIntervalSeconds = Math.Clamp(PollingIntervalSeconds, MinPollingIntervalSeconds, MaxPollingIntervalSeconds),
        GracePeriodSeconds = Math.Clamp(GracePeriodSeconds, MinGracePeriodSeconds, MaxGracePeriodSeconds),
    };
}
