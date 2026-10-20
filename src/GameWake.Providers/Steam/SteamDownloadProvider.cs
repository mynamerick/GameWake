using System.Globalization;
using GameWake.Core.Infrastructure;
using GameWake.Core.Models;
using GameWake.Core.Providers;
using GameWake.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GameWake.Providers.Steam;

/// <summary>
/// Detects Steam downloads from Steam's own records: <c>logs\content_log.txt</c> (primary),
/// <c>HKCU\Software\Valve\Steam\Apps\&lt;id&gt;\Updating</c> (corroboration) and app manifests
/// (names and the progress Steam saved when a download was paused). Read-only throughout.
/// </summary>
public sealed class SteamDownloadProvider : LauncherProviderBase
{
    private static readonly TimeSpan RegistrySweepInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ManifestRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LibraryCheckInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InterestWindow = TimeSpan.FromMinutes(30);

    private static readonly string[] StaticLimitations =
    [
        "Live progress is unavailable: Steam saves download progress to disk only when a download changes state.",
        "Speed is Steam's own measurement, logged about once a minute; with several downloads Steam reports one combined rate.",
    ];

    private readonly IRegistryReader _registry;
    private readonly IProcessInspector _processes;
    private readonly Func<string?> _locateSteam;
    private readonly SteamManifestCache _manifestCache = new();
    private readonly Dictionary<uint, SteamRegistryApp> _registryApps = [];

    private string? _steamRoot;
    private SteamSession? _session;
    private IncrementalLogReader? _logReader;
    private IReadOnlyList<string> _libraries = [];
    private string? _libraryProblem;
    private DateTime? _libraryFileWrite;
    private DateTimeOffset _librariesCheckedAt = DateTimeOffset.MinValue;
    private IReadOnlyDictionary<uint, SteamAppManifest> _manifests = new Dictionary<uint, SteamAppManifest>();
    private List<string> _manifestProblems = [];
    private DateTimeOffset _manifestsRefreshedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _registrySweptAt = DateTimeOffset.MinValue;
    private bool _sessionStartUnknown;

    public SteamDownloadProvider(
        IClock clock,
        ILogger<SteamDownloadProvider> logger,
        IRegistryReader registry,
        IProcessInspector processes,
        Func<string?>? locateSteam = null)
        : base(clock, logger)
    {
        _registry = registry;
        _processes = processes;
        _locateSteam = locateSteam ?? (() => SteamLocator.Locate(registry));
    }

    public override LauncherKind Launcher => LauncherKind.Steam;

