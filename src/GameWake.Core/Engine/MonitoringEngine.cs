using GameWake.Core.History;
using GameWake.Core.Infrastructure;
using GameWake.Core.Models;
using GameWake.Core.Policy;
using GameWake.Core.Power;
using GameWake.Core.Providers;
using GameWake.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GameWake.Core.Engine;

/// <summary>
/// Polls the enabled providers on one background loop, decides whether sleep must be prevented,
/// and publishes a combined snapshot. All decisions are made on that single loop, so detection
/// updates and power-state changes can never race each other.
/// </summary>
public sealed class MonitoringEngine : IAsyncDisposable
{
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan NotificationCooldown = TimeSpan.FromSeconds(60);

    private readonly ILauncherProviderFactory _factory;
    private readonly IPowerRequestController _power;
    private readonly ActivityHistoryTracker _history;
    private readonly IClock _clock;
    private readonly ILogger<MonitoringEngine> _logger;
    private readonly RuntimeOptions _runtime;
    private readonly SleepDecisionMachine _decision = new();
    private readonly Dictionary<LauncherKind, ProviderSlot> _slots = [];
    private readonly Dictionary<LauncherKind, string?> _reportedErrors = [];
    private readonly Dictionary<string, DateTimeOffset> _lastNotified = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _lifecycle = new();

    private AppSettings _settings = new();
    private volatile bool _paused;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private MonitorSnapshot _current;
    private IReadOnlyList<LauncherKind> _lastResponsible = [];
    private IReadOnlyList<LauncherSnapshot> _lastLaunchers = [];
    private bool _holding;
    private DateTimeOffset? _holdingSince;
    private bool _firstPollCompleted;

    public MonitoringEngine(
        ILauncherProviderFactory factory,
        IPowerRequestController power,
        ActivityHistoryTracker history,
        RuntimeOptions runtime,
        IClock clock,
        ILogger<MonitoringEngine> logger)
    {
        _factory = factory;
        _power = power;
        _history = history;
        _runtime = runtime;
        _clock = clock;
        _logger = logger;
        _current = MonitorSnapshot.Initial(clock.Now);
    }

    public event EventHandler<MonitorSnapshot>? SnapshotUpdated;

    public event EventHandler<MonitorNotification>? NotificationRaised;

    public MonitorSnapshot Current => Volatile.Read(ref _current);

    public bool IsPaused => _paused;

    public bool IsStarted
    {
        get
        {
            lock (_lifecycle)
            {
                return _loop is { IsCompleted: false };
            }
        }
    }

