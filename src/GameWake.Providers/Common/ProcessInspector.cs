using System.ComponentModel;
using System.Diagnostics;

namespace GameWake.Providers.Common;

public sealed record ProcessInfo(int Id, string Name, DateTime? StartTime);

/// <summary>Finds launcher processes. Process presence is supporting evidence only, never proof of a download.</summary>
public interface IProcessInspector
{
    IReadOnlyList<ProcessInfo> FindByName(string processName);

    ProcessInfo? FindById(int processId);
}

/// <summary>
/// Shares one process-table snapshot (refreshed at most once a second) across all providers,
/// so several providers polling together do not each enumerate every process.
/// </summary>
public sealed class ProcessInspector : IProcessInspector
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private DateTime _takenAt = DateTime.MinValue;
    private Dictionary<string, List<int>> _idsByName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ProcessInfo> FindByName(string processName)
    {
        List<int>? ids;
        lock (_gate)
        {
            RefreshIfStale();
            _idsByName.TryGetValue(processName, out ids);
            ids = ids is null ? null : [.. ids];
        }

        if (ids is null)
        {
            return [];
        }

        var result = new List<ProcessInfo>(ids.Count);
        foreach (var id in ids)
        {
            if (FindById(id) is { } info && string.Equals(info.Name, processName, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(info);
            }
        }

        return result;
    }

    public ProcessInfo? FindById(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            DateTime? start = null;
            try
            {
                start = process.StartTime;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
            }

            return new ProcessInfo(process.Id, process.ProcessName, start);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private void RefreshIfStale()
    {
        var now = DateTime.UtcNow;
        if (now - _takenAt < CacheLifetime)
        {
            return;
        }

        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!map.TryGetValue(process.ProcessName, out var list))
                {
                    list = [];
                    map[process.ProcessName] = list;
                }

                list.Add(process.Id);
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        _idsByName = map;
        _takenAt = now;
    }
}
