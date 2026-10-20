using GameWake.Core.Engine;
using GameWake.Core.History;
using GameWake.Core.Models;
using GameWake.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameWake.Tests.Engine;

public sealed class MonitoringEngineTests
{
    [Fact]
    public async Task Confirmed_download_prevents_sleep_idle_running_launcher_does_not()
    {
        var (engine, power, clock, steam) = Create();
        steam.Next = () => Running("570", ActivityState.Idle, DetectionConfidence.Inactive);
        await engine.TickAsync(CancellationToken.None);
        Assert.False(power.IsHeld);
        Assert.Equal(MonitorStatus.Idle, engine.Current.Status);

        steam.Next = () => Running("570", ActivityState.Downloading, DetectionConfidence.Confirmed, "Dota 2");
        await engine.TickAsync(CancellationToken.None);
        Assert.True(power.IsHeld);
        Assert.Equal(MonitorStatus.SleepPrevented, engine.Current.Status);
        Assert.Equal("Dota 2", Assert.Single(engine.Current.QualifyingOperations).GameTitle);
        Assert.Contains(LauncherKind.Steam, engine.Current.ResponsibleLaunchers);
    }

    [Fact]
    public async Task Multiple_simultaneous_downloads_are_all_listed()
    {
        var (engine, power, _, steam) = Create();
        steam.Next = () => new LauncherSnapshot
        {
            Launcher = LauncherKind.Steam,
            CheckedAt = DateTimeOffset.Now,
            IsInstalled = true,
            IsRunning = true,
            Operations =
            [
                Ops.Steam("1", ActivityState.Downloading, DetectionConfidence.Confirmed, "One"),
                Ops.Steam("2", ActivityState.Downloading, DetectionConfidence.Confirmed, "Two"),
            ],
        };
        await engine.TickAsync(CancellationToken.None);
        Assert.True(power.IsHeld);
        Assert.Equal(2, engine.Current.QualifyingOperations.Count);
    }

    [Fact]
    public async Task Pausing_a_download_enters_grace_then_releases()
    {
        var (engine, power, clock, steam) = Create(grace: 30);
        steam.Next = () => Running("1", ActivityState.Downloading, DetectionConfidence.Confirmed, "Game");
        await engine.TickAsync(CancellationToken.None);
        Assert.True(power.IsHeld);

        steam.Next = () => Running("1", ActivityState.Paused, DetectionConfidence.Inactive, "Game");
        await engine.TickAsync(CancellationToken.None);
        Assert.True(power.IsHeld);
        Assert.Equal(MonitorStatus.GracePeriod, engine.Current.Status);

        clock.Advance(TimeSpan.FromSeconds(31));
        await engine.TickAsync(CancellationToken.None);
        Assert.False(power.IsHeld);
        Assert.Equal(MonitorStatus.Idle, engine.Current.Status);
    }

    [Fact]
    public async Task Provider_exception_does_not_crash_and_does_not_prevent_sleep()
    {
        var (engine, power, _, steam) = Create();
        steam.Throw = new InvalidOperationException("boom");
        await engine.TickAsync(CancellationToken.None);
        Assert.False(power.IsHeld);
        var steamSnap = engine.Current.Launchers.Single(l => l.Launcher == LauncherKind.Steam);
        Assert.Contains("boom", steamSnap.Error);
    }

    [Fact]
    public async Task User_pause_releases_power_immediately()
    {
        var (engine, power, _, steam) = Create();
        steam.Next = () => Running("1", ActivityState.Downloading, DetectionConfidence.Confirmed, "Game");
        await engine.TickAsync(CancellationToken.None);
        Assert.True(power.IsHeld);

        engine.Pause();
        await engine.TickAsync(CancellationToken.None);
        Assert.False(power.IsHeld);
        Assert.Equal(MonitorStatus.Paused, engine.Current.Status);
    }

