using System.Windows;
using GameWake.App.ViewModels;
using GameWake.Core.Models;

namespace GameWake.App.Services;

/// <summary>Owns the notification-area icon and its menu. All WinForms objects stay on the WPF dispatcher thread.</summary>
public sealed class TrayHost : IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly AppHost _host;
    private readonly Func<Window> _window;
    private readonly System.Windows.Forms.NotifyIcon _notify;
    private readonly System.Windows.Forms.ToolStripMenuItem _open;
    private readonly System.Windows.Forms.ToolStripMenuItem _activity;
    private readonly System.Windows.Forms.ToolStripMenuItem _pause;
    private readonly System.Windows.Forms.ToolStripMenuItem _settings;
    private readonly System.Windows.Forms.ToolStripMenuItem _exit;
    private readonly Dictionary<TrayState, System.Drawing.Icon> _icons = [];
    private TrayState _state = TrayState.Idle;
    private TrayState _lastActive = TrayState.Downloading;
    private bool _disposed;

    public TrayHost(ShellViewModel shell, AppHost host, Func<Window> window)
    {
        _shell = shell;
        _host = host;
        _window = window;

        var size = Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width);
        foreach (TrayState state in Enum.GetValues<TrayState>())
        {
            _icons[state] = TrayIconFactory.Create(state, size);
        }

        _open = new System.Windows.Forms.ToolStripMenuItem("Open Dashboard", null, (_, _) => _shell.ShowDashboard());
        _activity = new System.Windows.Forms.ToolStripMenuItem("Current Activity");
        _pause = new System.Windows.Forms.ToolStripMenuItem("Pause Monitoring", null, (_, _) => _shell.ToggleMonitoring());
        _settings = new System.Windows.Forms.ToolStripMenuItem("Settings", null, (_, _) => _shell.ShowSettings());
        _exit = new System.Windows.Forms.ToolStripMenuItem("Exit", null, (_, _) => _shell.Exit());

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(_open);
        menu.Items.Add(_activity);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_pause);
        menu.Items.Add(_settings);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_exit);

        _notify = new System.Windows.Forms.NotifyIcon
        {
            Icon = _icons[TrayState.Idle],
            Text = "GameWake — Starting",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notify.DoubleClick += (_, _) => _shell.ShowDashboard();

        _host.Engine.SnapshotUpdated += OnSnapshot;
        Apply(_host.Engine.Current);
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _notify.ShowBalloonTip(4000, title, message, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception)
        {
        }
    }

    private void OnSnapshot(object? sender, MonitorSnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            Apply(snapshot);
        }
        else
        {
            dispatcher.BeginInvoke(() => Apply(snapshot));
        }
    }

    private void Apply(MonitorSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        var presentation = TrayPresenter.Describe(snapshot, _lastActive);
        _state = presentation.State;
        if (snapshot.Status is MonitorStatus.SleepPrevented)
        {
            _lastActive = presentation.State;
        }

        _notify.Icon = _icons.GetValueOrDefault(_state) ?? _icons[TrayState.Idle];
        _notify.Text = presentation.ToolTip;
        _pause.Text = snapshot.IsPaused ? "Resume Monitoring" : "Pause Monitoring";

        _activity.DropDownItems.Clear();
        var operations = snapshot.QualifyingOperations.Count > 0
            ? snapshot.QualifyingOperations
            : snapshot.Launchers.SelectMany(l => l.Operations).Where(o => o.State.IsWorking()).ToList();
        if (operations.Count == 0)
        {
            _activity.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("No active downloads") { Enabled = false });
            return;
        }

        foreach (var operation in operations)
        {
            var title = $"{operation.Launcher.ToDisplayName()}: {operation.DisplayTitle} — {operation.State.ToDisplayText()}";
            var item = new System.Windows.Forms.ToolStripMenuItem(title) { Enabled = false };
            _activity.DropDownItems.Add(item);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Engine.SnapshotUpdated -= OnSnapshot;
        _notify.Visible = false;
        _notify.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }
}
