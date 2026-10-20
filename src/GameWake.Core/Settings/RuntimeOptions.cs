namespace GameWake.Core.Settings;

/// <summary>Command-line switches. These are never persisted.</summary>
public sealed record RuntimeOptions
{
    /// <summary>Launched by the per-user startup entry.</summary>
    public bool LaunchedAtStartup { get; init; }

    /// <summary>Development-only: adds a clearly labelled simulated launcher for UI testing.</summary>
    public bool Simulation { get; init; }

    /// <summary>Development-only: lets simulated activity prevent sleep. Off unless explicitly passed.</summary>
    public bool SimulationCanPreventSleep { get; init; }

    public static RuntimeOptions Parse(IEnumerable<string> args)
    {
        var set = new HashSet<string>(args.Select(a => a.Trim().ToLowerInvariant()));
        var allowSleep = set.Contains("--simulate-prevent-sleep");
        return new RuntimeOptions
        {
            LaunchedAtStartup = set.Contains("--autostart"),
            Simulation = set.Contains("--simulate") || allowSleep,
            SimulationCanPreventSleep = allowSleep,
        };
    }
}
