using GameWake.Core.Infrastructure;
using GameWake.Core.Models;
using GameWake.Core.Providers;
using GameWake.Providers.Common;
using Microsoft.Extensions.Logging;

namespace GameWake.Providers.Xbox;

/// <summary>
/// Pure classification of Windows package reports and Xbox install-folder activity.
/// See docs/detection-feasibility.md for the rules.
/// </summary>
public static class XboxActivityClassifier
{
    public static readonly TimeSpan EventFreshness = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EventRetention = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan FolderWriteFreshness = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan CompletedDisplayTime = TimeSpan.FromMinutes(2);

    public static IReadOnlyList<GameOperation> Classify(
        IReadOnlyList<PackageActivity> events,
        IReadOnlyList<PackageActivity> servicing,
        IReadOnlyList<string> installRoots,
        IReadOnlyDictionary<string, DateTimeOffset> contentWrites,
        DateTimeOffset now)
    {
        var operations = new Dictionary<string, GameOperation>(StringComparer.OrdinalIgnoreCase);
        var foldersCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var latest = events.Concat(servicing.Where(s => !events.Any(e => e.FullName.Equals(s.FullName, StringComparison.OrdinalIgnoreCase))))
            .GroupBy(a => a.FamilyName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(a => a.At)!);

        foreach (var activity in latest)
        {
            if (activity.Error is not null)
            {
                continue;
            }

            var gameFolder = GameFolderOf(activity.InstalledPath, installRoots);
            if (gameFolder is not null)
            {
                foldersCovered.Add(gameFolder);
            }

            var age = now - activity.At;
            var evidence = new List<string>
            {
                $"Windows reported {activity.Kind.ToString().ToLowerInvariant()}{(activity.Progress is { } p ? $" at {p:P0}" : string.Empty)}{(activity.IsComplete ? " (complete)" : string.Empty)} at {activity.At.LocalDateTime:HH:mm:ss}",
                $"Package: {activity.FamilyName}",
            };

            DateTimeOffset? folderWrite = gameFolder is not null && contentWrites.TryGetValue(gameFolder, out var written) ? written : null;
            if (folderWrite is { } fw)
            {
                evidence.Add($"Files written in {gameFolder}\\Content at {fw.LocalDateTime:HH:mm:ss}");
            }

            var title = activity.DisplayName ?? gameFolder;
            var operation = new GameOperation
            {
                Key = $"xbox:{activity.FamilyName}",
                Launcher = LauncherKind.Xbox,
                GameId = activity.FamilyName,
                GameTitle = title,
                TitleNote = activity.DisplayName is null
                    ? gameFolder is null ? "Windows did not report a display name for this package" : "Name taken from the install folder"
                    : null,
                State = ActivityState.Unknown,
                Confidence = DetectionConfidence.Unknown,
                EvidenceKind = EvidenceKind.Direct,
                Progress = activity.IsComplete ? null : activity.Progress,
                ProgressNote = activity.Progress is null ? "Windows did not report progress for this package" : "Reported by Windows",
                SpeedNote = "Download speed is not available for Xbox and Microsoft Store packages",
                Evidence = evidence,
            };

            if (activity.IsComplete)
            {
                if (activity.Kind != PackageActivityKind.Servicing && age <= CompletedDisplayTime)
                {
                    operations[operation.Key] = operation with
                    {
                        State = ActivityState.Completed,
                        Confidence = DetectionConfidence.Inactive,
                        StateDetail = $"Finished at {activity.At.LocalDateTime:HH:mm:ss}",
                    };
                }

                continue;
            }

            var state = activity.Kind switch
            {
                PackageActivityKind.Staging => ActivityState.Downloading,
                PackageActivityKind.Installing => ActivityState.Installing,
                _ => ActivityState.Updating,
            };
            var isGame = gameFolder is not null;
            var fresh = age <= EventFreshness;
            var writing = folderWrite is { } recent && now - recent <= FolderWriteFreshness;

            if (activity.Kind == PackageActivityKind.Servicing || !fresh)
            {
                if (!fresh && !writing)
                {
                    if (age <= EventRetention && activity.Kind != PackageActivityKind.Servicing)
                    {
                        operations[operation.Key] = operation with
                        {
                            Progress = null,
                            StateDetail = $"Windows has not reported progress for {(int)age.TotalMinutes} minutes",
                        };
                    }

                    continue;
                }

                operations[operation.Key] = operation with
                {
                    State = state,
                    Confidence = DetectionConfidence.Probable,
                    EvidenceKind = EvidenceKind.Inferred,
                    StateDetail = isGame ? "Windows reports the game package is being serviced" : "Microsoft Store package being serviced",
                };
                continue;
            }

            operations[operation.Key] = operation with
            {
                State = state,
                Confidence = isGame ? DetectionConfidence.Confirmed : DetectionConfidence.Probable,
                StateDetail = isGame
                    ? activity.Kind == PackageActivityKind.Staging ? "Windows is staging the game package (download and unpack)" : null
                    : "Microsoft Store package (not identified as a game)",
            };
        }

        foreach (var (folder, at) in contentWrites)
        {
            if (foldersCovered.Contains(folder) || now - at > FolderWriteFreshness)
            {
                continue;
            }

            var name = Path.GetFileName(folder);
            var key = $"xbox-folder:{folder.ToLowerInvariant()}";
            operations[key] = new GameOperation
            {
                Key = key,
                Launcher = LauncherKind.Xbox,
                GameTitle = name,
                TitleNote = "Name taken from the install folder; Windows did not report this package",
                State = ActivityState.Downloading,
                Confidence = DetectionConfidence.Probable,
                EvidenceKind = EvidenceKind.Inferred,
                StateDetail = "Files are being written in the game's Content folder",
                ProgressNote = "Windows did not report progress for this folder",
                SpeedNote = "Download speed is not available for Xbox and Microsoft Store packages",
                Evidence = [$"Files written in {folder}\\Content at {at.LocalDateTime:HH:mm:ss} while Gaming Services was running"],
            };
        }

        return operations.Values
            .OrderByDescending(o => o.State.SummaryPriority())
            .ThenBy(o => o.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Returns <c>X:\XboxGames\Game</c> for a package installed at <c>X:\XboxGames\Game\Content</c>.</summary>
    public static string? GameFolderOf(string? installedPath, IReadOnlyList<string> installRoots)
    {
        if (string.IsNullOrWhiteSpace(installedPath))
        {
            return null;
        }

        var path = Path.TrimEndingDirectorySeparator(installedPath);
        foreach (var root in installRoots)
        {
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = path[prefix.Length..];
            var separator = rest.IndexOf(Path.DirectorySeparatorChar);
            var game = separator < 0 ? rest : rest[..separator];
            return game.Length == 0 ? null : Path.Combine(Path.TrimEndingDirectorySeparator(root), game);
        }

        var marker = $"{Path.DirectorySeparatorChar}XboxGames{Path.DirectorySeparatorChar}";
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var afterRoot = path[(index + marker.Length)..];
        var end = afterRoot.IndexOf(Path.DirectorySeparatorChar);
        var gameName = end < 0 ? afterRoot : afterRoot[..end];
        return gameName.Length == 0 ? null : path[..(index + marker.Length)] + gameName;
    }
}

/// <summary>
/// Xbox app / Gaming Services / PC Game Pass detection from Windows package deployment reports
/// and write activity in Xbox install folders. Protected WindowsApps folders are never opened.
/// </summary>
public sealed class XboxDownloadProvider : LauncherProviderBase
{
    private const string GamingServicesFamily = "Microsoft.GamingServices_8wekyb3d8bbwe";
    private const string XboxAppFamily = "Microsoft.GamingApp_8wekyb3d8bbwe";

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RootsRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallCheckInterval = TimeSpan.FromMinutes(5);

    private static readonly string[] StaticLimitations =
    [
        "Download speed is not available for Xbox and Microsoft Store packages.",
        "Whether Windows reports Xbox game downloads while they are still downloading (rather than only when installing) has not been verified.",
        "Folder activity shows only that files are being written; such activity is at most Probable.",
    ];

    private readonly IProcessInspector _processes;
    private readonly Func<IPackageActivitySource> _sourceFactory;
    private readonly Func<IReadOnlyList<string>> _findRoots;
    private readonly Dictionary<string, DirectoryActivityMonitor> _monitors = new(StringComparer.OrdinalIgnoreCase);

    private IPackageActivitySource? _source;
    private IReadOnlyList<string> _roots = [];
    private DateTimeOffset _rootsCheckedAt = DateTimeOffset.MinValue;
    private IReadOnlyList<PackageActivity> _servicing = [];
    private DateTimeOffset _sweptAt = DateTimeOffset.MinValue;
    private string? _sweepError;
    private bool? _installed;
    private DateTimeOffset _installCheckedAt = DateTimeOffset.MinValue;

    public XboxDownloadProvider(
        IClock clock,
        ILogger<XboxDownloadProvider> logger,
        IProcessInspector processes,
        Func<IPackageActivitySource>? sourceFactory = null,
        Func<IReadOnlyList<string>>? findRoots = null)
        : base(clock, logger)
    {
        _processes = processes;
        _sourceFactory = sourceFactory ?? (() => new PackageCatalogActivitySource(() => clock.Now));
        _findRoots = findRoots ?? (() => GamingRoot.FindInstallFolders());
    }

    public override LauncherKind Launcher => LauncherKind.Xbox;

    protected override ValueTask<LauncherSnapshot> PollCoreAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Poll(now));
    }

