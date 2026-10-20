using GameWake.Core.Infrastructure;
using GameWake.Core.Models;
using GameWake.Core.Providers;
using GameWake.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GameWake.Providers.Gog;

public sealed record GogPaths(string? ClientExecutable, string? LogFile)
{
    public bool IsInstalled => ClientExecutable is not null;
}

public static class GogLocator
{
    public const string GamesKey = @"SOFTWARE\WOW6432Node\GOG.com\Games";

    public static GogPaths Locate(IRegistryReader registry)
    {
        string? executable = null;
        var clientDir = registry.GetString(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths", "client");
        var clientExe = registry.GetString(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient", "clientExecutable") ?? "GalaxyClient.exe";
        if (!string.IsNullOrWhiteSpace(clientDir) && File.Exists(Path.Combine(clientDir, clientExe)))
        {
            executable = Path.Combine(clientDir, clientExe);
        }
        else
        {
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GOG Galaxy", "GalaxyClient.exe");
            executable = File.Exists(fallback) ? fallback : null;
        }

        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GOG.com", "Galaxy", "logs", "GalaxyClient.log");
        return new GogPaths(executable, log);
    }
}

/// <summary>
/// Reads Galaxy's job-state log lines for the current Galaxy session and corroborates running
/// jobs with file writes in the game's target folder or further log lines about the product.
/// The GOG database is deliberately not opened (see docs/detection-feasibility.md).
/// </summary>
public sealed class GogDownloadProvider : LauncherProviderBase
{
    public static readonly TimeSpan CorroborationWindow = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan FinishedDisplayTime = TimeSpan.FromMinutes(2);

    private static readonly string[] StaticLimitations =
    [
        "Progress and speed are not available: GOG Galaxy does not expose them outside the client.",
        "Galaxy's log format is not documented; job states GameWake does not recognise are shown as Unknown.",
    ];

    private readonly IRegistryReader _registry;
    private readonly IProcessInspector _processes;
    private readonly Func<GogPaths> _locate;
    private readonly Dictionary<string, DirectoryActivityMonitor> _monitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string? Name, string? Path)> _registryGames = new(StringComparer.Ordinal);

    private GogJobTracker? _tracker;
    private IncrementalLogReader? _logReader;
    private int? _sessionProcessId;

    public GogDownloadProvider(
        IClock clock,
        ILogger<GogDownloadProvider> logger,
        IRegistryReader registry,
        IProcessInspector processes,
        Func<GogPaths>? locate = null)
        : base(clock, logger)
    {
        _registry = registry;
        _processes = processes;
        _locate = locate ?? (() => GogLocator.Locate(registry));
    }

    public override LauncherKind Launcher => LauncherKind.Gog;

    protected override ValueTask<LauncherSnapshot> PollCoreAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Poll(now));
    }

    private LauncherSnapshot Poll(DateTimeOffset now)
    {
        var paths = _locate();
        if (!paths.IsInstalled)
        {
            ResetSession();
            return new LauncherSnapshot
            {
                Launcher = LauncherKind.Gog,
                CheckedAt = now,
                IsInstalled = false,
                StatusDetail = "GOG Galaxy is not installed",
            };
        }

        var process = _processes.FindByName("GalaxyClient").OrderBy(p => p.StartTime ?? DateTime.MaxValue).FirstOrDefault();
        if (process is null)
        {
            ResetSession();
            return new LauncherSnapshot
            {
                Launcher = LauncherKind.Gog,
                CheckedAt = now,
                IsInstalled = true,
                IsRunning = false,
                StatusDetail = "GOG Galaxy is not running",
                ExecutablePath = paths.ClientExecutable,
                Limitations = StaticLimitations,
                Diagnostics = [$"Client: {paths.ClientExecutable}", $"Log: {paths.LogFile}"],
            };
        }

        var limitations = new List<string>(StaticLimitations);
        string? error = null;
        if (_tracker is null || _sessionProcessId != process.Id)
        {
            ResetSession();
            _sessionProcessId = process.Id;
            _tracker = new GogJobTracker(process.StartTime ?? now.LocalDateTime);
            if (process.StartTime is null)
            {
                limitations.Add("Galaxy's start time could not be read, so only log lines written after GameWake started are used.");
            }
        }

        if (paths.LogFile is not null)
        {
            ReadLog(paths.LogFile, now, limitations, ref error);
        }

        var jobs = _tracker.CurrentJobs(now.LocalDateTime, FinishedDisplayTime);
        var operations = new List<GameOperation>();
        foreach (var job in jobs)
        {
            var kind = GogClientLogParser.ClassifyJobType(job.JobType);
            if (kind is null)
            {
                continue;
            }

            var target = ResolveTarget(job.ProductId);
            EnsureMonitor(job, target);
            operations.Add(Classify(job, kind.Value, target, now));
        }

        foreach (var stale in _monitors.Keys.Where(k => !jobs.Any(j => j.Status == GogJobStatus.Working && j.ProductId == k)).ToList())
        {
            _monitors[stale].Dispose();
            _monitors.Remove(stale);
        }

        return new LauncherSnapshot
        {
            Launcher = LauncherKind.Gog,
            CheckedAt = now,
            IsInstalled = true,
            IsRunning = true,
            StatusDetail = $"GOG Galaxy is running (session started {_tracker.SessionStart:HH:mm})",
            ExecutablePath = paths.ClientExecutable,
            Operations = operations.OrderBy(o => o.DisplayTitle, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Error = error,
            Limitations = limitations,
            Diagnostics =
            [
                $"Client: {paths.ClientExecutable}",
                $"Log: {paths.LogFile} (read to byte {_logReader?.Position ?? 0:N0})",
                $"Galaxy process {process.Id}, session started {_tracker.SessionStart:yyyy-MM-dd HH:mm:ss}",
                $"Jobs tracked this session: {jobs.Count}; log lines from earlier sessions or without timestamps ignored: {_tracker.IgnoredLines:N0}",
                .. _monitors.Select(m => $"Watching {m.Value.Path} for product {m.Key}{(m.Value.IsWatching ? string.Empty : $" (not active: {m.Value.Error})")}"),
            ],
        };
    }

    private void ReadLog(string logFile, DateTimeOffset now, List<string> limitations, ref string? error)
    {
        var incremental = _logReader is not null;
        _logReader ??= new IncrementalLogReader(logFile);
        try
        {
            var result = _logReader.ReadNewLines();
            if (result.FileMissing)
            {
                limitations.Add("Galaxy's client log was not found, so Galaxy jobs cannot be detected.");
                return;
            }

            foreach (var line in result.Lines)
            {
                _tracker!.Apply(line, now.LocalDateTime, incremental && !result.Restarted);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Galaxy's client log could not be read: {ex.Message}";
        }
    }

    private GameOperation Classify(GogJob job, ActivityState kind, string? target, DateTimeOffset now)
    {
        var (registryName, _) = GetRegistryGame(job.ProductId);
        var folderName = target is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(target));
        var title = registryName ?? folderName;
        var titleNote = registryName is not null ? null
            : folderName is not null ? "Name taken from the install folder"
            : $"Galaxy has not registered a name yet; GOG product {job.ProductId}";

        var evidence = new List<string>
        {
            $"Galaxy log at {job.ChangedAt:HH:mm:ss}: {job.JobType} job {job.JobId} state '{job.StateWord}'",
        };

        DateTime? lastWrite = null;
        if (_monitors.TryGetValue(job.ProductId, out var monitor))
        {
            foreach (var at in monitor.ActivityByBucket().Values.Append(monitor.LastOverflow ?? DateTimeOffset.MinValue))
            {
                if (at > DateTimeOffset.MinValue && (lastWrite is null || at.LocalDateTime > lastWrite))
                {
                    lastWrite = at.LocalDateTime;
                }
            }

            if (lastWrite is { } written)
            {
                evidence.Add($"Files written in {monitor.Path} at {written:HH:mm:ss}");
            }
        }

        var logActivity = _tracker!.LastLogActivity(job.ProductId);
        if (logActivity is { } logged && logged > job.ChangedAt)
        {
            evidence.Add($"Galaxy log mentioned this product at {logged:HH:mm:ss}");
        }

        var nowLocal = now.LocalDateTime;
        var operation = new GameOperation
        {
            Key = $"gog:{job.ProductId}",
            Launcher = LauncherKind.Gog,
            GameId = job.ProductId,
            GameTitle = title,
            TitleNote = titleNote,
            State = kind,
            Confidence = DetectionConfidence.Inactive,
            EvidenceKind = EvidenceKind.Direct,
            ProgressNote = "GOG Galaxy does not expose progress locally",
            SpeedNote = "GOG Galaxy does not expose download speed locally",
            Evidence = evidence,
        };

        switch (job.Status)
        {
            case GogJobStatus.Paused:
                return operation with { State = ActivityState.Paused, StateDetail = "Paused in GOG Galaxy" };
            case GogJobStatus.Queued:
                return operation with { State = ActivityState.Queued, StateDetail = "Waiting in GOG Galaxy's queue" };
            case GogJobStatus.Finished:
                return operation with { State = ActivityState.Completed, StateDetail = $"Finished at {job.ChangedAt:HH:mm:ss}" };
            case GogJobStatus.Unrecognized:
                return operation with
                {
                    State = ActivityState.Unknown,
                    Confidence = DetectionConfidence.Unknown,
                    StateDetail = $"Galaxy reported an unrecognised job state '{job.StateWord}'",
                };
        }

        if (kind == ActivityState.Unknown)
        {
            return operation with
            {
                Confidence = DetectionConfidence.Unknown,
                StateDetail = $"Galaxy is running an unrecognised job type '{job.JobType}'",
            };
        }

        var corroborated = (lastWrite is { } w && nowLocal - w <= CorroborationWindow)
            || (logActivity is { } l && l > job.ChangedAt && nowLocal - l <= CorroborationWindow);
        var lastSign = new[] { job.ChangedAt, lastWrite ?? DateTime.MinValue, logActivity ?? DateTime.MinValue }.Max();
        if (corroborated)
        {
            return operation with { Confidence = DetectionConfidence.Confirmed, StateDetail = "Galaxy reports the job running and files are changing" };
        }

        if (nowLocal - lastSign > StaleAfter)
        {
            return operation with
            {
                State = ActivityState.Unknown,
                Confidence = DetectionConfidence.Unknown,
                StateDetail = $"Galaxy reported the job running, but nothing has changed for {(int)(nowLocal - lastSign).TotalMinutes} minutes",
            };
        }

        return operation with { Confidence = DetectionConfidence.Probable, StateDetail = "Galaxy reports the job running; no file activity seen yet" };
    }

    private string? ResolveTarget(string productId)
    {
        if (_tracker!.TargetPaths.TryGetValue(productId, out var fromLog))
        {
            return fromLog;
        }

        return GetRegistryGame(productId).Path;
    }

    private (string? Name, string? Path) GetRegistryGame(string productId)
    {
        if (!_registryGames.TryGetValue(productId, out var game) || game.Name is null)
        {
            var key = $@"{GogLocator.GamesKey}\{productId}";
            var name = _registry.GetString(RegistryHive.LocalMachine, key, "gameName");
            var path = _registry.GetString(RegistryHive.LocalMachine, key, "path");
            game = (string.IsNullOrWhiteSpace(name) ? null : name.Trim(), string.IsNullOrWhiteSpace(path) ? null : path.Trim());
            _registryGames[productId] = game;
        }

        return game;
    }

    private void EnsureMonitor(GogJob job, string? target)
    {
        if (job.Status != GogJobStatus.Working || target is null || _monitors.ContainsKey(job.ProductId))
        {
            return;
        }

        if (Directory.Exists(target))
        {
            _monitors[job.ProductId] = new DirectoryActivityMonitor(target, Clock);
            return;
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(target));
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(target));
        if (parent is not null && Directory.Exists(parent))
        {
            _monitors[job.ProductId] = new DirectoryActivityMonitor(parent, Clock, relative =>
                relative.Equals(leaf, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(leaf + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? DirectoryActivityMonitor.WholeFolder
                    : null);
        }
    }

    private void ResetSession()
    {
        foreach (var monitor in _monitors.Values)
        {
            monitor.Dispose();
        }

        _monitors.Clear();
        _tracker = null;
        _logReader = null;
        _sessionProcessId = null;
        _registryGames.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ResetSession();
        }
    }
}
