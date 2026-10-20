using System.Globalization;

namespace GameWake.Providers.Steam;

/// <summary>Bits of <c>StateFlags</c> in <c>appmanifest_*.acf</c> (Steam's EAppState).</summary>
[Flags]
public enum SteamAppStateFlags : long
{
    None = 0,
    Uninstalled = 0x1,
    UpdateRequired = 0x2,
    FullyInstalled = 0x4,
    Encrypted = 0x8,
    Locked = 0x10,
    FilesMissing = 0x20,
    AppRunning = 0x40,
    FilesCorrupt = 0x80,
    UpdateRunning = 0x100,
    UpdatePaused = 0x200,
    UpdateStarted = 0x400,
    Uninstalling = 0x800,
    BackupRunning = 0x1000,
    Reconfiguring = 0x10000,
    Validating = 0x20000,
    AddingFiles = 0x40000,
    Preallocating = 0x80000,
    Downloading = 0x100000,
    Staging = 0x200000,
    Committing = 0x400000,
    UpdateStopping = 0x800000,
}

/// <summary>
/// The subset of an app manifest GameWake uses. Steam rewrites manifests only on state changes,
/// so these values describe the last saved state, not necessarily the current one.
/// </summary>
public sealed record SteamAppManifest
{
    public required uint AppId { get; init; }

    public string? Name { get; init; }

    public SteamAppStateFlags StateFlags { get; init; }

    public string? InstallDir { get; init; }

    public long BytesToDownload { get; init; }

    public long BytesDownloaded { get; init; }

    public long BytesToStage { get; init; }

    public long BytesStaged { get; init; }

    public long? BuildId { get; init; }

    public long? TargetBuildId { get; init; }

    public required string ManifestPath { get; init; }

    public required string LibraryPath { get; init; }

    public DateTimeOffset LastWriteTime { get; init; }

    public bool IsFullyInstalled => StateFlags.HasFlag(SteamAppStateFlags.FullyInstalled);

    /// <summary>Returns null when the document is not a usable app manifest.</summary>
    public static SteamAppManifest? FromVdf(VdfNode document, string manifestPath, string libraryPath, DateTimeOffset lastWriteTime)
    {
        var state = document["AppState"];
        if (state is null || !state.IsObject)
        {
            return null;
        }

        if (!uint.TryParse(state.GetString("appid"), NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0)
        {
            return null;
        }

        var name = state.GetString("name");
        return new SteamAppManifest
        {
            AppId = appId,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            StateFlags = (SteamAppStateFlags)(state.GetInt64("StateFlags") ?? 0),
            InstallDir = state.GetString("installdir"),
            BytesToDownload = Math.Max(0, state.GetInt64("BytesToDownload") ?? 0),
            BytesDownloaded = Math.Max(0, state.GetInt64("BytesDownloaded") ?? 0),
            BytesToStage = Math.Max(0, state.GetInt64("BytesToStage") ?? 0),
            BytesStaged = Math.Max(0, state.GetInt64("BytesStaged") ?? 0),
            BuildId = state.GetInt64("buildid"),
            TargetBuildId = state.GetInt64("TargetBuildID"),
            ManifestPath = manifestPath,
            LibraryPath = libraryPath,
            LastWriteTime = lastWriteTime,
        };
    }
}
