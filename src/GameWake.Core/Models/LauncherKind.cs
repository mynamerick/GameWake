namespace GameWake.Core.Models;

public enum LauncherKind
{
    Steam,
    Xbox,
    Epic,
    Gog,
    Simulation,
}

public static class LauncherKindExtensions
{
    public static IReadOnlyList<LauncherKind> SupportedLaunchers { get; } =
        [LauncherKind.Steam, LauncherKind.Xbox, LauncherKind.Epic, LauncherKind.Gog];

    public static string ToDisplayName(this LauncherKind kind) => kind switch
    {
        LauncherKind.Steam => "Steam",
        LauncherKind.Xbox => "Xbox",
        LauncherKind.Epic => "Epic Games",
        LauncherKind.Gog => "GOG Galaxy",
        LauncherKind.Simulation => "Simulation",
        _ => kind.ToString(),
    };
}
