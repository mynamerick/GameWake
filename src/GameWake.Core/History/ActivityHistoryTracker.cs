using GameWake.Core.Models;

namespace GameWake.Core.History;

public enum ActivityEventKind
{
    Started,
    Completed,
    Paused,
    Stopped,
}

public sealed record ActivityEvent(ActivityEventKind Kind, ActivityHistoryEntry Entry, bool Qualifies);

/// <summary>
/// Turns successive snapshots into history entries. An entry starts when an operation is seen
/// working with at least probable confidence and ends when the launcher reports it completed,
/// paused or gone. Transient detection errors keep entries open rather than ending them.
/// </summary>
public sealed class ActivityHistoryTracker
{
    public const int MaxEntries = 300;

    private static readonly TimeSpan UnknownTimeout = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly List<ActivityHistoryEntry> _entries;
    private readonly Dictionary<string, ActivityHistoryEntry> _open = new(StringComparer.Ordinal);
    private bool _dirty;
    private bool _visibleChange;

    public ActivityHistoryTracker(IEnumerable<ActivityHistoryEntry>? existing = null)
    {
        _entries = existing?.OrderBy(e => e.StartedAt).ToList() ?? [];
        foreach (var stale in _entries.Where(e => e.Outcome == HistoryOutcome.InProgress))
        {
            stale.Outcome = HistoryOutcome.MonitoringStopped;
            _dirty = true;
        }
    }

    public event EventHandler? Changed;

    public bool IsDirty
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    /// <summary>Copies of all entries, newest first.</summary>
    public IReadOnlyList<ActivityHistoryEntry> GetEntries()
    {
        lock (_gate)
        {
            return _entries.OrderByDescending(e => e.StartedAt).Select(e => e.Clone()).ToList();
        }
    }

    public IReadOnlyList<ActivityEvent> Update(
        IReadOnlyList<LauncherSnapshot> snapshots,
        IReadOnlySet<string> qualifyingKeys,
        bool sleepHeld,
        DateTimeOffset now)
    {
        var events = new List<ActivityEvent>();
        bool visibleChange;
        lock (_gate)
        {
            _visibleChange = false;
            foreach (var snapshot in snapshots)
            {
                UpdateLauncher(snapshot, qualifyingKeys, sleepHeld, now, events);
            }

            Trim();
            visibleChange = _visibleChange || events.Count > 0;
        }

        if (visibleChange)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return events;
    }

    /// <summary>Ends every open entry, e.g. when monitoring stops or the app exits.</summary>
    public void CloseAll(HistoryOutcome outcome, DateTimeOffset now)
    {
        bool closedAny;
        lock (_gate)
        {
            var open = _open.Values.ToList();
            foreach (var entry in open)
            {
                Close(entry, outcome, now);
            }

            closedAny = open.Count > 0;
        }

        if (closedAny)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _open.Clear();
            _dirty = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns the entries to persist when something changed since the last call.</summary>
    public IReadOnlyList<ActivityHistoryEntry>? TakeChangesForSave()
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return null;
            }

