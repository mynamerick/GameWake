using System.Windows;
using GameWake.App.Services;
using GameWake.App.ViewModels;
using GameWake.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GameWake.App;

public partial class App : Application
{
    private AppHost? _host;
    private SingleInstance? _instance;
    private TrayHost? _tray;
    private int _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        DesktopNotifier.Initialize();

        DispatcherUnhandledException += (_, args) =>
        {
            _host?.LoggerFactory.CreateLogger("GameWake").LogError(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };

        var runtime = RuntimeOptions.Parse(e.Args);
        _host = AppHost.Create(runtime);
        var settings = _host.Settings.Current;
        _host.Startup.Apply(settings.LaunchWithWindows);

        var shell = new ShellViewModel(_host);
        var window = new MainWindow(shell);
        MainWindow = window;

        _tray = new TrayHost(shell, _host, () => window);
        shell.Attach(window, _tray.ShowBalloon);

        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(shell.ShowDashboard));

        _host.Engine.Start(settings, startPaused: !settings.StartMonitoringAutomatically);

        if (!(runtime.LaunchedAtStartup || settings.StartMinimized))
        {
            window.Show();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        ShutdownHost();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShutdownHost();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void ShutdownHost()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
        {
            return;
        }

        if (_host is null)
        {
            return;
        }

        _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
