# GameWake

This app was vibe coded.

Windows tray utility that keeps the PC awake **only** while Steam, Xbox / PC Game Pass, Epic Games or GOG Galaxy is actually downloading, updating, verifying, unpacking or installing. A launcher that is merely running is never treated as a download. When the work stops, GameWake drops its sleep request and leaves your power plan alone.

## Download

Grab the latest `GameWake-*-win-x64.zip` from this repository's **Releases** page. Unzip and run `GameWake.exe`. No installer, no admin.

The build is self-contained (.NET is bundled) and unsigned. Windows SmartScreen may warn on first launch; that’s expected until someone code-signs it.

## What it does

- Watches enabled launchers on a background loop (default every 5 seconds)
- Calls `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` on one dedicated thread while confirmed work is in progress
- Lets the display turn off
- Never changes the Windows power plan
- Stores settings, history and logs only under `%LOCALAPPDATA%\GameWake`

Only **confirmed** activity prevents sleep by default. **Probable** (inferred) activity is shown on the dashboard and ignored unless you turn on *Include probable activity*.

| Launcher | Status |
| --- | --- |
| Steam | Live-verified (client build `1791918634`) plus tests |
| Xbox / PC Game Pass | Implemented honestly; catalog-during-download not live-verified |
| Epic Games | Implemented; not live-verified (launcher wasn’t installed on the build PC) |
| GOG Galaxy | Implemented; not live-verified |

If a launcher cannot be read, GameWake reports Unknown / Unavailable instead of inventing titles, progress or speeds. Evidence sources and limits: [docs/detection-feasibility.md](docs/detection-feasibility.md).

## Privacy

Local-only. No telemetry, analytics, accounts, ads or background updates. The only network call is an optional **Check for updates** on the Advanced page, which hits GitHub when you click it. No PowerShell, no writes to launcher files, no credentials.

## Build

Windows 10 (1809+) or Windows 11. [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet test GameWake.sln -c Release
dotnet publish src\GameWake.App\GameWake.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\GameWake
```

`publish\` is gitignored. Run from source with `dotnet run --project src\GameWake.App\GameWake.App.csproj`.

Development-only switches (never persisted):

| Switch | Effect |
| --- | --- |
| `--autostart` | Used by the per-user startup entry |
| `--simulate` | Labelled fake launcher for UI testing. Does **not** prevent sleep |
| `--simulate-prevent-sleep` | Same, and lets simulated activity hold a sleep request |

## Release

CI runs tests on every push and pull request (Windows). Pushing a tag `v1.0.0` publishes a GitHub Release with the zip attached.

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## NuGet packages

| Package | Why |
| --- | --- |
| CommunityToolkit.Mvvm | MVVM source generators for the WPF UI |
| Microsoft.Extensions.DependencyInjection | Process composition |
| Microsoft.Extensions.Logging | Structured file logging |
| xunit / Microsoft.NET.Test.Sdk | Automated tests |

No other third-party packages are used at runtime.

## License

[MIT](LICENSE)

Steam, Xbox, Epic Games and GOG are trademarks of their owners. GameWake is not affiliated with them. Launcher marks are used only to identify those apps.
