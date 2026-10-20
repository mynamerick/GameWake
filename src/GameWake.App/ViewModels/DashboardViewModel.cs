using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using GameWake.App.Services;
using GameWake.App.Themes;
using GameWake.Core.Models;
using GameWake.Core.Settings;

namespace GameWake.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    public ObservableCollection<LauncherCardViewModel> Cards { get; } = [];

    [ObservableProperty] private string _statusTitle = "Checking activity";
    [ObservableProperty] private string _statusSubtitle = "GameWake is looking at launcher activity.";
    [ObservableProperty] private string _statusHint = string.Empty;
    [ObservableProperty] private Tone _statusTone = Tone.Neutral;
    [ObservableProperty] private string _activeCountText = "—";
    [ObservableProperty] private string _durationText = "—";
    [ObservableProperty] private string _responsibleText = "—";
    [ObservableProperty] private bool _isSimulation;
    [ObservableProperty] private string _simulationBanner = string.Empty;
    [ObservableProperty] private bool _showSimulationBanner;

    public void Apply(MonitorSnapshot snapshot, AppSettings settings, DateTimeOffset now)
    {
        (StatusTitle, StatusSubtitle, StatusHint, StatusTone) = Describe(snapshot, now);
        var qualifying = snapshot.QualifyingOperations;
        ActiveCountText = snapshot.Status is MonitorStatus.Checking
            ? "—"
            : qualifying.Count == 1 ? "1 active" : $"{qualifying.Count} active";
        DurationText = snapshot.SleepPreventedSince is { } since
            ? Format.Clock(now - since)
            : snapshot.Status == MonitorStatus.GracePeriod && snapshot.GraceEndsAt is { } ends
                ? "until " + Format.Time(ends, now)
                : "Off";
        ResponsibleText = snapshot.ResponsibleLaunchers.Count == 0
            ? "None"
            : string.Join(" · ", snapshot.ResponsibleLaunchers.Select(l => l.ToDisplayName()));

        IsSimulation = snapshot.IsSimulationActive;
        ShowSimulationBanner = snapshot.IsSimulationActive;
        SimulationBanner = snapshot.IsSimulationActive
            ? "Simulation mode is on. Simulated activity is not a real download and does not prevent sleep unless you passed --simulate-prevent-sleep."
            : string.Empty;

        SyncCards(snapshot, settings, now);
    }

    public void TickDuration(MonitorSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.SleepPreventedSince is { } since)
        {
            DurationText = Format.Clock(now - since);
        }
    }

    private void SyncCards(MonitorSnapshot snapshot, AppSettings settings, DateTimeOffset now)
    {
        var wanted = snapshot.Launchers
            .Where(l => l.Launcher != LauncherKind.Simulation || snapshot.IsSimulationActive)
            .ToList();
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (wanted.All(l => l.Launcher != Cards[i].Kind))
            {
                Cards.RemoveAt(i);
            }
        }

        foreach (var launcher in wanted)
        {
            var card = Cards.FirstOrDefault(c => c.Kind == launcher.Launcher);
            if (card is null)
            {
                card = new LauncherCardViewModel();
                var index = wanted.FindIndex(l => l.Launcher == launcher.Launcher);
                Cards.Insert(Math.Clamp(index, 0, Cards.Count), card);
            }

            card.Apply(launcher, settings.ShowDetectionDetails, now);
        }
    }

    private static (string Title, string Subtitle, string Hint, Tone Tone) Describe(MonitorSnapshot snapshot, DateTimeOffset now)
    {
        switch (snapshot.Status)
        {
            case MonitorStatus.Checking:
                return ("Checking activity", "GameWake is looking at launcher activity.", string.Empty, Tone.Neutral);
            case MonitorStatus.SleepPrevented:
                var names = snapshot.QualifyingOperations
                    .Select(o => o.DisplayTitle)
                    .Distinct()
                    .Take(3)
                    .ToList();
                var who = names.Count == 0
                    ? snapshot.ResponsibleLaunchers.FirstOrDefault().ToDisplayName()
                    : names.Count == 1 ? names[0] : $"{names.Count} games";
                var launchers = string.Join(", ", snapshot.ResponsibleLaunchers.Select(l => l.ToDisplayName()));
                return ("Downloads active — sleep prevented",
                    $"Keeping the PC awake while {launchers} works on {who}.",
                    "The monitor can still turn off. Your power plan is unchanged.",
                    Tone.Active);
            case MonitorStatus.GracePeriod:
                var remaining = snapshot.GraceEndsAt is { } ends ? Format.Duration(ends - now) : "a moment";
                return ("Activity stopped — grace period",
                    $"Normal sleep returns in {remaining}.",
                    "A short delay avoids toggling if a download briefly disappears from the logs.",
                    Tone.Working);
            case MonitorStatus.Paused:
                return ("Monitoring paused", "Normal sleep is enabled while monitoring is paused.", string.Empty, Tone.Warning);
            case MonitorStatus.Unavailable:
                return ("Detection unavailable", "No enabled launcher can currently be checked.", "Turn on a launcher in Settings, or install Steam, Xbox, Epic or GOG Galaxy.", Tone.Warning);
            default:
                return ("No active downloads — normal sleep enabled",
                    "Launchers may be running; nothing confirmed is downloading.",
                    string.Empty,
                    Tone.Neutral);
        }
    }
}

