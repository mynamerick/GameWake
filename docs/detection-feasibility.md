# Download detection feasibility

This document records what GameWake can and cannot know about launcher activity
**without administrator rights**, which evidence each provider relies on, and the
confidence GameWake assigns to each signal. It was written before the providers
were implemented and is updated whenever a signal is verified against real
launcher activity.

Ground rules applied to every provider:

- A launcher process merely running is never treated as evidence of a download.
- Launcher files, databases and registry keys are opened read-only with sharing
  flags that never block the launcher (`FileShare.ReadWrite | FileShare.Delete`).
- No credentials, tokens, cookies or account databases are read.
- No network traffic, no undocumented IPC with launchers, no process injection.
- When the data is not available, GameWake says so instead of estimating.

## Confidence model

| Level | Meaning | Prevents sleep |
| --- | --- | --- |
| Confirmed | The launcher (or Windows) directly reports an active operation for this game | Yes (default) |
| Probable | Several independent indirect signals agree, but nothing states it explicitly | Only if "Include probable activity" is enabled |
| Unknown | Signals are missing, stale or contradict each other | No |
| Inactive | Reliable evidence that nothing is transferring (idle, paused, queued, finished) | No |

Activity is also tagged as *direct* or *inferred* evidence so the UI can show why
a decision was made ("Show provider detection details").

---

## Steam — verified on a real machine

Environment used for verification: Steam client build `1791918634`, two libraries
(`C:\Program Files (x86)\Steam`, `D:\Games`), an active 41 GB first-time install,
a paused 12 GB update, and a history of finished/suspended updates in the logs.

### Evidence sources

| Source | Location | What it gives | Notes |
| --- | --- | --- | --- |
| Install path | `HKCU\Software\Valve\Steam\SteamPath` (fallback `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`) | Steam root | Readable without elevation |
| Active process | `HKCU\Software\Valve\Steam\ActiveProcess\pid` + process table | Steam running, session start time | Session start is used to discard log lines from earlier sessions |
| Libraries | `<Steam>\steamapps\libraryfolders.vdf` | All library roots | Both the current nested format and the legacy flat format are parsed |
| App manifests | `<library>\steamapps\appmanifest_<appid>.acf` | Game name, `StateFlags`, `BytesToDownload`/`BytesDownloaded`, `TargetBuildID` | **Only written on state changes.** During an active download the file kept `BytesDownloaded 0` for 25+ minutes. Never used as proof of activity. |
| Per-app registry flags | `HKCU\Software\Valve\Steam\Apps\<appid>\Updating` | `1` while Steam runs an update/install job for that app | Observed `1` for the downloading app, `0` for the paused app |
| Content log | `<Steam>\logs\content_log.txt` (+ `content_log.previous.txt` after rotation at ~4 MB) | Per-app state transitions, update phases, completion, Steam-measured download rate | Primary evidence. Read incrementally from the last offset. |
| Download staging | `<library>\steamapps\downloading\<appid>` | Existence only | Steam pre-allocates files here, so size growth is **not** a usable signal and is not used. |

### Content log grammar (observed)

```
[2026-10-20 16:22:21] AppID 5280930 state changed : Update Required,Update Queued,Update Running,
[2026-10-20 16:22:23] AppID 5280930 update started : download 0/40938995792, store 0/0, reuse 0/0, delta 0/0, stage 0/47128058401
[2026-10-20 16:22:23] AppID 5280930 App update changed : Running Update,Downloading,Staging,
[2026-10-20 16:23:36] Current download rate: 58.964 Mbps
[2026-10-15 21:12:41] AppID 1867240 App update changed : Running Update,Committing,
[2026-10-15 21:12:41] AppID 1867240 finished update, 1 mounted depots (BuildID 25937608) : ...
[2026-10-15 21:12:41] AppID 1867240 state changed : Fully Installed,
[2026-10-20 16:22:51] AppID 1172710 state changed : Update Required,Fully Installed,Update Paused,
[2026-10-15 18:32:45] AppID 1144200 update canceled : Disabled (Suspended)
[2026-10-15 18:32:45] AppID 1144200 state changed : Update Required,Fully Installed,Update Queued,Update Started, (Suspended)
```

