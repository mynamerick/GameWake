using System.Globalization;
using System.Text.RegularExpressions;

namespace GameWake.Providers.Steam;

public enum SteamUpdateChannel
{
    App,
    Workshop,
    Shader,
}

public abstract record SteamLogEvent(DateTime Timestamp);

/// <summary><c>AppID 570 state changed : Update Required,Fully Installed,Update Running, (note)</c></summary>
public sealed record SteamStateChanged(DateTime Timestamp, uint AppId, IReadOnlySet<string> Flags, string? Note) : SteamLogEvent(Timestamp);

/// <summary><c>AppID 570 App update changed : Running Update,Downloading,Staging,</c></summary>
public sealed record SteamPhaseChanged(DateTime Timestamp, uint AppId, SteamUpdateChannel Channel, IReadOnlySet<string> Phases) : SteamLogEvent(Timestamp);

/// <summary><c>AppID 570 update started : download 0/409389, store 0/0, ..., stage 0/47128</c></summary>
public sealed record SteamUpdateStarted(DateTime Timestamp, uint AppId, long DownloadDone, long DownloadTotal, long StageDone, long StageTotal) : SteamLogEvent(Timestamp);

/// <summary><c>AppID 570 finished update, 1 mounted depots ...</c></summary>
public sealed record SteamUpdateFinished(DateTime Timestamp, uint AppId) : SteamLogEvent(Timestamp);

/// <summary><c>AppID 570 update canceled : Disabled (Suspended)</c></summary>
public sealed record SteamUpdateCanceled(DateTime Timestamp, uint AppId, string Reason) : SteamLogEvent(Timestamp);

/// <summary><c>Current download rate: 43.937 Mbps</c> — written about once a minute, only while transferring.</summary>
public sealed record SteamDownloadRate(DateTime Timestamp, double MegabitsPerSecond) : SteamLogEvent(Timestamp)
{
    public double BytesPerSecond => MegabitsPerSecond * 1_000_000 / 8;
}

/// <summary>
/// Parses lines of <c>logs\content_log.txt</c>. The grammar was taken from real Steam logs
/// (see docs/detection-feasibility.md). Lines that do not match are ignored.
/// </summary>
public static partial class SteamContentLogParser
{
    public const string RunningUpdatePhase = "Running Update";