    [Fact]
    public async Task Stop_releases_power_and_disposes_providers()
    {
        var (engine, power, _, steam) = Create();
        steam.Next = () => Running("1", ActivityState.Downloading, DetectionConfidence.Confirmed, "Game");
        engine.Start(new AppSettings { EnableXbox = false, EnableEpic = false, EnableGog = false, PollingIntervalSeconds = 2 }, startPaused: false);
        engine.RequestRefresh();
        await WaitUntil(() => power.IsHeld, TimeSpan.FromSeconds(8));
        await engine.StopAsync();
        Assert.False(power.IsHeld);
        Assert.True(steam.Disposed);
    }

    [Fact]
    public async Task Simulated_activity_is_excluded_from_sleep_by_default()
    {
        var clock = new FakeClock();
        var power = new FakePower();
        var factory = new FakeFactory();
        var sim = new ScriptedProvider(LauncherKind.Simulation)
        {
            Next = () => new LauncherSnapshot
            {
                Launcher = LauncherKind.Simulation,
                CheckedAt = clock.Now,
                IsInstalled = true,
                IsRunning = true,
                IsSimulated = true,
                Operations = [Ops.Steam("s", ActivityState.Downloading, DetectionConfidence.Confirmed, "Fake") with { Launcher = LauncherKind.Simulation, Key = "simulation:s", IsSimulated = true }],
            },
        };
        factory.Map[LauncherKind.Simulation] = sim;
        var engine = new MonitoringEngine(factory, power, new ActivityHistoryTracker(), new RuntimeOptions { Simulation = true }, clock, NullLogger<MonitoringEngine>.Instance);
        engine.ApplySettings(new AppSettings { EnableSteam = false, EnableXbox = false, EnableEpic = false, EnableGog = false });
        await engine.TickAsync(CancellationToken.None);
        Assert.False(power.IsHeld);
        Assert.Contains(engine.Current.Launchers, l => l.IsSimulated);
    }

    [Fact]
    public async Task Probable_activity_is_excluded_until_the_setting_is_on()
    {
        var (engine, power, _, steam) = Create();
        steam.Next = () => Running("1", ActivityState.Downloading, DetectionConfidence.Probable, "Maybe");
        await engine.TickAsync(CancellationToken.None);
        Assert.False(power.IsHeld);
        Assert.Single(engine.Current.ExcludedOperations);

        engine.ApplySettings(new AppSettings { IncludeProbableActivity = true, EnableXbox = false, EnableEpic = false, EnableGog = false });
        await engine.TickAsync(CancellationToken.None);
        Assert.True(power.IsHeld);
    }

    private static (MonitoringEngine Engine, FakePower Power, FakeClock Clock, ScriptedProvider Steam) Create(int grace = 120)
    {
        var clock = new FakeClock();
        var power = new FakePower();
        var steam = new ScriptedProvider(LauncherKind.Steam)
        {
            Next = () => new LauncherSnapshot { Launcher = LauncherKind.Steam, CheckedAt = clock.Now, IsInstalled = true, IsRunning = false },
        };
        var factory = new FakeFactory { Map = { [LauncherKind.Steam] = steam } };
        var engine = new MonitoringEngine(factory, power, new ActivityHistoryTracker(), new RuntimeOptions(), clock, NullLogger<MonitoringEngine>.Instance);
        engine.ApplySettings(new AppSettings
        {
            EnableXbox = false,
            EnableEpic = false,
            EnableGog = false,
            GracePeriodSeconds = grace,
        });
        return (engine, power, clock, steam);
    }

    private static LauncherSnapshot Running(string id, ActivityState state, DetectionConfidence confidence, string? title = null) => new()
    {
        Launcher = LauncherKind.Steam,
        CheckedAt = DateTimeOffset.Now,
        IsInstalled = true,
        IsRunning = true,
        Operations = [Ops.Steam(id, state, confidence, title)],
    };

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(20);
        }
    }
}
