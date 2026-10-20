using GameWake.Core.Models;

namespace GameWake.Core.Providers;

/// <summary>
/// Detects download, update and install activity for one launcher.
/// Implementations must never treat "launcher is running" as evidence of a download,
/// must never modify launcher files, and must report unavailable data instead of estimating it.
/// </summary>
public interface ILauncherDownloadProvider : IDisposable
{
    LauncherKind Launcher { get; }

    /// <summary>
    /// Takes a fresh look at the launcher. Called from a background thread, never concurrently
    /// for the same provider instance.
    /// </summary>
    ValueTask<LauncherSnapshot> PollAsync(CancellationToken cancellationToken);
}

public interface ILauncherProviderFactory
{
    ILauncherDownloadProvider Create(LauncherKind kind);
}
