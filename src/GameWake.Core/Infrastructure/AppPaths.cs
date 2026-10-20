namespace GameWake.Core.Infrastructure;

/// <summary>Per-user storage locations. Everything lives under %LOCALAPPDATA%\GameWake.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string HistoryFile => Path.Combine(Root, "history.json");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public static AppPaths ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameWake"));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}