    private LauncherSnapshot Poll(DateTimeOffset now)
    {
        _source ??= _sourceFactory();

        if (_installed is null || now - _installCheckedAt >= InstallCheckInterval)
        {
            _installed = _source.IsPackageFamilyInstalled(GamingServicesFamily) || _source.IsPackageFamilyInstalled(XboxAppFamily);
            _installCheckedAt = now;
        }

        var running = _processes.FindByName("gamingservices").Count > 0;
        if (_installed != true && !running)
        {
            StopWatching();
            return new LauncherSnapshot
            {
                Launcher = LauncherKind.Xbox,
                CheckedAt = now,
                IsInstalled = false,
                StatusDetail = "The Xbox app and Gaming Services are not installed",
            };
        }

        if (!running)
        {
            StopWatching();
            return new LauncherSnapshot
            {
                Launcher = LauncherKind.Xbox,
                CheckedAt = now,
                IsInstalled = true,
                IsRunning = false,
                StatusDetail = "Gaming Services is not running",
                Limitations = StaticLimitations,
            };
        }

        if (now - _rootsCheckedAt >= RootsRefreshInterval)
        {
            _roots = _findRoots();
            _rootsCheckedAt = now;
            SyncMonitors();
        }

        if (now - _sweptAt >= SweepInterval)
        {
            _sweptAt = now;
            try
            {
                _servicing = _source.SweepDeploymentInProgress(now);
                _sweepError = null;
            }
            catch (Exception ex)
            {
                _servicing = [];
                _sweepError = ex.Message;
                Logger.LogWarning(ex, "Package status sweep failed");
            }
        }

        var events = _source.GetRecent(now, XboxActivityClassifier.EventRetention);
        var operations = XboxActivityClassifier.Classify(events, _servicing, _roots, ContentWrites(), now);

        var limitations = new List<string>(StaticLimitations);
        if (_source.Error is { } sourceError)
        {
            limitations.Add($"{sourceError}. Only folder activity can be detected.");
        }

        var diagnostics = new List<string>
        {
            $"Gaming Services running; Xbox app or Gaming Services package installed: {_installed}",
            $"Windows package events received since start: {_source.EventsReceived}",
            $"Packages being serviced at last sweep ({_sweptAt.LocalDateTime:HH:mm:ss}): {_servicing.Count}"
                + (_source.LastSweepDuration is { } duration ? $" (sweep took {duration.TotalMilliseconds:0} ms)" : string.Empty),
            _roots.Count == 0 ? "No Xbox install folders found" : $"Xbox install folders: {string.Join("; ", _roots)}",
        };
        if (_sweepError is not null)
        {
            diagnostics.Add($"Last package sweep failed: {_sweepError}");
        }

        diagnostics.AddRange(_monitors.Values.Where(m => !m.IsWatching).Select(m => $"Cannot watch {m.Path}: {m.Error}"));

        return new LauncherSnapshot
        {
            Launcher = LauncherKind.Xbox,
            CheckedAt = now,
            IsInstalled = true,
            IsRunning = true,
            StatusDetail = "Gaming Services is running",
            Operations = operations,
            Limitations = limitations,
            Diagnostics = diagnostics,
        };
    }

