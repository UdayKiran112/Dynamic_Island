# Contributing

Thanks for taking a look at Dynamic Island.

## Before you start

- Use Windows 10 2004+ or Windows 11.
- Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- Run `dotnet build -c Debug` and make sure the app starts before sending a PR.

## Pull requests

1. Fork the repository and create a topic branch.
2. Keep changes focused — one feature or fix per PR.
3. Match the existing style: nullable enabled, file-scoped namespaces, `_camelCase` fields, XAML resources for colours and styles.
4. Do not add heavyweight dependencies. The app intentionally uses only the .NET desktop stack and Win32 interop.
5. Test on a multi-monitor setup if you touch positioning, DPI or fullscreen detection.
6. Update the README if you add a user-facing feature.

## Architecture notes

- **Event-driven first.** Media uses GSMTC events; fullscreen detection uses a `SetWinEventHook` foreground hook with a slow safety check. Avoid adding polling loops.
- **UI thread.** Services marshal updates through the WPF `Dispatcher`. `MainViewModel` assumes events arrive on the UI thread.
- **Island states.** `MainWindow` animates between `Compact`, `Peek` and `Expanded`. New panels belong in the expanded card and should be bound through `MainViewModel`.
- **Settings.** Add a property to `AppSettings`; the store serializes it automatically. Handle layout/behaviour changes in `MainWindow.OnSettingsChanged` and app-level effects in `App.OnSettingsChanged`.

## Reporting bugs

Include:

- Windows version and display scaling
- Whether the app is running packaged or unpackaged
- Steps to reproduce
- Relevant output from `DebugView`/`dotnet run` if the app crashes

## Code of conduct

Be kind and constructive. Harassment or personal attacks are not welcome.
