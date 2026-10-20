using System.Globalization;
using GameWake.Core.Models;

namespace GameWake.Providers.Steam;

public sealed record SteamRegistryApp(uint AppId, bool? Updating, string? Name);

public sealed record SteamClassification(
    IReadOnlyList<GameOperation> Operations,
    double? TotalBytesPerSecond,
    string? TotalSpeedNote);

/// <summary>
/// Turns Steam's own evidence (content log, per-app registry flags, app manifests) into game
/// operations. Pure and deterministic so every rule is unit-tested. Only called while Steam is
/// running; when it is not, nothing Steam wrote earlier is trusted.
/// </summary>
public static class SteamActivityClassifier
{
    /// <summary>Steam logs the download rate about once a minute while transferring.</summary>
    public static readonly TimeSpan RateFreshness = TimeSpan.FromSeconds(150);

    /// <summary>A transfer phase with no corroboration for this long is no longer trusted.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan CompletedDisplayTime = TimeSpan.FromMinutes(2);

    private const string UpdateRunning = "Update Running";
    private const string UpdatePaused = "Update Paused";
    private const string UpdateQueued = "Update Queued";
    private const string UpdateStarted = "Update Started";
    private const string FullyInstalled = "Fully Installed";
    private const string MovingFolder = "Moving Folder";

