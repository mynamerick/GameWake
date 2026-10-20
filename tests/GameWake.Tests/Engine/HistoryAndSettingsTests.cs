using GameWake.Core.History;
using GameWake.Core.Infrastructure;
using GameWake.Core.Models;
using GameWake.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameWake.Tests.Engine;

public sealed class HistoryAndSettingsTests
{
    [Fact]
    public void History_opens_on_working_activity_and_closes_on_pause_and_complete()
    {
        var tracker = new ActivityHistoryTracker();
        var now = new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero);
        var started = Snap(Ops.Steam("1", ActivityState.Downloading, DetectionConfidence.Confirmed, "Game"));
        tracker.Update([started], new HashSet<string> { "steam:1" }, sleepHeld: true, now);
        var open = Assert.Single(tracker.GetEntries());
        Assert.Equal(HistoryOutcome.InProgress, open.Outcome);
        Assert.True(open.SleepPrevented);

        tracker.Update([Snap(Ops.Steam("1", ActivityState.Paused, DetectionConfidence.Inactive, "Game"))], new HashSet<string>(), false, now.AddMinutes(5));
        Assert.Equal(HistoryOutcome.Paused, Assert.Single(tracker.GetEntries()).Outcome);

        tracker.Update([Snap(Ops.Steam("2", ActivityState.Downloading, DetectionConfidence.Confirmed, "Other"))], new HashSet<string> { "steam:2" }, true, now.AddMinutes(6));
        tracker.Update([Snap(Ops.Steam("2", ActivityState.Completed, DetectionConfidence.Inactive, "Other"))], new HashSet<string>(), false, now.AddMinutes(20));
        Assert.Contains(tracker.GetEntries(), e => e.GameTitle == "Other" && e.Outcome == HistoryOutcome.Completed);
    }

    [Fact]
    public void Detection_errors_do_not_close_open_entries()
    {
        var tracker = new ActivityHistoryTracker();
        var now = DateTimeOffset.Now;
        tracker.Update([Snap(Ops.Steam("1", ActivityState.Downloading, DetectionConfidence.Confirmed, "Game"))], new HashSet<string> { "steam:1" }, true, now);
        tracker.Update(
            [new LauncherSnapshot { Launcher = LauncherKind.Steam, CheckedAt = now, Error = "failed" }],
            new HashSet<string> { "steam:1" },
            true,
            now.AddSeconds(5));
        Assert.Equal(HistoryOutcome.InProgress, Assert.Single(tracker.GetEntries()).Outcome);
    }

    [Fact]
    public void Settings_store_round_trips_and_recovers_from_corrupt_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameWake-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path, NullLogger<SettingsStore>.Instance);
            store.Save(new AppSettings { PollingIntervalSeconds = 9, EnableGog = false, IncludeProbableActivity = true });
            var loaded = store.Load();
            Assert.Equal(9, loaded.PollingIntervalSeconds);
            Assert.False(loaded.EnableGog);
            Assert.True(loaded.IncludeProbableActivity);

            File.WriteAllText(path, "{ not json");
            var recovered = store.Load();
            Assert.Equal(5, recovered.PollingIntervalSeconds);
            Assert.True(Directory.EnumerateFiles(dir, "settings.json.corrupt-*").Any());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Settings_normalize_clamps_unsafe_values()
    {
        var settings = new AppSettings { PollingIntervalSeconds = 0, GracePeriodSeconds = 99999 }.Normalize();
        Assert.Equal(AppSettings.MinPollingIntervalSeconds, settings.PollingIntervalSeconds);
        Assert.Equal(AppSettings.MaxGracePeriodSeconds, settings.GracePeriodSeconds);
    }

    [Fact]
    public void History_store_skips_simulated_entries_and_survives_corrupt_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameWake-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "history.json");
            var store = new HistoryStore(path, NullLogger<HistoryStore>.Instance);
            store.Save(
            [
                new ActivityHistoryEntry { GameTitle = "Real", StartedAt = DateTimeOffset.Now, Launcher = LauncherKind.Steam, IsSimulated = false },
                new ActivityHistoryEntry { GameTitle = "Fake", StartedAt = DateTimeOffset.Now, Launcher = LauncherKind.Simulation, IsSimulated = true },
            ]);
            var loaded = store.Load();
            Assert.DoesNotContain(loaded, e => e.IsSimulated);
            Assert.Contains(loaded, e => e.GameTitle == "Real");

            File.WriteAllText(path, "!!!!");
            Assert.Empty(store.Load());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Atomic_file_replaces_destination()
    {
        var path = Path.Combine(Path.GetTempPath(), "GameWake-atomic-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            AtomicFile.WriteAllText(path, "one");
            AtomicFile.WriteAllText(path, "two");
            Assert.Equal("two", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static LauncherSnapshot Snap(params GameOperation[] operations) => new()
    {
        Launcher = LauncherKind.Steam,
        CheckedAt = DateTimeOffset.Now,
        IsInstalled = true,
        IsRunning = true,
        Operations = operations,
    };
}
