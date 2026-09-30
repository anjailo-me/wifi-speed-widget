# Automated checks

These checks run the real Speedline code and drive the real widget window, so they catch problems a compile cannot. They cover settings and the daily test limit, the network, Wi-Fi and ping monitors, the speed tester, cancelling, servers that fail in different ways, every menu and schedule choice in the window, the system tray icon (hiding, bringing the widget back, its tooltip and the one-time notice), a Wi-Fi name that Windows hides, and a 2.5-minute soak that watches memory, handles, threads and window freezes. A full run takes about nine minutes.

## Run them

Close the widget first, then from the repository root:

```
dotnet build WifiSpeedWidget\WifiSpeedWidget.csproj -c Release
dotnet build tests\SpeedlineChecks\SpeedlineChecks.csproj -c Release
dotnet fsi --exec tests\run-checks.fsx
```

The window appears on screen for several minutes while it is driven, so leave it alone. The script prints a PASS, FAIL or SKIP line for every check and ends with a total. The exit code is 0 when nothing failed.

To run part of it, set `CHECKS_ONLY` before the last command:

| Value | What runs |
| --- | --- |
| `engine` | Settings, monitors, and the speed tester checks, about three minutes |
| `ui` | Only the widget window walkthrough |
| `shots` | Saves screenshots of the widget and its menus into `tests\out` |

## How the speed tests are checked

Measurement Lab (M-Lab) allows only 40 tests a day from one connection, so the checks do not use it by default. They start a small stand-in server on your own PC that speaks the same protocol, recorded from the real service (`MockNdt7.cs`). It can also misbehave on purpose: answer "busy", return no servers, send no measurements, or drop the connection after 3 or 7 seconds. A normal run therefore sends nothing to M-Lab.

To also test the real service, set `CHECKS_LIVE=1`. That runs two real tests and uses two of the 40 daily tests. It is skipped automatically if M-Lab is refusing your connection. The `shots` mode uses the real service when M-Lab accepts your connection and the stand-in server when it does not, so screenshots made then show sample numbers.

## Good to know

- While running, the checks replace your saved widget settings with test values and put the originals back at the end.
- The launcher loads WPF in its own load context, because the .NET install ships a stub `WindowsBase` that clashes with the real one. It loads the built files from memory, so it also works on a PC where unsigned programs cannot be started from disk.
- The window checks include the real timers, so the first automatic test starts on its own between 20 and 60 seconds after launch.
- Not covered here: anything that only exists once the app is installed from the Store (the startup task, the location permission), and behavior with no network at all.
