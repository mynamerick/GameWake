using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace GameWake.Providers.Xbox;

public enum PackageActivityKind
{
    Staging,
    Installing,
    Updating,
    Servicing,
}

/// <summary>One deployment report from Windows about a package installed for the current user.</summary>
public sealed record PackageActivity(
    string FullName,
    string FamilyName,
    string? DisplayName,
    string? InstalledPath,
    PackageActivityKind Kind,
    double? Progress,
    bool IsComplete,
    string? Error,
    DateTimeOffset At);

public interface IPackageActivitySource : IDisposable
{
    /// <summary>Null when Windows package events are being received.</summary>
    string? Error { get; }

    int EventsReceived { get; }

    TimeSpan? LastSweepDuration { get; }

    /// <summary>The latest event per package received within <paramref name="window"/>.</summary>
    IReadOnlyList<PackageActivity> GetRecent(DateTimeOffset now, TimeSpan window);

    /// <summary>Packages Windows currently reports as being serviced (<c>PackageStatus.DeploymentInProgress</c>).</summary>
    IReadOnlyList<PackageActivity> SweepDeploymentInProgress(DateTimeOffset now);

    bool IsPackageFamilyInstalled(string familyName);
}

/// <summary>
/// Receives <see cref="PackageCatalog"/> events for the current user. Documented: events are
/// raised for all packages being installed for the current user and include progress (0..1).
/// Whether Gaming Services raises them during the download phase of a game is unverified.
/// </summary>
public sealed class PackageCatalogActivitySource : IPackageActivitySource
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, PackageActivity> _activity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;
    private PackageCatalog? _catalog;
    private int _events;
    private long _lastSweepTicks = -1;

    public PackageCatalogActivitySource(Func<DateTimeOffset> now)
    {
        _now = now;
        try
        {
            _catalog = PackageCatalog.OpenForCurrentUser();
            _catalog.PackageStaging += OnStaging;
            _catalog.PackageInstalling += OnInstalling;
            _catalog.PackageUpdating += OnUpdating;
            _catalog.PackageStatusChanged += OnStatusChanged;
        }
        catch (Exception ex)
        {
            Error = $"Windows package events are unavailable: {ex.Message}";
            _catalog = null;
        }
    }

    public string? Error { get; }

    public int EventsReceived => Volatile.Read(ref _events);

    public TimeSpan? LastSweepDuration
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSweepTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    public IReadOnlyList<PackageActivity> GetRecent(DateTimeOffset now, TimeSpan window)
    {
        foreach (var (key, value) in _activity)
        {
            if (now - value.At > Retention)
            {
                _activity.TryRemove(key, out _);
            }
        }

        return _activity.Values.Where(a => now - a.At <= window).ToList();
    }

    public IReadOnlyList<PackageActivity> SweepDeploymentInProgress(DateTimeOffset now)
    {
        var watch = Stopwatch.StartNew();
        var result = new List<PackageActivity>();
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.Status.DeploymentInProgress)
                    {
                        result.Add(Describe(package, PackageActivityKind.Servicing, null, false, null, now));
                    }
                }
                catch (Exception)
                {
                    // A package can disappear or deny one property while being serviced; skip it.
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _lastSweepTicks, watch.Elapsed.Ticks);
        }

        return result;
    }

    public bool IsPackageFamilyInstalled(string familyName)
    {
        try
        {
            return new PackageManager().FindPackagesForUser(string.Empty, familyName).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnStaging(PackageCatalog sender, PackageStagingEventArgs args) =>
        Record(args.Package, PackageActivityKind.Staging, args.Progress, args.IsComplete, args.ErrorCode);

    private void OnInstalling(PackageCatalog sender, PackageInstallingEventArgs args) =>
        Record(args.Package, PackageActivityKind.Installing, args.Progress, args.IsComplete, args.ErrorCode);

    private void OnUpdating(PackageCatalog sender, PackageUpdatingEventArgs args) =>
        Record(args.TargetPackage ?? args.SourcePackage, PackageActivityKind.Updating, args.Progress, args.IsComplete, args.ErrorCode);

    private void OnStatusChanged(PackageCatalog sender, PackageStatusChangedEventArgs args)
    {
        try
        {
            if (args.Package?.Status.DeploymentInProgress == true)
            {
                Record(args.Package, PackageActivityKind.Servicing, null, false, null);
            }
        }
        catch (Exception)
        {
        }
    }

    private void Record(Package? package, PackageActivityKind kind, double? progress, bool isComplete, Exception? error)
    {
        Interlocked.Increment(ref _events);
        if (package is null)
        {
            return;
        }

        try
        {
            var activity = Describe(package, kind, progress, isComplete, error, _now());
            _activity[activity.FullName] = activity;
        }
        catch (Exception)
        {
            // Event handlers must never throw back into Windows.
        }
    }

    private static PackageActivity Describe(Package package, PackageActivityKind kind, double? progress, bool isComplete, Exception? error, DateTimeOffset at)
    {
        var id = package.Id;
        return new PackageActivity(
            id.FullName,
            id.FamilyName,
            Try(() => package.DisplayName),
            Try(() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? package.InstalledPath : package.InstalledLocation.Path),
            kind,
            progress is >= 0 and <= 1 ? progress : null,
            isComplete,
            error is null ? null : $"0x{error.HResult:X8}",
            at);
    }

    private static string? Try(Func<string> read)
    {
        try
        {
            var value = read();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        var catalog = _catalog;
        _catalog = null;
        if (catalog is null)
        {
            return;
        }

        catalog.PackageStaging -= OnStaging;
        catalog.PackageInstalling -= OnInstalling;
        catalog.PackageUpdating -= OnUpdating;
        catalog.PackageStatusChanged -= OnStatusChanged;
    }
}