    public static SteamClassification Classify(
        SteamSession session,
        IReadOnlyDictionary<uint, SteamAppManifest> manifests,
        IReadOnlyDictionary<uint, SteamRegistryApp> registry,
        DateTime now)
    {
        var rate = session.LastRate;
        var rateFresh = rate is not null && rate.MegabitsPerSecond > 0 && now - rate.Timestamp <= RateFreshness;

        var appIds = new SortedSet<uint>(session.Apps.Keys);
        appIds.UnionWith(registry.Values.Where(r => r.Updating == true).Select(r => r.AppId));
        appIds.UnionWith(manifests.Values.Where(m => m.StateFlags.HasFlag(SteamAppStateFlags.UpdatePaused)).Select(m => m.AppId));

        var results = new List<Classified>();
        foreach (var appId in appIds)
        {
            session.Apps.TryGetValue(appId, out var log);
            manifests.TryGetValue(appId, out var manifest);
            registry.TryGetValue(appId, out var registryApp);
            var context = new AppContext(appId, log, manifest, registryApp, session, rate, rateFresh, now);

            if (ClassifyApp(context) is { } app)
            {
                results.Add(app);
            }

            if (log is not null)
            {
                foreach (var channel in new[] { SteamUpdateChannel.Workshop, SteamUpdateChannel.Shader })
                {
                    if (ClassifyChannel(context, channel) is { } content)
                    {
                        results.Add(content);
                    }
                }
            }
        }

        double? totalSpeed = null;
        string? totalSpeedNote = null;
        var transferring = results.Where(r => r.Transferring && r.Operation.Confidence >= DetectionConfidence.Probable).ToList();
        if (rateFresh && rate is not null)
        {
            var note = $"Measured by Steam at {rate.Timestamp:HH:mm:ss}";
            if (transferring.Count == 1)
            {
                var single = transferring[0];
                single.Operation = single.Operation with { BytesPerSecond = rate.BytesPerSecond, SpeedNote = note };
            }
            else
            {
                totalSpeed = rate.BytesPerSecond;
                totalSpeedNote = transferring.Count > 1
                    ? $"{note}; Steam reports one rate for all downloads"
                    : note;
            }
        }

        var operations = results
            .Select(r => r.Operation)
            .OrderByDescending(o => o.State.SummaryPriority())
            .ThenBy(o => o.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new SteamClassification(operations, totalSpeed, totalSpeedNote);
    }

    private static Classified? ClassifyApp(AppContext c)
    {
        var log = c.Log;
        var hasLogState = log?.FlagsAt is not null;
        var installed = hasLogState ? log!.HasFlag(FullyInstalled) : c.Manifest?.IsFullyInstalled ?? false;

        if (hasLogState && log!.HasFlag(MovingFolder))
        {
            return Build(c, ActivityState.Installing, DetectionConfidence.Confirmed, "Moving the install folder", transferring: false,
                LogEvidence(c));
        }

        if (hasLogState && log!.HasFlag(UpdateRunning))
        {
            var channel = log.Channels.GetValueOrDefault(SteamUpdateChannel.App);
            var phases = channel?.EffectivePhases ?? new HashSet<string>();
            if (channel is not null && channel.Phases.Contains("Stopping"))
            {
                return Build(c, ActivityState.Paused, DetectionConfidence.Inactive, "Stopping", transferring: false, LogEvidence(c), useSavedProgress: true);
            }

            var (state, detail, transferring) = MapPhases(phases, installed);
            var evidence = LogEvidence(c, includeRate: transferring);
            var confidence = DetectionConfidence.Confirmed;

            if (transferring && !c.RateFresh && c.Registry?.Updating != true)
            {
                var lastSign = Latest(channel?.ChangedAt, c.Rate?.Timestamp, log.FlagsAt);
                if (c.Now - lastSign > StaleAfter)
                {
                    confidence = DetectionConfidence.Unknown;
                    detail = $"Steam has reported no transfer activity for {(int)(c.Now - lastSign).TotalMinutes} minutes";
                    evidence.Add("Neither the registry flag nor a recent download rate confirms the transfer");
                }
            }

            return Build(c, state, confidence, detail, transferring, evidence);
        }

        if (hasLogState)
        {
            if (log!.HasFlag(UpdatePaused))
            {
                return Build(c, ActivityState.Paused, DetectionConfidence.Inactive, "Paused in Steam", transferring: false, LogEvidence(c), useSavedProgress: true);
            }

            var suspended = log.FlagsNote?.Contains("Suspended", StringComparison.OrdinalIgnoreCase) == true;
            if (suspended && (log.HasFlag(UpdateStarted) || log.HasFlag(UpdateQueued)))
            {
                return Build(c, ActivityState.Paused, DetectionConfidence.Inactive, "Suspended by Steam (for example while a game is running)",
                    transferring: false, LogEvidence(c), useSavedProgress: true);
            }

            if (log.HasFlag(UpdateQueued))
            {
                var scheduled = log.FlagsNote?.Contains("delayed", StringComparison.OrdinalIgnoreCase) == true;
                return Build(c, ActivityState.Queued, DetectionConfidence.Inactive,
                    scheduled ? "Scheduled by Steam" : "Waiting in Steam's download queue", transferring: false, LogEvidence(c), useSavedProgress: true);
            }

            if (log.FinishedAt is { } finishedAt && c.Now - finishedAt <= CompletedDisplayTime)
            {
                return Build(c, ActivityState.Completed, DetectionConfidence.Inactive, $"Finished at {finishedAt:HH:mm:ss}", transferring: false,
                    LogEvidence(c));
            }

            if (c.Registry?.Updating == true)
            {
                var evidence = LogEvidence(c);
                evidence.Add("Steam's log and registry disagree; not treating this as an active download");
                return Build(c, ActivityState.Unknown, DetectionConfidence.Unknown, "Steam's registry and log disagree", transferring: false, evidence);
            }

            return null;
        }

        if (c.Registry?.Updating == true)
        {
            var evidence = new List<string> { $@"Registry: HKCU\Software\Valve\Steam\Apps\{c.AppId}\Updating = 1" };
            if (c.RateFresh)
            {
                evidence.Add(RateEvidence(c.Rate!));
            }

            return Build(c, installed ? ActivityState.Updating : ActivityState.Downloading,
                c.RateFresh ? DetectionConfidence.Confirmed : DetectionConfidence.Probable,
                "Detected from Steam's registry flag (no log entry this session)", transferring: true, evidence);
        }

        if (c.Manifest?.StateFlags.HasFlag(SteamAppStateFlags.UpdatePaused) == true)
        {
            return Build(c, ActivityState.Paused, DetectionConfidence.Inactive, "Paused (from Steam's app manifest)", transferring: false,
                [$"App manifest StateFlags = {(long)c.Manifest.StateFlags} (includes UpdatePaused)"], useSavedProgress: true);
        }

        return null;
    }

    private static Classified? ClassifyChannel(AppContext c, SteamUpdateChannel channelKind)
    {
        var channel = c.Log!.Channels.GetValueOrDefault(channelKind);
        if (channel is null || channel.IsIdle || channel.EffectivePhases.Count == 0 || channel.Phases.Contains("Stopping"))
        {
            return null;
        }

        var (state, _, transferring) = MapPhases(channel.EffectivePhases, installed: false);
        var label = channelKind == SteamUpdateChannel.Workshop ? "Workshop content" : "Shader pre-cache";
        var evidence = new List<string>
        {
            $"Steam log: {channelKind} update phase '{string.Join(", ", channel.EffectivePhases)}' at {channel.ChangedAt:HH:mm:ss}",
        };
        if (c.RateFresh)
        {
            evidence.Add(RateEvidence(c.Rate!));
        }

        var confidence = DetectionConfidence.Confirmed;
        var detail = label;
        var lastSign = Latest(channel.ChangedAt, c.Rate?.Timestamp, null);
        if (!c.RateFresh && c.Now - lastSign > StaleAfter)
        {
            confidence = DetectionConfidence.Unknown;
            detail = $"{label}: no activity reported for {(int)(c.Now - lastSign).TotalMinutes} minutes";
        }

        var operation = BaseOperation(c, $"steam:{c.AppId}:{channelKind.ToString().ToLowerInvariant()}") with
        {
            State = state,
            Confidence = confidence,
            StateDetail = detail,
            Evidence = evidence,
            ProgressNote = "Steam does not publish live progress for workshop or shader downloads",
        };
        return new Classified(operation, transferring);
    }

    private static (ActivityState State, string? Detail, bool Transferring) MapPhases(IReadOnlySet<string> phases, bool installed)
    {
        if (phases.Contains("Downloading"))
        {
            return (installed ? ActivityState.Updating : ActivityState.Downloading, null, true);
        }

        if (phases.Contains("Verifying Installed") || phases.Contains("Verifying Staged") || phases.Contains("Validating"))
        {
            return (ActivityState.Verifying, "Verifying files", false);
        }

        if (phases.Contains("Committing"))
        {
            return (ActivityState.Installing, "Committing files", false);
        }

        if (phases.Contains("Preallocating"))
        {
            return (ActivityState.Installing, "Allocating disk space", false);
        }

        if (phases.Contains("Reconfiguring"))
        {
            return (ActivityState.Installing, "Reconfiguring", false);
        }

        if (phases.Contains("Staging"))
        {
            return (ActivityState.Unpacking, "Staging downloaded content", false);
        }

        return (installed ? ActivityState.Updating : ActivityState.Downloading, "Preparing", false);
    }

    private static Classified Build(
        AppContext c,
        ActivityState state,
        DetectionConfidence confidence,
        string? detail,
        bool transferring,
        List<string> evidence,
        bool useSavedProgress = false)
    {
        var operation = BaseOperation(c, $"steam:{c.AppId}") with
        {
            State = state,
            Confidence = confidence,
            StateDetail = detail,
            Evidence = evidence,
        };

        var manifest = c.Manifest;
        long? total = c.Log?.LastStart is { DownloadTotal: > 0 } start ? start.DownloadTotal
            : manifest is { BytesToDownload: > 0 } ? manifest.BytesToDownload
            : null;
        operation = operation with { TotalBytes = total };

        if (useSavedProgress && manifest is { BytesToDownload: > 0 })
        {
            var fraction = Math.Clamp((double)manifest.BytesDownloaded / manifest.BytesToDownload, 0, 1);
            operation = operation with
            {
                Progress = fraction,
                TotalBytes = manifest.BytesToDownload,
                ProgressNote = $"Saved by Steam at {manifest.LastWriteTime:HH:mm}",
            };
        }
        else if (state.IsWorking())
        {
            operation = operation with { ProgressNote = "Steam does not save live progress to disk while it works" };
        }

        return new Classified(operation, transferring);
    }

    private static GameOperation BaseOperation(AppContext c, string key)
    {
        var title = c.Manifest?.Name ?? c.Registry?.Name;
        return new GameOperation
        {
            Key = key,
            Launcher = LauncherKind.Steam,
            GameId = c.AppId.ToString(CultureInfo.InvariantCulture),
            GameTitle = title,
            TitleNote = title is null ? $"Steam has not saved a name for app {c.AppId} yet" : null,
            State = ActivityState.Unknown,
            Confidence = DetectionConfidence.Unknown,
            EvidenceKind = EvidenceKind.Direct,
        };
    }

    private static List<string> LogEvidence(AppContext c, bool includeRate = false)
    {
        var evidence = new List<string>();
        var log = c.Log;
        if (log?.FlagsAt is { } flagsAt)
        {
            var note = log.FlagsNote is null ? string.Empty : $" ({log.FlagsNote})";
            evidence.Add($"Steam log at {flagsAt:HH:mm:ss}: state '{string.Join(", ", log.Flags)}'{note}");
        }

        if (log?.Channels.GetValueOrDefault(SteamUpdateChannel.App) is { EffectivePhases.Count: > 0 } channel)
        {
            evidence.Add($"Steam log at {channel.ChangedAt:HH:mm:ss}: phase '{string.Join(", ", channel.EffectivePhases)}'");
        }

        if (c.Registry?.Updating is { } updating)
        {
            evidence.Add($@"Registry: HKCU\Software\Valve\Steam\Apps\{c.AppId}\Updating = {(updating ? 1 : 0)}");
        }

        if (includeRate && c.RateFresh)
        {
            evidence.Add(RateEvidence(c.Rate!));
        }

        return evidence;
    }

    private static string RateEvidence(SteamDownloadRate rate) =>
        $"Steam log at {rate.Timestamp:HH:mm:ss}: download rate {rate.MegabitsPerSecond.ToString("0.0", CultureInfo.InvariantCulture)} Mbps";

    private static DateTime Latest(DateTime? a, DateTime? b, DateTime? c) =>
        new[] { a, b, c }.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(DateTime.MinValue).Max();

    private sealed record AppContext(
        uint AppId,
        SteamAppLog? Log,
        SteamAppManifest? Manifest,
        SteamRegistryApp? Registry,
        SteamSession Session,
        SteamDownloadRate? Rate,
        bool RateFresh,
        DateTime Now);

    private sealed class Classified(GameOperation operation, bool transferring)
    {
        public GameOperation Operation { get; set; } = operation;

        public bool Transferring { get; } = transferring;
    }
}
