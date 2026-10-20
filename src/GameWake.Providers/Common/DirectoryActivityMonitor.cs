using System.Collections.Concurrent;
using GameWake.Core.Infrastructure;

namespace GameWake.Providers.Common;

/// <summary>
/// Records when files under a folder were last written, using change notifications instead of
/// scanning. Events can be grouped into buckets (e.g. one per game folder); returning null from
/// the bucket selector ignores an event. A notification-buffer overflow means heavy activity
/// somewhere under the folder and is recorded separately.
/// </summary>
public sealed class DirectoryActivityMonitor : IDisposable
{
    public const string WholeFolder = "";

    private readonly FileSystemWatcher? _watcher;
    private readonly Func<string, string?> _bucketSelector;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _activity = new(StringComparer.OrdinalIgnoreCase);
    private long _lastOverflowTicks;

    public DirectoryActivityMonitor(string path, IClock clock, Func<string, string?>? bucketSelector = null)
    {
        Path = path;
        _clock = clock;
        _bucketSelector = bucketSelector ?? (_ => WholeFolder);
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Error = ex.Message;
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public string Path { get; }

    public string? Error { get; private set; }

    public bool IsWatching => _watcher is not null && Error is null;

    public DateTimeOffset? LastOverflow
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastOverflowTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime();
        }
    }

    public DateTimeOffset? LastActivity(string bucket = WholeFolder) =>
        _activity.TryGetValue(bucket, out var at) ? at : null;

    public IReadOnlyDictionary<string, DateTimeOffset> ActivityByBucket() => new Dictionary<string, DateTimeOffset>(_activity, StringComparer.OrdinalIgnoreCase);

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        string relative;
        try
        {
            relative = System.IO.Path.GetRelativePath(Path, e.FullPath);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (_bucketSelector(relative) is { } bucket)
        {
            _activity[bucket] = _clock.Now;
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (e.GetException() is InternalBufferOverflowException)
        {
            Interlocked.Exchange(ref _lastOverflowTicks, _clock.Now.UtcTicks);
            return;
        }

        Error = e.GetException().Message;
    }

    public void Dispose()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Deleted -= OnChanged;
        _watcher.Renamed -= OnChanged;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }
}
