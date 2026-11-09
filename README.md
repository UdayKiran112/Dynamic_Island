# Dynamic Island

A lightweight, resource-efficient Dynamic Island for Windows 10/11.

A compact, animated pill docked at the top-center of your screen that expands on demand to show what matters, then gets out of the way.

![status](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)

## Features

- **Media controls** — track, artist, album art, play/pause/skip for Spotify, browsers and any app that exposes a Windows media session (GSMTC)
- **Notifications** — Windows toast notifications (when the notification-listener capability is available) plus an in-island alert API for scripts and tools
- **Timers and stopwatch** — quick presets, custom minutes, multiple countdowns and a stopwatch
- **System stats** — CPU, RAM, battery and network throughput, sampled only while the Stats tab is open
- **Smooth expand/collapse animations** — GPU-composited WPF animations on the compact pill
- **Fullscreen-aware** — hides itself in games, videos and presentations and returns when you exit
- **Multi-monitor support** — pick the display, offset from the top and scale the island
- **Themes** — Dark, Light and Aurora, with adjustable opacity and scale
- **Tray icon and global hotkeys** — quick access even when the island is collapsed
- **Settings** — stored in `%APPDATA%\DynamicIsland\settings.json`

## Why it's lightweight

- Native stack, no Electron, no bundled browser engine
- Event-driven updates (GSMTC events, WinEvent foreground hooks, low-frequency safety checks)
- Idle CPU close to 0% — the stats sampler and the clock are the only background timers
- GPU-accelerated animations only while the island is active
- Small single-file, framework-dependent build (the .NET 8 desktop runtime is required)

## Installation

1. Download the latest release from the Releases page
2. Run `DynamicIsland.exe`
3. (Optional) Enable **Start with Windows** in the island settings or the tray menu

The app is unpackaged, so it does not need an installer. It stores its settings under `%APPDATA%\DynamicIsland`.

## Build from source

Requirements: Windows 10 2004+ / Windows 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/<your-username>/Dynamic_Island.git
cd Dynamic_Island
dotnet build -c Release
```

Run the debug build:

```powershell
dotnet run --project src/DynamicIsland -c Debug
```

Create a single-file release build:

```powershell
./build.ps1
# self-contained (larger, no runtime install required)
./build.ps1 -SelfContained
```

The output lands in `publish/`.

## Usage

| Action | How |
| --- | --- |
| Expand / collapse | Click the pill, press `Ctrl+Alt+D`, or use the tray menu |
| Play / pause | `Ctrl+Alt+M` |
| Start a 5-minute timer | `Ctrl+Alt+T` |
| Open settings | Tray menu → Settings, or the Settings tab |
| Dismiss a notification | Click it to open the island, or press the × button |

### Send a notification from a script

A second instance forwards `--notify` to the running app:

```powershell
DynamicIsland.exe --notify "Build finished"
DynamicIsland.exe --notify "CI|Build #42 is green"
```

You can also write one JSON object per line to the named pipe `DynamicIsland.Notify`:

```powershell
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'DynamicIsland.Notify', 'Out')
$pipe.Connect(1500)
$writer = New-Object System.IO.StreamWriter($pipe)
$writer.AutoFlush = $true
$writer.WriteLine('{"app":"CI","title":"Build","body":"Green"}')
$writer.Dispose()
```

> **Notification listener note:** `UserNotificationListener` requires an MSIX package identity and the `userNotificationListener` capability. The app detects this and keeps working without it; the CLI/pipe endpoint and timer alerts are always available.

## Project layout

```
src/DynamicIsland/
  App.xaml(.cs)            App startup, single instance, settings persistence
  MainWindow.xaml(.cs)     The island window, state machine and animations
  TrayService.cs           WinForms NotifyIcon and context menu
  Core/
    AppSettings.cs         Persisted preferences
    FullscreenWatcher.cs   WinEvent hook + safety check
    NativeMethods.cs       Win32 interop
    ThemeManager.cs        Runtime theme switching
  Services/
    MediaService.cs        GSMTC media sessions and artwork
    NotificationService.cs Listener + named-pipe alerts
    StatsService.cs        CPU/RAM/battery/network sampling
    TimerService.cs        Countdowns and stopwatch
  ViewModels/
    MainViewModel.cs       Bindable state and commands
  Themes/                  Dark / Light / Aurora + shared styles and icons
```

## Roadmap

- [x] Media controls
- [x] Notification support
- [x] Timer / stopwatch
- [x] System stats module
- [ ] Plugin support
- [ ] Theme editor
- [ ] Optional MSIX package for toast listening
- [ ] Album-art colour extraction

## Contributing

PRs and issues welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT — see [LICENSE](LICENSE).
