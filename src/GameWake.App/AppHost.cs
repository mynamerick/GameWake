using System.Reflection;
using GameWake.Core.Diagnostics;
using GameWake.Core.Engine;
using GameWake.Core.History;
using GameWake.Core.Infrastructure;
using GameWake.Core.Logging;
using GameWake.Core.Models;
using GameWake.Core.Power;
using GameWake.Core.Providers;
using GameWake.Core.Settings;
using GameWake.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameWake.App;

/// <summary>Builds and owns the process-wide services for one GameWake session.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private int _disposed;

    private AppHost(
        AppPaths paths,
        RuntimeOptions runtime,
        SettingsStore settings,
        HistoryStore historyStore,
        ActivityHistoryTracker history,
        MonitoringEngine engine,
        IPowerRequestController power,
        FileLoggerProvider fileLogger,
        ILoggerFactory loggerFactory,
        Services.StartupRegistration startup,
        string version)
    {
        Paths = paths;
        Runtime = runtime;
        Settings = settings;
        HistoryStore = historyStore;
        History = history;
        Engine = engine;
        Power = power;
        FileLogger = fileLogger;
        LoggerFactory = loggerFactory;
        Startup = startup;
        Version = version;
        History.Changed += (_, _) => PersistHistory();
    }

    public AppPaths Paths { get; }

    public RuntimeOptions Runtime { get; }

    public SettingsStore Settings { get; }

    public HistoryStore HistoryStore { get; }

    public ActivityHistoryTracker History { get; }

    public MonitoringEngine Engine { get; }

    public IPowerRequestController Power { get; }

    public FileLoggerProvider FileLogger { get; }

    public ILoggerFactory LoggerFactory { get; }

    public Services.StartupRegistration Startup { get; }

    public string Version { get; }

    public static AppHost Create(RuntimeOptions runtime)
    {
        var paths = AppPaths.ForCurrentUser();
        paths.EnsureCreated();

        var fileLogger = new FileLoggerProvider(paths.LogsDirectory);
        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(fileLogger);
        });

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(runtime);
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(fileLogger);
        services.AddSingleton(sp => new SettingsStore(paths.SettingsFile, sp.GetRequiredService<ILogger<SettingsStore>>()));
        services.AddSingleton(sp => new HistoryStore(paths.HistoryFile, sp.GetRequiredService<ILogger<HistoryStore>>()));
        services.AddSingleton<IExecutionStateApi, Win32ExecutionStateApi>();
        services.AddSingleton<IPowerRequestController, PowerRequestController>();
        services.AddSingleton<ILauncherProviderFactory, LauncherProviderFactory>();
        services.AddSingleton<Services.StartupRegistration>();

        var provider = services.BuildServiceProvider();
        var settingsStore = provider.GetRequiredService<SettingsStore>();
        var historyStore = provider.GetRequiredService<HistoryStore>();
        var history = new ActivityHistoryTracker(historyStore.Load());
        var engine = new MonitoringEngine(
            provider.GetRequiredService<ILauncherProviderFactory>(),
            provider.GetRequiredService<IPowerRequestController>(),
            history,
            runtime,
            SystemClock.Instance,
            provider.GetRequiredService<ILogger<MonitoringEngine>>());

        var version = typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppHost).Assembly.GetName().Version?.ToString()
            ?? "1.0.0";
        var plus = version.IndexOf('+');
        if (plus > 0)
        {
            version = version[..plus];
        }

        var host = new AppHost(
            paths,
            runtime,
            settingsStore,
            historyStore,
            history,
            engine,
            provider.GetRequiredService<IPowerRequestController>(),
            fileLogger,
            loggerFactory,
            provider.GetRequiredService<Services.StartupRegistration>(),
            version);

        settingsStore.Load();
        loggerFactory.CreateLogger("GameWake").LogInformation(
            "GameWake {Version} starting (simulation: {Simulation}, autostart: {Autostart})",
            version, runtime.Simulation, runtime.LaunchedAtStartup);
        return host;
    }

    public string BuildDiagnostics() => DiagnosticsReport.Build(
        Engine.Current,
        Settings.Current,
        Power.IsHeld,
        Power.HeldSince,
        FileLogger.Recent.Snapshot(),
        Version);

    public void PersistHistory()
    {
        var changes = History.TakeChangesForSave();
        if (changes is not null)
        {
            HistoryStore.Save(changes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            PersistHistory();
        }
        catch
        {
        }

        await Engine.DisposeAsync().ConfigureAwait(false);
        Power.Dispose();
        FileLogger.Dispose();
        LoggerFactory.Dispose();
    }
}
