using GameWake.Core.Infrastructure;
using GameWake.Core.Models;
using GameWake.Core.Providers;
using GameWake.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GameWake.Providers.Epic;

public sealed record EpicPaths(string? DataDirectory, string? LauncherExecutable, string? LogFile)
{
    public bool IsInstalled => LauncherExecutable is not null || (DataDirectory is not null && Directory.Exists(DataDirectory));

    public string? ManifestDirectory => DataDirectory is null ? null : Path.Combine(DataDirectory, "Manifests");

    public string? PendingDirectory => DataDirectory is null ? null : Path.Combine(DataDirectory, "Manifests", "Pending");
}

public static class EpicLocator
{
    public static EpicPaths Locate(IRegistryReader registry)
    {
        var data = registry.GetString(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher", "AppDataPath");
        if (string.IsNullOrWhiteSpace(data) || !Directory.Exists(data))
        {
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data");
        }

        return new EpicPaths(
            Directory.Exists(data) ? Path.TrimEndingDirectorySeparator(data) : null,
            FindExecutable(registry),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "Logs", "EpicGamesLauncher.log"));
    }

    private static string? FindExecutable(IRegistryReader registry)
    {
        var command = registry.GetString(RegistryHive.LocalMachine, @"SOFTWARE\Classes\com.epicgames.launcher\shell\open\command", string.Empty);
        if (ExtractExecutable(command) is { } fromProtocol && File.Exists(fromProtocol))
        {
            return fromProtocol;
        }

        foreach (var programFiles in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            foreach (var platform in new[] { "Win64", "Win32" })
            {
                var candidate = Path.Combine(Environment.GetFolderPath(programFiles), "Epic Games", "Launcher", "Portal", "Binaries", platform, "EpicGamesLauncher.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : null;
    }
}

/// <summary>
/// Epic exposes no local per-game download state, so this provider combines three indirect
/// signals: a pending manifest (an install or update has been started and not finished), the
/// launcher process, and files being written in that game's folders. The result is at most
/// Probable. See docs/detection-feasibility.md.
/// </summary>
public sealed class EpicDownloadProvider : LauncherProviderBase
{
    public static readonly TimeSpan WriteFreshness = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan InstalledRefreshInterval = TimeSpan.FromSeconds(60);
    private const string StagingBucket = "staging";
    private const string InstallBucket = "install";

    private static readonly string[] StaticLimitations =
    [
        "Epic does not record download state locally, so Epic activity is at most Probable and only prevents sleep when \"Include probable activity\" is on.",
        "Progress and speed are not available: Epic does not expose them outside the launcher.",
        "A paused or queued Epic download cannot be told apart from a stalled one.",
    ];

    private readonly IProcessInspector _processes;
    private readonly Func<EpicPaths> _locate;
    private readonly EpicManifestCache _pendingCache = new();
    private readonly EpicManifestCache _installedCache = new();
    private readonly Dictionary<string, DirectoryActivityMonitor> _monitors = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<EpicManifest> _installed = [];
    private DateTimeOffset _installedRefreshedAt = DateTimeOffset.MinValue;
    private IncrementalLogReader? _logReader;
    private DateTimeOffset? _lastPatcherLogLine;

    public EpicDownloadProvider(
        IClock clock,
        ILogger<EpicDownloadProvider> logger,
        IRegistryReader registry,
        IProcessInspector processes,
        Func<EpicPaths>? locate = null)
        : base(clock, logger)
    {
        _processes = processes;
        _locate = locate ?? (() => EpicLocator.Locate(registry));
    }

    public override LauncherKind Launcher => LauncherKind.Epic;

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
            StopWatching();
            return new LauncherSnapshot
            {
                Launcher = LauncherKind.Epic,
                CheckedAt = now,
                IsInstalled = false,
                StatusDetail = "Epic Games Launcher is not installed",
            };
        }

        var running = _processes.FindByName("EpicGamesLauncher").Count > 0;
        if (!running)
        {
            StopWatching();
            return new LauncherSnapshot
            {
                Launcher = LauncherKind.Epic,
                CheckedAt = now,
                IsInstalled = true,
                IsRunning = false,
                StatusDetail = "Epic Games Launcher is not running",
                ExecutablePath = paths.LauncherExecutable,
                Limitations = StaticLimitations,
                Diagnostics = Describe(paths, []),
            };
        }

        var problems = new List<string>();
        var pending = paths.PendingDirectory is { } pendingDir ? _pendingCache.Refresh(pendingDir, problems) : [];
        if (pending.Count > 0 && (now - _installedRefreshedAt >= InstalledRefreshInterval || _installed.Count == 0))
        {
            _installed = paths.ManifestDirectory is { } manifestDir ? _installedCache.Refresh(manifestDir, problems) : [];
            _installedRefreshedAt = now;
        }

        ReadLauncherLog(paths, now);
        SyncMonitors(pending);

        var operations = pending
            .GroupBy(p => p.AppName, StringComparer.OrdinalIgnoreCase)
            .Select(g => Classify(g.OrderByDescending(m => m.LastWriteTime).First(), now))
            .OrderBy(o => o.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var diagnostics = Describe(paths, pending);
        diagnostics.AddRange(problems.Take(10));

        return new LauncherSnapshot
        {
            Launcher = LauncherKind.Epic,
            CheckedAt = now,
            IsInstalled = true,
            IsRunning = true,
            StatusDetail = pending.Count == 0
                ? "Epic Games Launcher is running; no install or update is pending"
                : $"Epic Games Launcher is running; {pending.Count} pending install or update",
            ExecutablePath = paths.LauncherExecutable,
            Operations = operations,
            Limitations = StaticLimitations,
            Diagnostics = diagnostics,
        };
    }

    private GameOperation Classify(EpicManifest pending, DateTimeOffset now)
    {
        var installed = _installed.FirstOrDefault(m => string.Equals(m.AppName, pending.AppName, StringComparison.OrdinalIgnoreCase));
        var isUpdate = installed is { IsIncompleteInstall: false } && !pending.IsIncompleteInstall;
        var title = pending.DisplayName ?? installed?.DisplayName;

        var evidence = new List<string> { $"Pending manifest: {Path.GetFileName(pending.ManifestPath)} (written {pending.LastWriteTime:yyyy-MM-dd HH:mm})" };
        DateTimeOffset? lastWrite = null;
        var watching = false;
        foreach (var folder in FoldersFor(pending))
        {
            if (!_monitors.TryGetValue(folder, out var monitor))
            {
                evidence.Add($"Folder not present yet: {folder}");
                continue;
            }

            watching |= monitor.IsWatching;
            foreach (var (bucket, at) in monitor.ActivityByBucket())
            {
                lastWrite = Max(lastWrite, at);
                evidence.Add($"Files written in {(bucket == StagingBucket ? "the staging folder" : "the install folder")} at {at.LocalDateTime:HH:mm:ss}");
            }

            if (monitor.LastOverflow is { } overflow)
            {
                lastWrite = Max(lastWrite, overflow);
                evidence.Add($"Heavy file activity in {folder} at {overflow.LocalDateTime:HH:mm:ss}");
            }

            if (monitor.Error is { } error)
            {
                evidence.Add($"Cannot watch {folder}: {error}");
            }
        }

        if (_lastPatcherLogLine is { } logLine)
        {
            evidence.Add($"Epic's launcher log reported patcher activity at {logLine.LocalDateTime:HH:mm:ss}");
        }

        var writing = lastWrite is { } last && now - last <= WriteFreshness;
        var operation = new GameOperation
        {
            Key = $"epic:{pending.AppName}",
            Launcher = LauncherKind.Epic,
            GameId = pending.AppName,
            GameTitle = title,
            TitleNote = title is null ? "Epic's manifest has no display name; showing the app ID" : null,
            State = writing ? (isUpdate ? ActivityState.Updating : ActivityState.Downloading) : ActivityState.Unknown,
            Confidence = writing ? DetectionConfidence.Probable : DetectionConfidence.Unknown,
            EvidenceKind = EvidenceKind.Inferred,
            StateDetail = writing
                ? "Files are being written; Epic does not report the exact step"
                : watching
                    ? "Pending in Epic, but no files are being written (paused, queued or stalled)"
                    : "Pending in Epic; the game folder does not exist yet",
            TotalBytes = pending.InstallSize,
            ProgressNote = "Epic does not expose download progress locally",
            SpeedNote = "Epic does not expose download speed locally",
            Evidence = evidence,
        };

        return title is null ? operation with { GameTitle = pending.AppName } : operation;
    }

    private void ReadLauncherLog(EpicPaths paths, DateTimeOffset now)
    {
        if (paths.LogFile is null)
        {
            return;
        }

        try
        {
            if (_logReader is null || !string.Equals(_logReader.Path, paths.LogFile, StringComparison.OrdinalIgnoreCase))
            {
                _logReader = new IncrementalLogReader(paths.LogFile);
                _logReader.SkipToEnd();
                return;
            }

            var result = _logReader.ReadNewLines();
            if (result.Lines.Any(l => l.Contains("LogBuildPatchServices", StringComparison.Ordinal)))
            {
                _lastPatcherLogLine = now;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug(ex, "Epic launcher log could not be read");
        }
    }

    private void SyncMonitors(IReadOnlyList<EpicManifest> pending)
    {
        var wanted = pending.SelectMany(FoldersFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _monitors.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _monitors[folder].Dispose();
            _monitors.Remove(folder);
        }

        foreach (var folder in wanted.Where(f => !_monitors.ContainsKey(f)))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            _monitors[folder] = new DirectoryActivityMonitor(folder, Clock, relative =>
                relative.StartsWith(".egstore", StringComparison.OrdinalIgnoreCase) ? StagingBucket : InstallBucket);
            Logger.LogDebug("Watching {Folder} for Epic download activity", folder);
        }
    }

    private static IEnumerable<string> FoldersFor(EpicManifest manifest)
    {
        if (manifest.InstallLocation is { } install)
        {
            yield return install;
        }

        if (manifest.StagingLocation is { } staging
            && (manifest.InstallLocation is null || !IsUnder(staging, manifest.InstallLocation)))
        {
            yield return staging;
        }
    }

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private List<string> Describe(EpicPaths paths, IReadOnlyList<EpicManifest> pending)
    {
        var lines = new List<string>
        {
            $"Data folder: {paths.DataDirectory ?? "not found"}",
            $"Launcher: {paths.LauncherExecutable ?? "not found"}",
            $"Pending manifests: {pending.Count}",
            $"Folders watched: {_monitors.Count}",
        };
        lines.AddRange(_monitors.Values.Select(m => $"Watching {m.Path}{(m.IsWatching ? string.Empty : $" (not active: {m.Error})")}"));
        if (_lastPatcherLogLine is { } logLine)
        {
            lines.Add($"Last LogBuildPatchServices line seen at {logLine.LocalDateTime:HH:mm:ss}");
        }

        return lines;
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset b) => a is { } value && value > b ? value : b;

    private void StopWatching()
    {
        foreach (var monitor in _monitors.Values)
        {
            monitor.Dispose();
        }

        _monitors.Clear();
        _logReader = null;
        _lastPatcherLogLine = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopWatching();
        }
    }
}
