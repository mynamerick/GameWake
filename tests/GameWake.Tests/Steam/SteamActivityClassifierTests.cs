using GameWake.Core.Models;
using GameWake.Providers.Steam;

namespace GameWake.Tests.Steam;

public sealed class SteamActivityClassifierTests
{
    private static readonly DateTime SessionStart = new(2026, 10, 20, 16, 0, 0);
    private static readonly DateTime Now = new(2026, 10, 20, 16, 25, 0);

    [Fact]
    public void Idle_steam_with_no_flags_reports_no_operations()
    {
        var result = Classify(new SteamSession(1, SessionStart));
        Assert.Empty(result.Operations);
    }

    [Fact]
    public void Active_download_is_confirmed_and_named_from_manifest()
    {
        var session = Session(
            "[2026-10-20 16:22:21] AppID 5280930 state changed : Update Required,Update Queued,Update Running,",
            "[2026-10-20 16:22:23] AppID 5280930 App update changed : Running Update,Downloading,Staging,",
            "[2026-10-20 16:22:23] AppID 5280930 update started : download 0/40938995792, store 0/0, reuse 0/0, delta 0/0, stage 0/47128058401",
            "[2026-10-20 16:23:36] Current download rate: 58.964 Mbps");
        var manifests = new Dictionary<uint, SteamAppManifest>
        {
            [5280930] = Manifest(5280930, "New Game", SteamAppStateFlags.UpdateRequired),
        };

        var result = Classify(session, manifests);
        var op = Assert.Single(result.Operations, o => o.GameId == "5280930" && o.Key == "steam:5280930");
        Assert.Equal(ActivityState.Downloading, op.State);
        Assert.Equal(DetectionConfidence.Confirmed, op.Confidence);
        Assert.Equal("New Game", op.GameTitle);
        Assert.Equal(40938995792, op.TotalBytes);
        Assert.NotNull(op.BytesPerSecond);
        Assert.Null(op.Progress);
        Assert.Contains("does not save live progress", op.ProgressNote);
    }

    [Fact]
    public void Installed_app_downloading_is_updating()
    {
        var session = Session(
            "[2026-10-20 16:22:21] AppID 570 state changed : Fully Installed,Update Running,",
            "[2026-10-20 16:22:23] AppID 570 App update changed : Running Update,Downloading,");
        var op = Assert.Single(Classify(session).Operations);
        Assert.Equal(ActivityState.Updating, op.State);
    }

    [Fact]
    public void Multiple_downloads_share_a_global_speed()
    {
        var session = Session(
            "[2026-10-20 16:22:21] AppID 1 state changed : Update Running,",
            "[2026-10-20 16:22:21] AppID 2 state changed : Update Running,",
            "[2026-10-20 16:22:22] AppID 1 App update changed : Running Update,Downloading,",
            "[2026-10-20 16:22:22] AppID 2 App update changed : Running Update,Downloading,",
            "[2026-10-20 16:23:36] Current download rate: 20 Mbps");
        var result = Classify(session);
        Assert.Equal(2, result.Operations.Count);
        Assert.All(result.Operations, o => Assert.Null(o.BytesPerSecond));
        Assert.NotNull(result.TotalBytesPerSecond);
        Assert.Contains("one rate", result.TotalSpeedNote);
    }

    [Fact]
    public void Paused_download_is_inactive_and_may_show_saved_progress()
    {
        var session = Session("[2026-10-20 16:22:51] AppID 1172710 state changed : Update Required,Fully Installed,Update Paused,");
        var manifests = new Dictionary<uint, SteamAppManifest>
        {
            [1172710] = Manifest(1172710, "Paused Game", SteamAppStateFlags.FullyInstalled | SteamAppStateFlags.UpdatePaused, 200, 50),
        };
        var op = Assert.Single(Classify(session, manifests).Operations);
        Assert.Equal(ActivityState.Paused, op.State);
        Assert.Equal(DetectionConfidence.Inactive, op.Confidence);
        Assert.Equal(0.25, op.Progress);
        Assert.Contains("Saved by Steam", op.ProgressNote);
    }

