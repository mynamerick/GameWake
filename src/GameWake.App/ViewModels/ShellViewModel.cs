using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameWake.App.Services;
using GameWake.Core.Engine;
using GameWake.Core.Models;

namespace GameWake.App.ViewModels;

public enum PageId
{
    Dashboard,
    History,
    Settings,
    Advanced,
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _clock;
    private Window? _window;
    private Action<string, string>? _balloon;
    private bool _exiting;
    private MonitorSnapshot _snapshot;

    public ShellViewModel(AppHost host)
    {
        _host = host;
        _snapshot = host.Engine.Current;
        Dashboard = new DashboardViewModel();
        History = new HistoryViewModel(host);
        Settings = new SettingsViewModel(host);
        Advanced = new AdvancedViewModel(host);
        CurrentPage = Dashboard;
        Dashboard.Apply(_snapshot, host.Settings.Current, DateTimeOffset.Now);

        host.Engine.SnapshotUpdated += OnSnapshot;
        host.Engine.NotificationRaised += OnNotification;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            Dashboard.TickDuration(_snapshot, DateTimeOffset.Now);
            if (Page == PageId.Advanced)
            {
                Advanced.RefreshLogs();
            }
        };
        _clock.Start();
    }

    public DashboardViewModel Dashboard { get; }

    public HistoryViewModel History { get; }

    public SettingsViewModel Settings { get; }

    public AdvancedViewModel Advanced { get; }

    public AppHost Host => _host;

    [ObservableProperty] private PageId _page = PageId.Dashboard;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _monitoringLabel = "PAUSE MONITORING";
    [ObservableProperty] private string _subtitle = "Smart download monitoring and sleep management.";

    public bool IsDashboardSelected => Page == PageId.Dashboard;
    public bool IsHistorySelected => Page == PageId.History;
    public bool IsSettingsSelected => Page == PageId.Settings;
    public bool IsAdvancedSelected => Page == PageId.Advanced;

    public void Attach(Window window, Action<string, string> balloon)
    {
        _window = window;
        _balloon = balloon;
    }

    public void ShowDashboard()
    {
        Page = PageId.Dashboard;
        ShowWindow();
    }

    public void ShowSettings()
    {
        Page = PageId.Settings;
        ShowWindow();
    }

    public void ToggleMonitoring()
    {
        if (_host.Engine.IsPaused)
        {
            _host.Engine.Resume();
        }
        else
        {
            _host.Engine.Pause();
        }
    }

    public void Exit()
    {
        _exiting = true;
        Application.Current?.Shutdown();
    }

    /// <summary>Returns true when the close should be cancelled (minimise to tray).</summary>
    public bool RequestClose()
    {
        if (_exiting || !_host.Settings.Current.MinimizeToTrayOnClose)
        {
            _exiting = true;
            Application.Current?.Shutdown();
            return false;
        }

        _window?.Hide();
        return true;
    }

    public void ShowWindow()
    {
        if (_window is global::GameWake.App.MainWindow main)
        {
            main.RestoreFromTray();
            return;
        }

        if (_window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.Activate();
        _window.Focus();
    }

    partial void OnPageChanged(PageId value)
    {
        CurrentPage = value switch
        {
            PageId.History => History,
            PageId.Settings => Settings,
            PageId.Advanced => Advanced,
            _ => Dashboard,
        };
        if (value == PageId.Advanced)
        {
            Advanced.RefreshLogs();
        }

        if (value == PageId.History)
        {
            History.Reload();
        }

        OnPropertyChanged(nameof(IsDashboardSelected));
        OnPropertyChanged(nameof(IsHistorySelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(IsAdvancedSelected));
    }

    [RelayCommand]
    private void GoDashboard() => Page = PageId.Dashboard;

    [RelayCommand]
    private void GoHistory() => Page = PageId.History;

    [RelayCommand]
    private void GoSettings() => Page = PageId.Settings;

    [RelayCommand]
    private void GoAdvanced() => Page = PageId.Advanced;

    [RelayCommand]
    private void PauseOrResume() => ToggleMonitoring();

    [RelayCommand]
    private void Quit() => Exit();

    private void OnSnapshot(object? sender, MonitorSnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            _snapshot = snapshot;
            IsPaused = snapshot.IsPaused;
            MonitoringLabel = snapshot.IsPaused ? "RESUME MONITORING" : "PAUSE MONITORING";
            Dashboard.Apply(snapshot, _host.Settings.Current, DateTimeOffset.Now);
        });
    }

    private void OnNotification(object? sender, MonitorNotification notification)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            if (!DesktopNotifier.TryShow(notification.Title, notification.Message))
            {
                _balloon?.Invoke(notification.Title, notification.Message);
            }
        });
    }
}