    public static SteamLogEvent? ParseLine(string line)
    {
        var match = LinePattern().Match(line);
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp))
        {
            return null;
        }

        var body = match.Groups["body"].Value;
        if (body.StartsWith("Current download rate", StringComparison.Ordinal))
        {
            var rate = RatePattern().Match(body);
            return rate.Success && double.TryParse(rate.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps)
                ? new SteamDownloadRate(timestamp, mbps)
                : null;
        }

        if (!body.StartsWith("AppID ", StringComparison.Ordinal))
        {
            return null;
        }

        if (StatePattern().Match(body) is { Success: true } state)
        {
            var note = state.Groups["note"].Success ? state.Groups["note"].Value.Trim() : null;
            return new SteamStateChanged(timestamp, ParseAppId(state), SplitList(state.Groups["flags"].Value), string.IsNullOrEmpty(note) ? null : note);
        }

        if (PhasePattern().Match(body) is { Success: true } phase)
        {
            var channel = phase.Groups["channel"].Value switch
            {
                "Workshop" => SteamUpdateChannel.Workshop,
                "Shader" => SteamUpdateChannel.Shader,
                _ => SteamUpdateChannel.App,
            };
            var phases = SplitList(phase.Groups["phases"].Value);
            if (phases.Count == 1 && phases.Contains("None"))
            {
                phases = new HashSet<string>(StringComparer.Ordinal);
            }

            return new SteamPhaseChanged(timestamp, ParseAppId(phase), channel, phases);
        }

        if (StartedPattern().Match(body) is { Success: true } started)
        {
            return new SteamUpdateStarted(
                timestamp,
                ParseAppId(started),
                ParseLong(started.Groups["dl"]),
                ParseLong(started.Groups["dlt"]),
                ParseLong(started.Groups["st"]),
                ParseLong(started.Groups["stt"]));
        }

        if (FinishedPattern().Match(body) is { Success: true } finished)
        {
            return new SteamUpdateFinished(timestamp, ParseAppId(finished));
        }

        if (CanceledPattern().Match(body) is { Success: true } canceled)
        {
            return new SteamUpdateCanceled(timestamp, ParseAppId(canceled), canceled.Groups["reason"].Value.Trim());
        }

        return null;
    }

    private static uint ParseAppId(Match match) =>
        uint.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    private static long ParseLong(Group group) =>
        group.Success && long.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static HashSet<string> SplitList(string list) =>
        new(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);

    [GeneratedRegex(@"^\[(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] (?<body>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"^Current download rate: (?<rate>\d+(?:\.\d+)?) Mbps", RegexOptions.CultureInvariant)]
    private static partial Regex RatePattern();

    [GeneratedRegex(@"^AppID (?<id>\d+) state changed : (?<flags>[^(]*?)\s*(?:\((?<note>[^)]*)\))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StatePattern();

    [GeneratedRegex(@"^AppID (?<id>\d+) (?<channel>App|Workshop|Shader) update changed : (?<phases>.*?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PhasePattern();

    [GeneratedRegex(@"^AppID (?<id>\d+) update started : download (?<dl>\d+)/(?<dlt>\d+)(?:.*?stage (?<st>\d+)/(?<stt>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex StartedPattern();

    [GeneratedRegex(@"^AppID (?<id>\d+) finished update\b", RegexOptions.CultureInvariant)]
    private static partial Regex FinishedPattern();

    [GeneratedRegex(@"^AppID (?<id>\d+) update canceled : (?<reason>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex CanceledPattern();
}

public sealed class SteamChannelLog
{
    /// <summary>Phases from the latest line, e.g. {Running Update, Downloading, Staging}.</summary>
    public IReadOnlySet<string> Phases { get; internal set; } = new HashSet<string>();

    /// <summary>
    /// The latest phase set naming a concrete step. Steam logs a bare "Running Update," between
    /// steps; that transient line does not replace the step being reported.
    /// </summary>
    public IReadOnlySet<string> EffectivePhases { get; internal set; } = new HashSet<string>();

    public DateTime ChangedAt { get; internal set; }

    public bool IsIdle => Phases.Count == 0;
}

public sealed class SteamAppLog(uint appId)
{
    public uint AppId { get; } = appId;

    public IReadOnlySet<string> Flags { get; internal set; } = new HashSet<string>();

    public string? FlagsNote { get; internal set; }

    public DateTime? FlagsAt { get; internal set; }

    public Dictionary<SteamUpdateChannel, SteamChannelLog> Channels { get; } = [];

    public SteamUpdateStarted? LastStart { get; internal set; }

    public DateTime? FinishedAt { get; internal set; }

    public SteamUpdateCanceled? LastCancel { get; internal set; }

    public bool HasFlag(string flag) => Flags.Contains(flag);
}

/// <summary>
/// What Steam's content log has said since the current Steam process started. Lines from earlier
/// sessions are ignored, because Steam re-logs each app's state when it starts and stale lines
/// could otherwise describe downloads that no longer exist.
/// </summary>
public sealed class SteamSession(int processId, DateTime start)
{
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    public int ProcessId { get; } = processId;

    public DateTime Start { get; } = start;

    public Dictionary<uint, SteamAppLog> Apps { get; } = [];

    public SteamDownloadRate? LastRate { get; private set; }

    public DateTime? LastEventAt { get; private set; }

    public int AppliedEvents { get; private set; }

    public int IgnoredEvents { get; private set; }

    public void Apply(SteamLogEvent logEvent)
    {
        if (logEvent.Timestamp < Start - StartTolerance)
        {
            IgnoredEvents++;
            return;
        }

        AppliedEvents++;
        LastEventAt = LastEventAt is { } last && last > logEvent.Timestamp ? last : logEvent.Timestamp;

        switch (logEvent)
        {
            case SteamDownloadRate rate:
                LastRate = rate;
                break;
            case SteamStateChanged state:
                var app = GetApp(state.AppId);
                app.Flags = state.Flags;
                app.FlagsNote = state.Note;
                app.FlagsAt = state.Timestamp;
                break;
            case SteamPhaseChanged phase:
                var channel = GetChannel(GetApp(phase.AppId), phase.Channel);
                channel.Phases = phase.Phases;
                channel.ChangedAt = phase.Timestamp;
                if (phase.Phases.Count == 0)
                {
                    channel.EffectivePhases = phase.Phases;
                }
                else if (phase.Phases.Any(p => p != SteamContentLogParser.RunningUpdatePhase))
                {
                    channel.EffectivePhases = phase.Phases;
                }

                break;
            case SteamUpdateStarted started:
                var startedApp = GetApp(started.AppId);
                startedApp.LastStart = started;
                startedApp.FinishedAt = null;
                break;
            case SteamUpdateFinished finished:
                GetApp(finished.AppId).FinishedAt = finished.Timestamp;
                break;
            case SteamUpdateCanceled canceled:
                GetApp(canceled.AppId).LastCancel = canceled;
                break;
        }
    }

    private SteamAppLog GetApp(uint appId)
    {
        if (!Apps.TryGetValue(appId, out var app))
        {
            app = new SteamAppLog(appId);
            Apps[appId] = app;
        }

        return app;
    }

    private static SteamChannelLog GetChannel(SteamAppLog app, SteamUpdateChannel channel)
    {
        if (!app.Channels.TryGetValue(channel, out var log))
        {
            log = new SteamChannelLog();
            app.Channels[channel] = log;
        }

        return log;
    }
}