public sealed partial class LauncherCardViewModel : ObservableObject
{
    public ObservableCollection<OperationRowViewModel> Operations { get; } = [];
    public ObservableCollection<string> Details { get; } = [];

    [ObservableProperty] private LauncherKind _kind;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _monogram = "?";
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _hasIcon;
    [ObservableProperty] private Brush _accent = Palette.Brush(Palette.Grey);
    [ObservableProperty] private string _runningText = "Not running";
    [ObservableProperty] private Tone _runningTone = Tone.Neutral;
    [ObservableProperty] private string _stateText = "Not running";
    [ObservableProperty] private Tone _stateTone = Tone.Neutral;
    [ObservableProperty] private string _confidenceText = string.Empty;
    [ObservableProperty] private Tone _confidenceTone = Tone.Neutral;
    [ObservableProperty] private string _statusDetail = string.Empty;
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private string _checkedText = string.Empty;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _showDetails;
    [ObservableProperty] private bool _isSimulated;
    [ObservableProperty] private bool _hasOperations;

    public void Apply(LauncherSnapshot snapshot, bool showDetails, DateTimeOffset now)
    {
        Kind = snapshot.Launcher;
        Name = snapshot.DisplayName;
        Monogram = Name.Length == 0 ? "?" : char.ToUpperInvariant(Name[0]).ToString();
        Accent = Palette.Brush(LauncherLogos.Tile(snapshot.Launcher));
        Icon = LauncherLogos.For(snapshot.Launcher);
        HasIcon = true;
        IsEnabled = snapshot.IsEnabled;
        HasError = snapshot.HasError;
        Error = snapshot.Error;
        IsSimulated = snapshot.IsSimulated;
        StatusDetail = snapshot.StatusDetail ?? string.Empty;
        RunningText = !snapshot.IsEnabled ? "Disabled" : !snapshot.IsInstalled ? "Not installed" : snapshot.IsRunning ? "Running" : "Not running";
        RunningTone = snapshot.IsRunning ? Tone.Success : Tone.Neutral;
        StateText = snapshot.SummaryState.ToDisplayText();
        StateTone = ToneFor(snapshot.SummaryState);
        ConfidenceText = snapshot.IsRunning && snapshot.IsEnabled ? snapshot.SummaryConfidence.ToDisplayText() : string.Empty;
        ConfidenceTone = snapshot.SummaryConfidence switch
        {
            DetectionConfidence.Confirmed => Tone.Success,
            DetectionConfidence.Probable => Tone.Working,
            DetectionConfidence.Unknown => Tone.Warning,
            _ => Tone.Neutral,
        };
        SpeedText = snapshot.TotalBytesPerSecond is { } bps
            ? Format.Speed(bps) + (snapshot.TotalSpeedNote is { } note ? $" · {note}" : string.Empty)
            : string.Empty;
        CheckedText = "Checked " + Format.Time(snapshot.CheckedAt, now);

        SyncOperations(snapshot.Operations);
        HasOperations = Operations.Count > 0;
        ShowDetails = showDetails;
        Details.Clear();
        if (showDetails)
        {
            foreach (var line in snapshot.Diagnostics)
            {
                Details.Add(line);
            }

            foreach (var line in snapshot.Limitations)
            {
                Details.Add(line);
            }
        }
    }