    public void Start(AppSettings settings, bool startPaused)
    {
        lock (_lifecycle)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            Volatile.Write(ref _settings, settings.Normalize());
            _paused = startPaused;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), token);
            _logger.LogInformation("Monitoring started (paused: {Paused}, interval: {Interval}s, grace: {Grace}s, simulation: {Simulation})",
                startPaused, settings.PollingIntervalSeconds, settings.GracePeriodSeconds, _runtime.Simulation);
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        Volatile.Write(ref _settings, settings.Normalize());
        Wake();
    }

    public void Pause()
    {
        if (!_paused)
        {
            _paused = true;
            _logger.LogInformation("Monitoring paused by user");
            Wake();
        }
    }

    public void Resume()
    {
        if (_paused)
        {
            _paused = false;
            _logger.LogInformation("Monitoring resumed by user");
            Wake();
        }
    }

    public void RequestRefresh() => Wake();

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_lifecycle)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                _logger.LogDebug(ex, "Monitoring loop ended during shutdown");
            }
        }

        _decision.Reset();
        _holding = false;
        _holdingSince = null;
        _power.SetRequested(false, "monitoring stopped");
        try
        {
            await _power.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timed out waiting for the power request to be released");
        }

        _history.CloseAll(HistoryOutcome.MonitoringStopped, _clock.Now);
        DisposeProviders();
        cts.Dispose();
        _logger.LogInformation("Monitoring stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _wake.Dispose();
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitoring cycle failed");
            }

            try
            {
                await _wake.WaitAsync(Volatile.Read(ref _settings).PollingInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs one detection and decision cycle. Exposed for tests.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var settings = Volatile.Read(ref _settings);
        SyncProviders(settings);

        if (_paused)
        {
            var pausedAt = _clock.Now;
            _decision.Reset();
            ApplyPower(hold: false, "monitoring paused", pausedAt, settings);
            _history.CloseAll(HistoryOutcome.MonitoringStopped, pausedAt);
            Publish(new MonitorSnapshot
            {
                Status = MonitorStatus.Paused,
                UpdatedAt = pausedAt,
                Launchers = _lastLaunchers,
                IsPaused = true,
                IsSimulationActive = _runtime.Simulation,
            });
            return;
        }

        var launchers = await PollProvidersAsync(settings, cancellationToken).ConfigureAwait(false);
        var now = _clock.Now;

        var operations = launchers.SelectMany(l => l.Operations).ToList();
        var qualifying = operations.Where(o => SleepPolicy.Qualifies(o, settings, _runtime.SimulationCanPreventSleep)).ToList();
        var qualifyingKeys = qualifying.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var excluded = operations.Where(o => o.State.IsWorking() && !qualifyingKeys.Contains(o.Key)).ToList();

        var decision = _decision.Evaluate(qualifying.Count > 0, now, settings.GracePeriod);
        if (qualifying.Count > 0)
        {
            _lastResponsible = qualifying.Select(o => o.Launcher).Distinct().ToList();
        }

        ApplyPower(decision.ShouldHold, DescribeReason(qualifying, decision), now, settings);

        var events = _history.Update(launchers, qualifyingKeys, decision.ShouldHold, now);
        RaiseActivityNotifications(events, settings, now);
        RaiseErrorNotifications(launchers, settings, now);

        _firstPollCompleted = true;
        _lastLaunchers = launchers;
        Publish(new MonitorSnapshot
        {
            Status = ComputeStatus(decision, launchers),
            UpdatedAt = now,
            Launchers = launchers,
            QualifyingOperations = qualifying,
            ExcludedOperations = excluded,
            SleepPrevented = decision.ShouldHold,
            SleepPreventedSince = _holdingSince,
            GraceEndsAt = decision.GraceEndsAt,
            ResponsibleLaunchers = decision.ShouldHold ? _lastResponsible : [],
            IsSimulationActive = _runtime.Simulation,
        });
    }

    private MonitorStatus ComputeStatus(SleepDecision decision, IReadOnlyList<LauncherSnapshot> launchers)
    {
        if (!_firstPollCompleted && launchers.Count == 0)
        {
            return MonitorStatus.Checking;
        }

        switch (decision.Phase)
        {
            case SleepPhase.Holding:
                return MonitorStatus.SleepPrevented;
            case SleepPhase.Grace:
                return MonitorStatus.GracePeriod;
        }

        var real = launchers.Where(l => l.IsEnabled && !l.IsSimulated).ToList();
        if (real.Count == 0 && !_runtime.Simulation)
        {
            return MonitorStatus.Unavailable;
        }

        if (real.Count > 0 && real.All(l => l.HasError || !l.IsInstalled))
        {
            return MonitorStatus.Unavailable;
        }

        return MonitorStatus.Idle;
    }

    private static string DescribeReason(IReadOnlyList<GameOperation> qualifying, SleepDecision decision)
    {
        if (qualifying.Count > 0)
        {
            return string.Join("; ", qualifying.Select(o => $"{o.Launcher.ToDisplayName()} {o.State.ToDisplayText().ToLowerInvariant()} {o.DisplayTitle}"));
        }

        return decision.Phase == SleepPhase.Grace ? "grace period after activity" : "no qualifying activity";
    }

    private void ApplyPower(bool hold, string reason, DateTimeOffset now, AppSettings settings)
    {
        _power.SetRequested(hold, reason);
        if (hold == _holding)
        {
            return;
        }

        _holding = hold;
        _holdingSince = hold ? now : null;
        if (hold)
        {
            Notify(settings.NotifySleepPreventionActivated, "power", now, new MonitorNotification(
                NotificationKind.SleepPreventionActivated, "Sleep prevented", $"Keeping the PC awake: {reason}."));
        }
        else
        {
            Notify(settings.NotifySleepPreventionReleased, "power", now, new MonitorNotification(
                NotificationKind.SleepPreventionReleased, "Normal sleep restored", "No qualifying downloads remain."));
        }
    }

    private void RaiseActivityNotifications(IReadOnlyList<ActivityEvent> events, AppSettings settings, DateTimeOffset now)
    {
        foreach (var activity in events)
        {
            var entry = activity.Entry;
            var meetsThreshold = entry.Confidence == DetectionConfidence.Confirmed
                || (entry.Confidence == DetectionConfidence.Probable && settings.IncludeProbableActivity);
            if (!meetsThreshold)
            {
                continue;
            }

            var title = entry.GameTitle ?? "Unknown game";
            var launcher = entry.Launcher.ToDisplayName();
            var prefix = entry.IsSimulated ? "[Simulated] " : string.Empty;
            var key = $"{activity.Kind}:{entry.OperationKey}";
            switch (activity.Kind)
            {
                case ActivityEventKind.Started:
                    Notify(settings.NotifyDownloadStarted, key, now, new MonitorNotification(
                        NotificationKind.DownloadStarted, $"{prefix}{launcher}", $"{entry.Operation.ToDisplayText()}: {title}"));
                    break;
                case ActivityEventKind.Completed:
                    Notify(settings.NotifyDownloadCompleted, key, now, new MonitorNotification(
                        NotificationKind.DownloadCompleted, $"{prefix}{launcher}", $"Finished: {title}"));
                    break;
                case ActivityEventKind.Paused:
                    Notify(settings.NotifyDownloadPaused, key, now, new MonitorNotification(
                        NotificationKind.DownloadPaused, $"{prefix}{launcher}", $"Paused: {title}"));
                    break;
            }
        }
    }

    private void RaiseErrorNotifications(IReadOnlyList<LauncherSnapshot> launchers, AppSettings settings, DateTimeOffset now)
    {
        foreach (var launcher in launchers)
        {
            _reportedErrors.TryGetValue(launcher.Launcher, out var previous);
            if (launcher.Error is not null && previous is null)
            {
                Notify(settings.NotifyDetectionErrors, $"error:{launcher.Launcher}", now, new MonitorNotification(
                    NotificationKind.DetectionError, $"{launcher.DisplayName} detection problem", launcher.Error));
            }

            _reportedErrors[launcher.Launcher] = launcher.Error;
        }
    }

    private void Notify(bool enabled, string key, DateTimeOffset now, MonitorNotification notification)
    {
        if (!enabled || !Volatile.Read(ref _settings).ShowNotifications)
        {
            return;
        }

        if (_lastNotified.TryGetValue(key, out var last) && now - last < NotificationCooldown)
        {
            return;
        }

        _lastNotified[key] = now;
        try
        {
            NotificationRaised?.Invoke(this, notification);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification handler failed");
        }
    }

    private void Publish(MonitorSnapshot snapshot)
    {
        Volatile.Write(ref _current, snapshot);
        try
        {
            SnapshotUpdated?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot handler failed");
        }
    }

    private void SyncProviders(AppSettings settings)
    {
        var desired = LauncherKindExtensions.SupportedLaunchers.Where(settings.IsLauncherEnabled).ToList();
        if (_runtime.Simulation)
        {
            desired.Add(LauncherKind.Simulation);
        }

        foreach (var kind in _slots.Keys.Except(desired).ToList())
        {
            var slot = _slots[kind];
            _slots.Remove(kind);
            DisposeSlot(slot);
            _logger.LogInformation("{Launcher} detection disabled", kind);
        }

        foreach (var kind in desired.Where(k => !_slots.ContainsKey(k)))
        {
            try
            {
                _slots[kind] = new ProviderSlot(_factory.Create(kind));
                _logger.LogInformation("{Launcher} detection enabled", kind);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not create the {Launcher} provider", kind);
                _slots[kind] = new ProviderSlot(null) { CreationError = ex.Message };
            }
        }
    }

    private async Task<IReadOnlyList<LauncherSnapshot>> PollProvidersAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var now = _clock.Now;
        var pending = new List<Task<LauncherSnapshot>>();
        foreach (var (kind, slot) in _slots.OrderBy(s => s.Key))
        {
            pending.Add(PollSlotAsync(kind, slot, now, cancellationToken));
        }

        var results = (await Task.WhenAll(pending).ConfigureAwait(false)).ToList();

        foreach (var kind in LauncherKindExtensions.SupportedLaunchers.Where(k => !settings.IsLauncherEnabled(k)))
        {
            results.Add(new LauncherSnapshot
            {
                Launcher = kind,
                CheckedAt = now,
                IsEnabled = false,
                StatusDetail = "Detection disabled in settings",
            });
        }

        return results.OrderBy(r => r.Launcher).ToList();
    }

    private async Task<LauncherSnapshot> PollSlotAsync(LauncherKind kind, ProviderSlot slot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (slot.Provider is null)
        {
            return ErrorSnapshot(kind, now, slot.Last, $"Provider unavailable: {slot.CreationError}");
        }

        if (slot.InFlight is { IsCompleted: false })
        {
            return ErrorSnapshot(kind, now, slot.Last, "The previous check has not finished yet");
        }

        var provider = slot.Provider;
        slot.InFlight = Task.Run(() => provider.PollAsync(cancellationToken).AsTask(), cancellationToken);
        try
        {
            var snapshot = await slot.InFlight.WaitAsync(ProviderTimeout, cancellationToken).ConfigureAwait(false);
            slot.Last = snapshot;
            return snapshot;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("{Launcher} detection timed out after {Timeout}s", kind, ProviderTimeout.TotalSeconds);
            return ErrorSnapshot(kind, now, slot.Last, $"Detection timed out after {ProviderTimeout.TotalSeconds:0} seconds");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Launcher} detection threw an exception", kind);
            return ErrorSnapshot(kind, now, slot.Last, $"Detection failed: {ex.Message}");
        }
    }

    private static LauncherSnapshot ErrorSnapshot(LauncherKind kind, DateTimeOffset now, LauncherSnapshot? last, string error) => new()
    {
        Launcher = kind,
        CheckedAt = now,
        IsInstalled = last?.IsInstalled ?? true,
        IsRunning = last?.IsRunning ?? false,
        ExecutablePath = last?.ExecutablePath,
        LastSuccessfulDetection = last?.LastSuccessfulDetection,
        Error = error,
        IsSimulated = kind == LauncherKind.Simulation,
    };

    private void DisposeProviders()
    {
        foreach (var slot in _slots.Values)
        {
            DisposeSlot(slot);
        }

        _slots.Clear();
    }

    private void DisposeSlot(ProviderSlot slot)
    {
        if (slot.Provider is null)
        {
            return;
        }

        if (slot.InFlight is { IsCompleted: false } inFlight)
        {
            var provider = slot.Provider;
            _ = inFlight.ContinueWith(_ => provider.Dispose(), TaskScheduler.Default);
            return;
        }

        try
        {
            slot.Provider.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider disposal failed");
        }
    }

    private sealed class ProviderSlot(ILauncherDownloadProvider? provider)
    {
        public ILauncherDownloadProvider? Provider { get; } = provider;

        public string? CreationError { get; init; }

        public Task<LauncherSnapshot>? InFlight { get; set; }

        public LauncherSnapshot? Last { get; set; }
    }
}
