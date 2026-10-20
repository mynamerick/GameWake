using CommunityToolkit.Mvvm.ComponentModel;
using GameWake.Core.Settings;

namespace GameWake.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _loading;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        Load(host.Settings.Current);
        host.Settings.Changed += (_, settings) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            dispatcher.BeginInvoke(() => Load(settings));
        };
    }

    [ObservableProperty] private bool _launchWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTrayOnClose;
    [ObservableProperty] private bool _showNotifications;
    [ObservableProperty] private bool _startMonitoringAutomatically;
    [ObservableProperty] private bool _enableSteam;
    [ObservableProperty] private bool _enableXbox;
    [ObservableProperty] private bool _enableEpic;
    [ObservableProperty] private bool _enableGog;
    [ObservableProperty] private int _pollingIntervalSeconds;
    [ObservableProperty] private int _gracePeriodSeconds;
    [ObservableProperty] private bool _preventSleepDuringDownloads;
    [ObservableProperty] private bool _preventSleepDuringUpdates;
    [ObservableProperty] private bool _preventSleepDuringInstallations;
    [ObservableProperty] private bool _preventSleepDuringUnpacking;
    [ObservableProperty] private bool _preventSleepDuringVerification;
    [ObservableProperty] private bool _includeProbableActivity;
    [ObservableProperty] private bool _notifyDownloadStarted;
    [ObservableProperty] private bool _notifyDownloadCompleted;
    [ObservableProperty] private bool _notifyDownloadPaused;
    [ObservableProperty] private bool _notifySleepPreventionActivated;
    [ObservableProperty] private bool _notifySleepPreventionReleased;
    [ObservableProperty] private bool _notifyDetectionErrors;
    [ObservableProperty] private bool _showDetectionDetails;
    [ObservableProperty] private string _pollingLabel = string.Empty;
    [ObservableProperty] private string _graceLabel = string.Empty;
    [ObservableProperty] private string? _startupWarning;

    public void Load(AppSettings settings)
    {
        _loading = true;
        LaunchWithWindows = settings.LaunchWithWindows;
        StartMinimized = settings.StartMinimized;
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        ShowNotifications = settings.ShowNotifications;
        StartMonitoringAutomatically = settings.StartMonitoringAutomatically;
        EnableSteam = settings.EnableSteam;
        EnableXbox = settings.EnableXbox;
        EnableEpic = settings.EnableEpic;
        EnableGog = settings.EnableGog;
        PollingIntervalSeconds = settings.PollingIntervalSeconds;
        GracePeriodSeconds = settings.GracePeriodSeconds;
        PreventSleepDuringDownloads = settings.PreventSleepDuringDownloads;
        PreventSleepDuringUpdates = settings.PreventSleepDuringUpdates;
        PreventSleepDuringInstallations = settings.PreventSleepDuringInstallations;
        PreventSleepDuringUnpacking = settings.PreventSleepDuringUnpacking;
        PreventSleepDuringVerification = settings.PreventSleepDuringVerification;
        IncludeProbableActivity = settings.IncludeProbableActivity;
        NotifyDownloadStarted = settings.NotifyDownloadStarted;
        NotifyDownloadCompleted = settings.NotifyDownloadCompleted;
        NotifyDownloadPaused = settings.NotifyDownloadPaused;
        NotifySleepPreventionActivated = settings.NotifySleepPreventionActivated;
        NotifySleepPreventionReleased = settings.NotifySleepPreventionReleased;
        NotifyDetectionErrors = settings.NotifyDetectionErrors;
        ShowDetectionDetails = settings.ShowDetectionDetails;
        UpdateLabels();
        StartupWarning = settings.LaunchWithWindows && _host.Startup.IsDisabledByUser()
            ? "Windows Startup apps has GameWake disabled. Enable it there, or toggle this setting off and on."
            : null;
        _loading = false;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading)
        {
            return;
        }

        if (e.PropertyName is nameof(PollingLabel) or nameof(GraceLabel) or nameof(StartupWarning))
        {
            return;
        }

        if (e.PropertyName is nameof(PollingIntervalSeconds) or nameof(GracePeriodSeconds))
        {
            UpdateLabels();
        }

        Persist();
    }

    private void UpdateLabels()
    {
        PollingLabel = $"{PollingIntervalSeconds} seconds";
        GraceLabel = GracePeriodSeconds == 0
            ? "None (release immediately)"
            : GracePeriodSeconds < 60
                ? $"{GracePeriodSeconds} seconds"
                : $"{GracePeriodSeconds} seconds ({GracePeriodSeconds / 60} min)";
    }

    private void Persist()
    {
        var settings = new AppSettings
        {
            LaunchWithWindows = LaunchWithWindows,
            StartMinimized = StartMinimized,
            MinimizeToTrayOnClose = MinimizeToTrayOnClose,
            ShowNotifications = ShowNotifications,
            StartMonitoringAutomatically = StartMonitoringAutomatically,
            EnableSteam = EnableSteam,
            EnableXbox = EnableXbox,
            EnableEpic = EnableEpic,
            EnableGog = EnableGog,
            PollingIntervalSeconds = PollingIntervalSeconds,
            GracePeriodSeconds = GracePeriodSeconds,
            PreventSleepDuringDownloads = PreventSleepDuringDownloads,
            PreventSleepDuringUpdates = PreventSleepDuringUpdates,
            PreventSleepDuringInstallations = PreventSleepDuringInstallations,
            PreventSleepDuringUnpacking = PreventSleepDuringUnpacking,
            PreventSleepDuringVerification = PreventSleepDuringVerification,
            IncludeProbableActivity = IncludeProbableActivity,
            NotifyDownloadStarted = NotifyDownloadStarted,
            NotifyDownloadCompleted = NotifyDownloadCompleted,
            NotifyDownloadPaused = NotifyDownloadPaused,
            NotifySleepPreventionActivated = NotifySleepPreventionActivated,
            NotifySleepPreventionReleased = NotifySleepPreventionReleased,
            NotifyDetectionErrors = NotifyDetectionErrors,
            ShowDetectionDetails = ShowDetectionDetails,
        }.Normalize();

        _host.Settings.Save(settings);
        _host.Startup.Apply(settings.LaunchWithWindows);
        _host.Engine.ApplySettings(settings);
        StartupWarning = settings.LaunchWithWindows && _host.Startup.IsDisabledByUser()
            ? "Windows Startup apps has GameWake disabled. Enable it there, or toggle this setting off and on."
            : null;
    }
}