    private void SyncOperations(IReadOnlyList<GameOperation> operations)
    {
        while (Operations.Count > operations.Count)
        {
            Operations.RemoveAt(Operations.Count - 1);
        }

        for (var i = 0; i < operations.Count; i++)
        {
            if (i == Operations.Count)
            {
                Operations.Add(new OperationRowViewModel());
            }

            Operations[i].Apply(operations[i]);
        }
    }

    internal static Tone ToneFor(ActivityState state) => state switch
    {
        ActivityState.Downloading => Tone.Active,
        ActivityState.Updating or ActivityState.Installing or ActivityState.Unpacking or ActivityState.Verifying => Tone.Working,
        ActivityState.Paused or ActivityState.Unknown => Tone.Warning,
        ActivityState.Completed => Tone.Success,
        _ => Tone.Neutral,
    };
}

public sealed partial class OperationRowViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "Unknown game";
    [ObservableProperty] private string? _titleNote;
    [ObservableProperty] private bool _hasTitleNote;
    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private Tone _stateTone = Tone.Neutral;
    [ObservableProperty] private string _confidenceText = string.Empty;
    [ObservableProperty] private string? _stateDetail;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = "Progress unavailable";
    [ObservableProperty] private bool _hasProgressText = true;
    [ObservableProperty] private string? _progressNote;
    [ObservableProperty] private bool _hasProgressNote;
    [ObservableProperty] private string _metaText = string.Empty;
    [ObservableProperty] private bool _hasMeta;
    [ObservableProperty] private bool _isSimulated;
    [ObservableProperty] private string _evidence = string.Empty;

    public void Apply(GameOperation operation)
    {
        Title = operation.DisplayTitle;
        TitleNote = operation.TitleNote;
        HasTitleNote = !string.IsNullOrWhiteSpace(operation.TitleNote);
        StateText = operation.State.ToDisplayText();
        StateTone = LauncherCardViewModel.ToneFor(operation.State);
        ConfidenceText = operation.Confidence.ToDisplayText();
        StateDetail = operation.StateDetail;
        IsSimulated = operation.IsSimulated;
        HasProgress = operation.Progress is not null;
        ProgressPercent = (operation.Progress ?? 0) * 100;
        ProgressNote = string.IsNullOrWhiteSpace(operation.ProgressNote) ? null : operation.ProgressNote;
        HasProgressNote = ProgressNote is not null;
        if (operation.Progress is { } p)
        {
            ProgressText = $"{p:P0}";
            HasProgressText = true;
        }
        else if (HasProgressNote)
        {
            ProgressText = string.Empty;
            HasProgressText = false;
        }
        else
        {
            ProgressText = "Progress unavailable";
            HasProgressText = true;
        }
        var speed = operation.BytesPerSecond is { } bps ? Format.Speed(bps) : null;
        var size = operation.TotalBytes is { } bytes ? Format.Bytes(bytes) : null;
        MetaText = string.Join("  ·  ", new[] { speed, size }.Where(s => !string.IsNullOrEmpty(s)));
        HasMeta = MetaText.Length > 0;
        Evidence = operation.Evidence.Count == 0 ? string.Empty : string.Join(Environment.NewLine, operation.Evidence);
    }
}