    [Fact]
    public void Stale_download_without_corroboration_is_unknown()
    {
        var session = Session(
            "[2026-10-20 16:00:01] AppID 9 state changed : Update Running,",
            "[2026-10-20 16:00:02] AppID 9 App update changed : Running Update,Downloading,");
        var op = Assert.Single(Classify(session).Operations);
        Assert.Equal(DetectionConfidence.Unknown, op.Confidence);
        Assert.Contains("no transfer activity", op.StateDetail);
    }

    [Fact]
    public void Registry_updating_with_fresh_rate_is_confirmed_without_log_state()
    {
        var session = new SteamSession(1, SessionStart);
        session.Apply(SteamContentLogParser.ParseLine("[2026-10-20 16:23:36] Current download rate: 10 Mbps")!);
        var registry = new Dictionary<uint, SteamRegistryApp> { [42] = new(42, true, "Registry Game") };
        var op = Assert.Single(Classify(session, registry: registry).Operations);
        Assert.Equal(DetectionConfidence.Confirmed, op.Confidence);
        Assert.Equal("Registry Game", op.GameTitle);
        Assert.True(op.State is ActivityState.Downloading or ActivityState.Updating);
    }

    [Fact]
    public void Registry_updating_alone_is_probable()
    {
        var registry = new Dictionary<uint, SteamRegistryApp> { [42] = new(42, true, "Maybe") };
        var op = Assert.Single(Classify(new SteamSession(1, SessionStart), registry: registry).Operations);
        Assert.Equal(DetectionConfidence.Probable, op.Confidence);
    }

    [Fact]
    public void Verifying_and_staging_map_to_distinct_states()
    {
        var verifying = Session(
            "[2026-10-20 16:22:21] AppID 1 state changed : Update Running,",
            "[2026-10-20 16:22:22] AppID 1 App update changed : Running Update,Verifying Installed,");
        Assert.Equal(ActivityState.Verifying, Assert.Single(Classify(verifying).Operations).State);

        var staging = Session(
            "[2026-10-20 16:22:21] AppID 2 state changed : Update Running,",
            "[2026-10-20 16:22:22] AppID 2 App update changed : Running Update,Staging,");
        Assert.Equal(ActivityState.Unpacking, Assert.Single(Classify(staging).Operations).State);
    }

    [Fact]
    public void Workshop_content_is_a_separate_operation()
    {
        var session = Session(
            "[2026-10-20 16:22:21] AppID 570 state changed : Fully Installed,",
            "[2026-10-20 16:22:22] AppID 570 Workshop update changed : Running Update,Downloading,");
        var op = Assert.Single(Classify(session).Operations);
        Assert.Equal("steam:570:workshop", op.Key);
        Assert.Equal(ActivityState.Downloading, op.State);
        Assert.Contains("Workshop", op.StateDetail);
    }

    private static SteamSession Session(params string[] lines)
    {
        var session = new SteamSession(1, SessionStart);
        foreach (var line in lines)
        {
            session.Apply(SteamContentLogParser.ParseLine(line) ?? throw new InvalidOperationException(line));
        }

        return session;
    }

    private static SteamClassification Classify(
        SteamSession session,
        IReadOnlyDictionary<uint, SteamAppManifest>? manifests = null,
        IReadOnlyDictionary<uint, SteamRegistryApp>? registry = null) =>
        SteamActivityClassifier.Classify(session, manifests ?? new Dictionary<uint, SteamAppManifest>(), registry ?? new Dictionary<uint, SteamRegistryApp>(), Now);

    private static SteamAppManifest Manifest(uint id, string name, SteamAppStateFlags flags, long total = 0, long done = 0) => new()
    {
        AppId = id,
        Name = name,
        StateFlags = flags,
        BytesToDownload = total,
        BytesDownloaded = done,
        ManifestPath = $"appmanifest_{id}.acf",
        LibraryPath = @"C:\Steam",
        LastWriteTime = Now,
    };
}