    protected override ValueTask<LauncherSnapshot> PollCoreAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Poll(now));
    }

    private LauncherSnapshot Poll(DateTimeOffset now)
    {
        var root = _locateSteam();
        if (root is null)
        {
            ResetSession();
            _steamRoot = null;
            return new LauncherSnapshot
            {
                Launcher = LauncherKind.Steam,
                CheckedAt = now,
                IsInstalled = false,
                StatusDetail = "Steam is not installed for this user",
                Diagnostics = [@"No Steam folder found via HKCU\Software\Valve\Steam\SteamPath or HKLM InstallPath"],
            };
        }

        if (!string.Equals(root, _steamRoot, StringComparison.OrdinalIgnoreCase))
        {
            ResetSession();
            _steamRoot = root;
            _librariesCheckedAt = DateTimeOffset.MinValue;
            _libraryFileWrite = null;
        }

        var executable = Path.Combine(root, "steam.exe");
        var process = FindSteamProcess();
        if (process is null)
        {
            ResetSession();
            return new LauncherSnapshot
            {
                Launcher = LauncherKind.Steam,
                CheckedAt = now,
                IsInstalled = true,
                IsRunning = false,
                StatusDetail = "Steam is not running",
                ExecutablePath = File.Exists(executable) ? executable : null,
                Limitations = StaticLimitations,
                Diagnostics = [$"Steam folder: {root}", "Steam process not found; downloads cannot run while Steam is closed"],
            };
        }

        var diagnostics = new List<string> { $"Steam folder: {root}" };
        var limitations = new List<string>(StaticLimitations);
        string? error = null;

        EnsureSession(root, process, now, diagnostics);
        var session = _session!;

        var newEvents = ReadLog(session, diagnostics, limitations, ref error);

        RefreshLibraries(root, now);
        if (newEvents > 0 || now - _manifestsRefreshedAt >= ManifestRefreshInterval)
        {
            var problems = new List<string>();
            _manifests = _manifestCache.Refresh(_libraries, problems);
            _manifestProblems = problems;
            _manifestsRefreshedAt = now;
        }

        RefreshRegistry(session, now);

        var classification = SteamActivityClassifier.Classify(session, _manifests, _registryApps, now.LocalDateTime);

        if (_sessionStartUnknown)
        {
            limitations.Add("Steam's start time could not be read, so only log lines written after GameWake started are used.");
        }

        AddDiagnostics(diagnostics, session);

        return new LauncherSnapshot
        {
            Launcher = LauncherKind.Steam,
            CheckedAt = now,
            IsInstalled = true,
            IsRunning = true,
            StatusDetail = $"Steam is running (session started {session.Start:HH:mm})",
            ExecutablePath = File.Exists(executable) ? executable : null,
            Operations = classification.Operations,
            TotalBytesPerSecond = classification.TotalBytesPerSecond,
            TotalSpeedNote = classification.TotalSpeedNote,
            Error = error,
            Limitations = limitations,
            Diagnostics = diagnostics,
        };
    }

    private ProcessInfo? FindSteamProcess()
    {
        var pid = _registry.GetInt32(RegistryHive.CurrentUser, SteamLocator.ActiveProcessKey, "pid");
        if (pid is > 0 && _processes.FindById(pid.Value) is { } byId
            && string.Equals(byId.Name, "steam", StringComparison.OrdinalIgnoreCase))
        {
            return byId;
        }

        return _processes.FindByName("steam").OrderBy(p => p.StartTime ?? DateTime.MaxValue).FirstOrDefault();
    }

    private void EnsureSession(string root, ProcessInfo process, DateTimeOffset now, List<string> diagnostics)
    {
        if (_session is not null && _session.ProcessId == process.Id)
        {
            return;
        }

        _sessionStartUnknown = process.StartTime is null;
        var start = process.StartTime ?? now.LocalDateTime;
        _session = new SteamSession(process.Id, start);
        _registryApps.Clear();
        _registrySweptAt = DateTimeOffset.MinValue;

        var logs = Path.Combine(root, "logs");
        var current = Path.Combine(logs, "content_log.txt");
        var previous = Path.Combine(logs, "content_log.previous.txt");
        _logReader = new IncrementalLogReader(current, previous);

        // If Steam rotated the log during this session, the session's first lines are in the previous file.
        try
        {
            var info = new FileInfo(previous);
            if (info.Exists && info.LastWriteTime >= start)
            {
                var rotated = new IncrementalLogReader(previous).ReadNewLines();
                foreach (var line in rotated.Lines)
                {
                    if (SteamContentLogParser.ParseLine(line) is { } logEvent)
                    {
                        _session.Apply(logEvent);
                    }
                }

                diagnostics.Add($"Read {rotated.Lines.Count} lines from content_log.previous.txt (rotated during this session)");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"content_log.previous.txt could not be read: {ex.Message}");
        }

        Logger.LogInformation("Steam session detected (pid {Pid}, started {Start:yyyy-MM-dd HH:mm:ss})", process.Id, start);
    }

    private int ReadLog(SteamSession session, List<string> diagnostics, List<string> limitations, ref string? error)
    {
        var reader = _logReader!;
        LogReadResult result;
        try
        {
            result = reader.ReadNewLines();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Steam's download log could not be read: {ex.Message}";
            limitations.Add("Detection is limited to Steam's registry flags until the download log can be read again.");
            return 0;
        }

        if (result.FileMissing)
        {
            limitations.Add("Steam's download log (logs\\content_log.txt) was not found; detection relies on Steam's registry flags.");
            return 0;
        }

        if (result.Restarted)
        {
            diagnostics.Add("The download log was rotated or truncated since the previous check");
        }

        var applied = 0;
        foreach (var line in result.Lines)
        {
            if (SteamContentLogParser.ParseLine(line) is { } logEvent)
            {
                session.Apply(logEvent);
                applied++;
            }
        }

        return applied;
    }

    private void RefreshLibraries(string root, DateTimeOffset now)
    {
        if (now - _librariesCheckedAt < LibraryCheckInterval && _libraries.Count > 0)
        {
            return;
        }

        _librariesCheckedAt = now;
        DateTime? write = null;
        try
        {
            var file = new FileInfo(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
            write = file.Exists ? file.LastWriteTimeUtc : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (_libraries.Count > 0 && write == _libraryFileWrite)
        {
            return;
        }

        _libraryFileWrite = write;
        _libraries = SteamLibraryReader.Read(root, out _libraryProblem);
        _manifestsRefreshedAt = DateTimeOffset.MinValue;
    }

    private void RefreshRegistry(SteamSession session, DateTimeOffset now)
    {
        IEnumerable<uint> ids;
        if (now - _registrySweptAt >= RegistrySweepInterval)
        {
            _registrySweptAt = now;
            ids = _registry.GetSubKeyNames(RegistryHive.CurrentUser, SteamLocator.AppsKey)
                .Select(name => uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id != 0)
                .ToList();
            _registryApps.Clear();
        }
        else
        {
            var cutoff = now.LocalDateTime - InterestWindow;
            ids = session.Apps.Values
                .Where(a => (a.FlagsAt ?? DateTime.MinValue) >= cutoff
                    || a.Channels.Values.Any(c => c.ChangedAt >= cutoff)
                    || a.HasFlag("Update Running"))
                .Select(a => a.AppId)
                .Concat(_registryApps.Values.Where(r => r.Updating == true).Select(r => r.AppId))
                .Distinct()
                .ToList();
        }

        foreach (var id in ids)
        {
            var key = $@"{SteamLocator.AppsKey}\{id.ToString(CultureInfo.InvariantCulture)}";
            var updating = _registry.GetInt32(RegistryHive.CurrentUser, key, "Updating");
            if (updating is null && !_registryApps.ContainsKey(id))
            {
                continue;
            }

            var name = _registryApps.TryGetValue(id, out var known) && known.Name is not null
                ? known.Name
                : updating == 1 || session.Apps.ContainsKey(id) ? _registry.GetString(RegistryHive.CurrentUser, key, "Name") : null;
            _registryApps[id] = new SteamRegistryApp(id, updating is null ? null : updating == 1, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        }
    }

    private void AddDiagnostics(List<string> diagnostics, SteamSession session)
    {
        diagnostics.Add($"Steam process {session.ProcessId}, session started {session.Start:yyyy-MM-dd HH:mm:ss}");
        if (_logReader is { } reader)
        {
            diagnostics.Add($"Download log: {reader.Path} (read to byte {reader.Position:N0})");
        }

        diagnostics.Add($"Log events used this session: {session.AppliedEvents:N0}; earlier-session lines ignored: {session.IgnoredEvents:N0}");
        if (session.LastEventAt is { } lastEvent)
        {
            diagnostics.Add($"Latest log event: {lastEvent:HH:mm:ss}");
        }

        if (session.LastRate is { } rate)
        {
            diagnostics.Add($"Latest download rate line: {rate.MegabitsPerSecond.ToString("0.###", CultureInfo.InvariantCulture)} Mbps at {rate.Timestamp:HH:mm:ss}");
        }

        diagnostics.Add($"Libraries: {string.Join("; ", _libraries)}");
        if (_libraryProblem is not null)
        {
            diagnostics.Add(_libraryProblem);
        }

        diagnostics.Add($"App manifests parsed: {_manifests.Count} (cached files: {_manifestCache.CachedFileCount})");
        diagnostics.AddRange(_manifestProblems.Take(10));

        var updating = _registryApps.Values.Where(r => r.Updating == true).Select(r => r.AppId.ToString(CultureInfo.InvariantCulture)).ToList();
        diagnostics.Add(updating.Count == 0
            ? "Registry: no app has Updating = 1"
            : $"Registry: Updating = 1 for app {string.Join(", ", updating)}");
    }

    private void ResetSession()
    {
        _session = null;
        _logReader = null;
        _registryApps.Clear();
        _registrySweptAt = DateTimeOffset.MinValue;
    }
}
