using GameWake.Core.Infrastructure;
using GameWake.Core.Models;
using GameWake.Core.Power;
using GameWake.Core.Providers;
using GameWake.Providers.Common;
using Microsoft.Win32;

namespace GameWake.Tests;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? now = null) => Now = now ?? new DateTimeOffset(2026, 10, 20, 16, 30, 0, TimeSpan.FromHours(10.5));

    public DateTimeOffset Now { get; set; }

    public void Advance(TimeSpan duration) => Now += duration;
}

public sealed class FakeRegistry : IRegistryReader
{
    private readonly Dictionary<string, Dictionary<string, object?>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _subKeys = new(StringComparer.OrdinalIgnoreCase);

    public void Set(RegistryHive hive, string subKey, string name, object? value)
    {
        var key = PathKey(hive, subKey);
        if (!_values.TryGetValue(key, out var map))
        {
            map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _values[key] = map;
        }

        map[name] = value;
    }

    public void AddSubKey(RegistryHive hive, string subKey, string child)
    {
        var key = PathKey(hive, subKey);
        if (!_subKeys.TryGetValue(key, out var list))
        {
            list = [];
            _subKeys[key] = list;
        }

        if (!list.Contains(child, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(child);
        }
    }

    public object? GetValue(RegistryHive hive, string subKey, string valueName) =>
        _values.TryGetValue(PathKey(hive, subKey), out var map) && map.TryGetValue(valueName, out var value) ? value : null;

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey) =>
        _subKeys.TryGetValue(PathKey(hive, subKey), out var list) ? list : [];

    private static string PathKey(RegistryHive hive, string subKey) => $"{hive}:{subKey}";
}

public sealed class FakeProcessInspector : IProcessInspector
{
    public List<ProcessInfo> Processes { get; } = [];

    public IReadOnlyList<ProcessInfo> FindByName(string processName) =>
        Processes.Where(p => string.Equals(p.Name, processName, StringComparison.OrdinalIgnoreCase)).ToList();

    public ProcessInfo? FindById(int processId) => Processes.FirstOrDefault(p => p.Id == processId);
}

public sealed class FakePower : IPowerRequestController
{
    public bool IsHeld { get; private set; }

    public DateTimeOffset? HeldSince { get; private set; }

    public List<(bool Hold, string Reason)> Calls { get; } = [];

    public event EventHandler<PowerRequestChangedEventArgs>? Changed;

    public void SetRequested(bool hold, string reason)
    {
        Calls.Add((hold, reason));
        IsHeld = hold;
        HeldSince = hold ? DateTimeOffset.Now : null;
        Changed?.Invoke(this, new PowerRequestChangedEventArgs(hold, reason, DateTimeOffset.Now));
    }

    public Task FlushAsync() => Task.CompletedTask;

    public void Dispose()
    {
        IsHeld = false;
    }
}

public sealed class ScriptedProvider : ILauncherDownloadProvider
{
    public ScriptedProvider(LauncherKind launcher) => Launcher = launcher;

    public LauncherKind Launcher { get; }

    public Func<LauncherSnapshot>? Next { get; set; }

    public Exception? Throw { get; set; }

    public TimeSpan Delay { get; set; }

    public int PollCount { get; private set; }

    public bool Disposed { get; private set; }

    public async ValueTask<LauncherSnapshot> PollAsync(CancellationToken cancellationToken)
    {
        PollCount++;
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Throw is not null)
        {
            throw Throw;
        }

        return Next?.Invoke() ?? new LauncherSnapshot { Launcher = Launcher, CheckedAt = DateTimeOffset.Now, IsInstalled = true, IsRunning = true };
    }

    public void Dispose() => Disposed = true;
}

public sealed class FakeFactory : ILauncherProviderFactory
{
    public Dictionary<LauncherKind, ILauncherDownloadProvider> Map { get; } = [];

    public ILauncherDownloadProvider Create(LauncherKind kind) =>
        Map.TryGetValue(kind, out var provider) ? provider : throw new InvalidOperationException($"No provider for {kind}");
}

public static class Ops
{
    public static GameOperation Steam(string id, ActivityState state, DetectionConfidence confidence, string? title = null) => new()
    {
        Key = $"steam:{id}",
        Launcher = LauncherKind.Steam,
        GameId = id,
        GameTitle = title,
        State = state,
        Confidence = confidence,
        EvidenceKind = EvidenceKind.Direct,
    };
}