Update phases seen in `App/Workshop/Shader update changed` lines: `Reconfiguring`,
`Verifying Installed`, `Verifying Staged`, `Preallocating`, `Downloading`,
`Staging`, `Committing`, `Stopping`, `None`.

The `Current download rate` line is written roughly once a minute **only while
bytes are being transferred**, which makes it a good liveness signal and a
Steam-measured speed (it is global, not per app).

### Mapping

| Steam evidence | GameWake state |
| --- | --- |
| `Update Running` + phase `Downloading`, app not yet installed | Downloading |
| `Update Running` + phase `Downloading`, app already `Fully Installed` | Updating |
| phase `Verifying Installed` / `Verifying Staged` | Verifying |
| phase `Staging` without `Downloading` | Unpacking |
| phase `Preallocating`, `Reconfiguring`, `Committing`; state `Moving Folder` | Installing |
| `Update Paused`, or `(Suspended)` without `Update Running` | Paused |
| `Update Queued` without `Update Running` | Queued |
| `finished update` | Completed |
| Workshop / shader-cache phase `Downloading` | Downloading (labelled as workshop or shader content) |

### Confidence rules

- Steam not running → *Not running*. Manifest and registry flags are ignored
  because they can be stale after a crash.
- Log reports an active phase since the current Steam session started → **Confirmed**.
  If the phase is `Downloading` but neither the registry flag nor a fresh download-rate
  line has been seen for 10 minutes, it is downgraded to **Unknown** (guards against
  a missed log line keeping the PC awake forever).
- Registry `Updating=1` **and** a fresh download-rate line, without a log phase
  (e.g. the log rotated) → **Confirmed**.
- Registry `Updating=1` alone → **Probable**.
- Paused / queued / completed → **Inactive**.

### Limitations

- **Live progress is not available locally.** Steam only writes `BytesDownloaded`
  to the manifest when the state changes (pause, stop, finish). GameWake shows the
  total size from the `update started` line and shows the saved percentage only for
  paused downloads, labelled with the time Steam saved it.
- Download speed is Steam's own global measurement. It is attributed to a game only
  when exactly one Steam transfer is active.
- Steam's log format is not a public API. It has been stable for years, but a
  Steam update could change it; the parser ignores unknown lines and the provider
  falls back to Probable via the registry flag.

---

## Epic Games Launcher — not installed on the build machine (unverified)

### Evidence sources

