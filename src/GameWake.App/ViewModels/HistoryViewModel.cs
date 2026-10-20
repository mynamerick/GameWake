using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameWake.App.Services;
using GameWake.App.Themes;
using GameWake.Core.History;
using GameWake.Core.Models;

namespace GameWake.App.ViewModels;

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppHost _host;

    public HistoryViewModel(AppHost host)
    {
        _host = host;
        host.History.Changed += (_, _) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                Reload();
            }
            else
            {
                dispatcher.BeginInvoke(Reload);
            }
        };
        Reload();
    }

    public ObservableCollection<HistoryRowViewModel> Entries { get; } = [];

    [ObservableProperty] private bool _isEmpty = true;

    public void Reload()
    {
        var now = DateTimeOffset.Now;
        var entries = _host.History.GetEntries();
        while (Entries.Count > entries.Count)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (i == Entries.Count)
            {
                Entries.Add(new HistoryRowViewModel());
            }

            Entries[i].Apply(entries[i], now);
        }

        IsEmpty = Entries.Count == 0;
    }

    [RelayCommand]
    private void Clear()
    {
        var result = System.Windows.MessageBox.Show(
            "Clear the saved activity history? This cannot be undone.",
            "GameWake",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        _host.History.Clear();
        _host.PersistHistory();
    }
}

public sealed partial class HistoryRowViewModel : ObservableObject
{
    [ObservableProperty] private string _launcher = string.Empty;
    [ObservableProperty] private string _game = "Unknown game";
    [ObservableProperty] private string _operation = string.Empty;
    [ObservableProperty] private string _started = string.Empty;
    [ObservableProperty] private string _finished = "—";
    [ObservableProperty] private string _duration = "—";
    [ObservableProperty] private string _sleep = "No";
    [ObservableProperty] private string _outcome = string.Empty;
    [ObservableProperty] private Tone _tone = Tone.Neutral;
    [ObservableProperty] private bool _isSimulated;

    public void Apply(ActivityHistoryEntry entry, DateTimeOffset now)
    {
        Launcher = entry.Launcher.ToDisplayName();
        Game = string.IsNullOrWhiteSpace(entry.GameTitle) ? "Unknown game" : entry.GameTitle;
        Operation = entry.Operation.ToDisplayText();
        if (entry.Phases.Count > 1)
        {
            Operation = string.Join(" → ", entry.Phases.Select(p => p.ToDisplayText()));
        }

        Started = Format.Time(entry.StartedAt, now);
        Finished = entry.FinishedAt is { } finished ? Format.Time(finished, now) : "In progress";
        var duration = entry.Duration ?? (now - entry.StartedAt);
        Duration = Format.Duration(duration);
        Sleep = entry.SleepPrevented ? "Sleep prevented" : "No";
        Outcome = entry.Outcome switch
        {
            HistoryOutcome.InProgress => "In progress",
            HistoryOutcome.Completed => "Finished",
            HistoryOutcome.Paused => "Paused",
            HistoryOutcome.Stopped => "Stopped",
            HistoryOutcome.LauncherClosed => "Launcher closed",
            HistoryOutcome.MonitoringStopped => "Monitoring stopped",
            _ => entry.Outcome.ToString(),
        };
        Tone = entry.Outcome switch
        {
            HistoryOutcome.InProgress => LauncherCardViewModel.ToneFor(entry.Operation),
            HistoryOutcome.Completed => Tone.Success,
            HistoryOutcome.Paused => Tone.Warning,
            _ => Tone.Neutral,
        };
        IsSimulated = entry.IsSimulated;
    }
}
