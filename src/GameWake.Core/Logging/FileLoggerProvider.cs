using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace GameWake.Core.Logging;

/// <summary>
/// Minimal file logger: one file per day under the logs folder, written by a single background
/// task so callers never block on disk I/O. Files are shared for reading so they can be viewed
/// while GameWake runs.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 10 * 1024 * 1024;

    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly Task _writer;
    private int _disposed;

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information, int retentionDays = 14)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        DeleteOldFiles(retentionDays);
        _writer = Task.Run(WriteLoopAsync);
    }

    public RecentLogBuffer Recent { get; } = new(1000);

    public string LogDirectory => _directory;

    public string? CurrentFile { get; private set; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimumLevel;

    internal void Write(string line)
    {
        Recent.Add(line);
        _channel.Writer.TryWrite(line);
    }

    /// <summary>Stops accepting log lines and waits for everything queued to reach the disk.</summary>
    public void Flush(TimeSpan timeout)
    {
        _channel.Writer.TryComplete();
        try
        {
            _writer.Wait(timeout);
        }
        catch (AggregateException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Flush(TimeSpan.FromSeconds(3));
        }
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        string? openPath = null;
        try
        {
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var path = ResolvePath();
                if (path != openPath)
                {
                    writer?.Dispose();
                    writer = Open(path);
                    openPath = writer is null ? null : path;
                    CurrentFile = openPath;
                }

                while (reader.TryRead(out var line))
                {
                    writer?.WriteLine(line);
                }

                writer?.Flush();
            }
        }
        catch (Exception)
        {
            // Logging must never take the application down.
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private string ResolvePath()
    {
        var stem = Path.Combine(_directory, $"gamewake-{DateTime.Now:yyyyMMdd}");
        var path = stem + ".log";
        for (var index = 1; index < 100; index++)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxFileBytes)
            {
                return path;
            }

            path = $"{stem}-{index}.log";
        }

        return path;
    }

    private static StreamWriter? Open(string path)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void DeleteOldFiles(int retentionDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("gamewake-*.log"))
            {
                if (file.LastWriteTime < cutoff)
                {
                    file.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _category = ShortenCategory(category);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder(160)
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [").Append(LevelCode(logLevel)).Append("] ")
                .Append(_category).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            provider.Write(builder.ToString());
        }

        private static string ShortenCategory(string category)
        {
            var dot = category.LastIndexOf('.');
            return dot >= 0 ? category[(dot + 1)..] : category;
        }

        private static string LevelCode(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}

public sealed class RecentLogBuffer(int capacity)
{
    private readonly Queue<string> _lines = new(capacity);
    private readonly object _gate = new();

    public void Add(string line)
    {
        lock (_gate)
        {
            if (_lines.Count == capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(line);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }
}