| Source | Location | What it gives |
| --- | --- | --- |
| Data path | `HKLM\SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher\AppDataPath` (fallback `C:\ProgramData\Epic\EpicGamesLauncher\Data\`) | Manifest root |
| Installed manifests | `Data\Manifests\*.item` (JSON) | `DisplayName`, `AppName`, `InstallLocation`, `StagingLocation`, `bIsIncompleteInstall` |
| Pending manifests | `Data\Manifests\Pending\*.item` | Written when an install/update starts, removed when it completes. Also left behind by interrupted installs. |
| Staging folder | `<InstallLocation>\.egstore\bps` (`StagingLocation`) | Build-patch staging written during transfers |
| Launcher log | `%LOCALAPPDATA%\EpicGamesLauncher\Saved\Logs\EpicGamesLauncher.log` | `LogBuildPatchServices` lines while the patcher runs (not reliably per game) |
| Process | `EpicGamesLauncher.exe` | Supporting evidence only |

### What is not available

- Epic writes no local, per-game "downloading / paused" state that GameWake could
  verify. A pending manifest exists for both active and paused/abandoned installs.
- No progress or speed is exposed locally.

### Strategy

For each pending manifest GameWake watches the game's install and staging
folders with a `FileSystemWatcher` (event-driven, no scanning). If the launcher is
running **and** a pending manifest exists **and** files are being written in that
game's folders → **Probable** (three independent signals). A pending manifest with
no disk activity is shown as **Unknown** ("paused, queued or stalled"). Epic
activity therefore does not prevent sleep unless the user enables
"Include probable activity".

---

## GOG Galaxy — not installed on the build machine (unverified)

### Evidence sources

| Source | Location | What it gives |
| --- | --- | --- |
| Client path | `HKLM\SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths\client` | Install detection |
| Client log | `%ProgramData%\GOG.com\Galaxy\logs\GalaxyClient.log` | Job state lines, e.g. `Job 'ProductInstall (1361243432) id: 3 state: Failed' state changed`, and `Processing update request for product ID: …, target path …` |
| Installed games | `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\<productId>` (`gameName`, `path`) | Game names for installed / updating games |
| Download temp | `<game folder>\!Temp\*.download` | Written while Galaxy downloads |
| Database | `%ProgramData%\GOG.com\Galaxy\storage\galaxy-2.0.db` | **Not used.** It is a live SQLite database in WAL mode; opening it could interfere with Galaxy, and it would add a native dependency. |
| Process | `GalaxyClient.exe` | Supporting evidence only |

### Strategy

Job lines are parsed generically (`Job '<type> (<productId>) id: <n> state: <state>'`).
Only `Failed` has been confirmed from public logs; `Running`, `Paused`, `Finished`,
`Canceled` and similar values are mapped by keyword, and unrecognised state words
produce **Unknown** instead of a guess.

- Running job + Galaxy running → **Probable**.
- …plus disk writes in the job's target folder or Galaxy installer-thread log
  activity in the last 3 minutes → **Confirmed**.
- Paused / queued / finished / failed → **Inactive**.

Progress and speed are not exposed locally and are shown as unavailable.

---

## Xbox app / Microsoft Gaming Services / PC Game Pass — partially verifiable

Environment: Gaming Services `38.118.9001.0` and the Xbox app are installed, but no
games are installed and no install could be started during development.

### Evidence sources

| Source | API | What it gives |
| --- | --- | --- |
| Package catalog events | `Windows.ApplicationModel.PackageCatalog.OpenForCurrentUser()` → `PackageStaging`, `PackageInstalling`, `PackageUpdating`, `PackageStatusChanged` | Documented: "package events will be received for all packages being installed for the current user", including `Progress` and `IsComplete` |
| Package status | `Windows.Management.Deployment.PackageManager.FindPackagesForUser("")` → `Package.Status.DeploymentInProgress` | Documented flag for packages being deployed; swept once a minute |
| Xbox game folders | `<drive>:\XboxGames\<game>\Content` | Default install location; write activity observed with a `FileSystemWatcher` |
| Processes | `gamingservices.exe`, `XboxPcApp.exe` | Supporting evidence only |

### Not used, and why

- **BITS**: Xbox/Store game content does not use BITS; enumerating other callers' jobs needs elevation anyway.
- **Delivery Optimization**: enumerating other callers' downloads is not available to standard users.
- **`HKLM\SOFTWARE\Microsoft\GamingServices`**: parts of it are access-denied for standard users (verified) and the layout is undocumented.
- **`WindowsApps`**: protected; never enumerated or opened.
- **GDK `XPackage*` APIs**: designed for a game monitoring its own package; not a supported way for a third-party tool to watch other games.

### Strategy

- Catalog event for a package under an `XboxGames` folder, not complete, updated in the last 5 minutes → **Confirmed**, with Windows-reported progress.
- Catalog event or `DeploymentInProgress` for a package that cannot be identified as a game → capped at **Probable** and labelled "Microsoft Store package".
- Write activity in `XboxGames\<game>\Content` while Gaming Services is running → **Probable**, titled from the folder name and labelled as such.

Whether Gaming Services raises catalog events during the **download** phase of an
MSIXVC game (as opposed to only at registration) could not be verified. If it does
not, Xbox downloads surface only as Probable folder activity.

---

## Summary

| Launcher | Game name | Progress | Speed | Best confidence | Verified |
| --- | --- | --- | --- | --- | --- |
| Steam | Yes (manifest / registry) | Paused downloads only | Yes (Steam-measured, global) | Confirmed | Yes |
| Epic | Yes (manifest `DisplayName`) | No | No | Probable | No |
| GOG Galaxy | When registered in registry; otherwise folder name or product ID | No | No | Confirmed (with corroboration) | No |
| Xbox | When Windows exposes the package display name | When Windows reports it | No | Confirmed (catalog events) | Partially |
