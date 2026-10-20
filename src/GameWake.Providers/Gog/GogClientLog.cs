using System.Globalization;
using System.Text.RegularExpressions;
using GameWake.Core.Models;

namespace GameWake.Providers.Gog;

public enum GogJobStatus
{
    Working,
    Paused,
    Queued,
    Finished,
    Ended,
    Unrecognized,
}

public abstract record GogLogEvent(DateTime? Timestamp);

/// <summary><c>Job 'ProductInstall (1207658924) id: 3 state: Running' state changed</c></summary>
public sealed record GogJobChanged(DateTime? Timestamp, string JobType, string ProductId, string JobId, string StateWord) : GogLogEvent(Timestamp);

/// <summary><c>Processing update request for product ID: 1207658924, target path: D:\GOG Games\X</c></summary>
public sealed record GogTargetPath(DateTime? Timestamp, string ProductId, string Path) : GogLogEvent(Timestamp);

/// <summary>
/// Parses the GOG Galaxy client log. Only the job-state line has been confirmed from public logs;
/// the state and job-type keywords are mapped conservatively and anything unrecognised is
/// reported as unknown rather than guessed.
/// </summary>
public static partial class GogClientLogParser
{
    public static GogLogEvent? ParseLine(string line)
    {
        if (line.Length > 4096)
        {
            return null;
        }

        var job = JobPattern().Match(line);
        if (job.Success)
        {
            return new GogJobChanged(
                ParseTimestamp(line),
                job.Groups["type"].Value,
                job.Groups["product"].Value,
                job.Groups["job"].Value,
                job.Groups["state"].Value);
        }

        var target = TargetPattern().Match(line);
        if (target.Success)
        {
            var path = target.Groups["path"].Value.Trim().Trim('"', '\'');
            return path.Length == 0 ? null : new GogTargetPath(ParseTimestamp(line), target.Groups["product"].Value, path);
        }

        return null;
    }

    /// <summary>Galaxy writes lines such as <c>2024-03-01 12:34:56.789 [Information]...</c>; returns null when there is no timestamp.</summary>
    public static DateTime? ParseTimestamp(string line)
    {
        var match = TimestampPattern().Match(line);
        return match.Success && DateTime.TryParseExact(
            $"{match.Groups["date"].Value} {match.Groups["time"].Value}",
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var timestamp)
            ? timestamp
            : null;
    }

    public static GogJobStatus ClassifyState(string stateWord) => stateWord.ToLowerInvariant() switch
    {
        "running" or "started" or "starting" or "inprogress" or "active" or "resumed" or "resuming"
            or "downloading" or "installing" or "updating" or "verifying" or "repairing" => GogJobStatus.Working,
        "paused" or "pausing" or "suspended" => GogJobStatus.Paused,
        "queued" or "pending" or "waiting" or "scheduled" or "created" or "enqueued" => GogJobStatus.Queued,
        "finished" or "completed" or "complete" or "succeeded" or "success" or "done" => GogJobStatus.Finished,
        "failed" or "error" or "canceled" or "cancelled" or "aborted" or "stopped" or "removed" => GogJobStatus.Ended,
        _ => GogJobStatus.Unrecognized,
    };

    /// <summary>Returns null for job types that never involve a download (e.g. uninstall).</summary>
    public static ActivityState? ClassifyJobType(string jobType)
    {
        if (jobType.Contains("Uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (jobType.Contains("Verify", StringComparison.OrdinalIgnoreCase)
            || jobType.Contains("Repair", StringComparison.OrdinalIgnoreCase)
            || jobType.Contains("Validat", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityState.Verifying;
        }

        if (jobType.Contains("Update", StringComparison.OrdinalIgnoreCase) || jobType.Contains("Patch", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityState.Updating;
        }

        if (jobType.Contains("Install", StringComparison.OrdinalIgnoreCase) || jobType.Contains("Download", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityState.Downloading;
        }

        if (jobType.Contains("Move", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityState.Installing;
        }

        return ActivityState.Unknown;
    }

    [GeneratedRegex(@"Job '(?<type>[A-Za-z]+) \((?<product>\d+)\) id: (?<job>\d+) state: (?<state>[A-Za-z]+)'", RegexOptions.CultureInvariant)]
    private static partial Regex JobPattern();

    [GeneratedRegex(@"Processing \w+ request for product ID:?\s*(?<product>\d+).*?target path:?\s*(?<path>.+?)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TargetPattern();

    [GeneratedRegex(@"^\[?(?<date>\d{4}-\d{2}-\d{2})[ T](?<time>\d{2}:\d{2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
}

public sealed record GogJob(
    string ProductId,
    string JobId,
    string JobType,
    string StateWord,
    GogJobStatus Status,
    DateTime ChangedAt);

/// <summary>Latest state of each Galaxy job, built from log lines of the current Galaxy session.</summary>
public sealed class GogJobTracker
{
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, GogJob> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _productLogActivity = new(StringComparer.Ordinal);

    public GogJobTracker(DateTime sessionStart)
    {
        SessionStart = sessionStart;
    }

    public DateTime SessionStart { get; }

    public IReadOnlyDictionary<string, string> TargetPaths => _targets;

    public int IgnoredLines { get; private set; }

    /// <summary>
    /// Applies one log line. <paramref name="readAt"/> stands in for the time of lines without a
    /// timestamp; such lines are only trusted when they were read incrementally.
    /// </summary>
    public void Apply(string line, DateTime readAt, bool incremental)
    {
        var parsed = GogClientLogParser.ParseLine(line);
        var timestamp = parsed?.Timestamp ?? GogClientLogParser.ParseTimestamp(line);
        if (timestamp is null && !incremental)
        {
            IgnoredLines++;
            return;
        }

        var at = timestamp ?? readAt;
        if (at < SessionStart - StartTolerance)
        {
            IgnoredLines++;
            return;
        }

        switch (parsed)
        {
            case GogJobChanged job:
                var key = $"{job.ProductId}:{job.JobId}";
                _jobs[key] = new GogJob(job.ProductId, job.JobId, job.JobType, job.StateWord, GogClientLogParser.ClassifyState(job.StateWord), at);
                _productLogActivity[job.ProductId] = at;
                break;
            case GogTargetPath target:
                _targets[target.ProductId] = target.Path;
                _productLogActivity[target.ProductId] = at;
                break;
            default:
                foreach (var productId in _jobs.Values.Where(j => j.Status == GogJobStatus.Working).Select(j => j.ProductId).Distinct())
                {
                    if (line.Contains(productId, StringComparison.Ordinal))
                    {
                        _productLogActivity[productId] = at;
                    }
                }

                break;
        }
    }

    /// <summary>The newest job per product, excluding jobs that ended (failed, canceled).</summary>
    public IReadOnlyList<GogJob> CurrentJobs(DateTime now, TimeSpan finishedDisplayTime) =>
        _jobs.Values
            .GroupBy(j => j.ProductId, StringComparer.Ordinal)
            .Select(g => g.MaxBy(j => j.ChangedAt)!)
            .Where(j => j.Status != GogJobStatus.Ended)
            .Where(j => j.Status != GogJobStatus.Finished || now - j.ChangedAt <= finishedDisplayTime)
            .ToList();

    public DateTime? LastLogActivity(string productId) =>
        _productLogActivity.TryGetValue(productId, out var at) ? at : null;
}