    private Dictionary<string, DateTimeOffset> ContentWrites()
    {
        var writes = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, monitor) in _monitors)
        {
            foreach (var (game, at) in monitor.ActivityByBucket())
            {
                writes[Path.Combine(root, game)] = at;
            }
        }

        return writes;
    }

    private void SyncMonitors()
    {
        foreach (var root in _monitors.Keys.Where(k => !_roots.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _monitors[root].Dispose();
            _monitors.Remove(root);
        }

        foreach (var root in _roots.Where(r => !_monitors.ContainsKey(r)))
        {
            _monitors[root] = new DirectoryActivityMonitor(root, Clock, ContentBucket);
        }
    }

    /// <summary>Only writes inside <c>&lt;game&gt;\Content</c> count; save data and other folders are ignored.</summary>
    internal static string? ContentBucket(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, 3);
        return parts.Length >= 2 && parts[1].Equals("Content", StringComparison.OrdinalIgnoreCase) ? parts[0] : null;
    }

    private void StopWatching()
    {
        foreach (var monitor in _monitors.Values)
        {
            monitor.Dispose();
        }

        _monitors.Clear();
        _rootsCheckedAt = DateTimeOffset.MinValue;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopWatching();
            _source?.Dispose();
            _source = null;
        }
    }
}