            _dirty = false;
            return _entries.Select(e => e.Clone()).ToList();
        }
    }

    private void UpdateLauncher(
        LauncherSnapshot snapshot,
        IReadOnlySet<string> qualifyingKeys,
        bool sleepHeld,
        DateTimeOffset now,
        List<ActivityEvent> events)
    {
        var openForLauncher = _open.Values.Where(e => e.Launcher == snapshot.Launcher).ToList();

        if (snapshot.HasError)
        {
            return;
        }

        if (!snapshot.IsEnabled || !snapshot.IsRunning)
        {
            foreach (var entry in openForLauncher)
            {
                Close(entry, snapshot.IsEnabled ? HistoryOutcome.LauncherClosed : HistoryOutcome.MonitoringStopped, now);
                events.Add(new ActivityEvent(ActivityEventKind.Stopped, entry.Clone(), false));
            }

            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in snapshot.Operations)
        {
            seen.Add(operation.Key);
            var qualifies = qualifyingKeys.Contains(operation.Key);
            _open.TryGetValue(operation.Key, out var entry);

            if (operation.State.IsWorking() && operation.Confidence >= DetectionConfidence.Probable)
            {
                if (entry is null)
                {
                    entry = new ActivityHistoryEntry
                    {
                        OperationKey = operation.Key,
                        Launcher = operation.Launcher,
                        GameId = operation.GameId,
                        GameTitle = operation.GameTitle,
                        Operation = operation.State,
                        Phases = [operation.State],
                        StartedAt = now,
                        Outcome = HistoryOutcome.InProgress,
                        Confidence = operation.Confidence,
                        IsSimulated = operation.IsSimulated,
                    };
                    _open[operation.Key] = entry;
                    _entries.Add(entry);
                    MarkChanged();
                    events.Add(new ActivityEvent(ActivityEventKind.Started, entry.Clone(), qualifies));
                }
                else if (entry.Phases.Count == 0 || entry.Phases[^1] != operation.State)
                {
                    entry.Phases.Add(operation.State);
                    MarkChanged();
                }

                if (entry.GameTitle is null && operation.GameTitle is not null)
                {
                    entry.GameTitle = operation.GameTitle;
                    MarkChanged();
                }

                entry.LastWorkingAt = now;
                if (operation.Confidence > entry.Confidence)
                {
                    entry.Confidence = operation.Confidence;
                    MarkChanged();
                }

                if (sleepHeld && qualifies && !entry.SleepPrevented)
                {
                    entry.SleepPrevented = true;
                    MarkChanged();
                }

                continue;
            }

            if (entry is null)
            {
                continue;
            }

            switch (operation.State)
            {
                case ActivityState.Completed:
                    Close(entry, HistoryOutcome.Completed, now);
                    events.Add(new ActivityEvent(ActivityEventKind.Completed, entry.Clone(), entry.SleepPrevented || qualifies));
                    break;
                case ActivityState.Paused:
                    Close(entry, HistoryOutcome.Paused, now);
                    events.Add(new ActivityEvent(ActivityEventKind.Paused, entry.Clone(), entry.SleepPrevented || qualifies));
                    break;
                case ActivityState.Queued:
                case ActivityState.Idle:
                case ActivityState.NotRunning:
                    Close(entry, HistoryOutcome.Stopped, now);
                    events.Add(new ActivityEvent(ActivityEventKind.Stopped, entry.Clone(), false));
                    break;
                default:
                    // Unknown evidence: keep the entry open for a while instead of guessing.
                    if (now - entry.LastWorkingAt > UnknownTimeout)
                    {
                        Close(entry, HistoryOutcome.Stopped, entry.LastWorkingAt);
                        events.Add(new ActivityEvent(ActivityEventKind.Stopped, entry.Clone(), false));
                    }

                    break;
            }
        }

        foreach (var entry in openForLauncher.Where(e => e.OperationKey is not null && !seen.Contains(e.OperationKey)))
        {
            Close(entry, HistoryOutcome.Stopped, now);
            events.Add(new ActivityEvent(ActivityEventKind.Stopped, entry.Clone(), false));
        }
    }

    private void Close(ActivityHistoryEntry entry, HistoryOutcome outcome, DateTimeOffset at)
    {
        entry.Outcome = outcome;
        entry.FinishedAt = at;
        if (entry.OperationKey is not null)
        {
            _open.Remove(entry.OperationKey);
        }

        MarkChanged();
    }

    private void MarkChanged()
    {
        _dirty = true;
        _visibleChange = true;
    }

    private void Trim()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        var removable = _entries
            .Where(e => e.Outcome != HistoryOutcome.InProgress)
            .OrderBy(e => e.StartedAt)
            .Take(_entries.Count - MaxEntries)
            .ToList();
        foreach (var entry in removable)
        {
            _entries.Remove(entry);
        }

        _dirty = true;
    }
}
