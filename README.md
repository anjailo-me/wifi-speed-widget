# Wi-Fi Speed Widget

A small, draggable Windows desktop widget that continuously monitors and tests your connection.

- Automatic speed tests (continuous, or every 1 / 5 / 15 / 30 / 60 minutes)
- Live ping, jitter and packet loss with a connection quality rating
- Wi-Fi network name, band and signal strength
- History of recent test results
- Follows the Windows light/dark theme and accent color
- Remembers its position, optional always-on-top and start with Windows

## Build

Requires the .NET 10 SDK. Open `WifiSpeedWidget.slnx` in Visual Studio and press F5, or run:

```
dotnet run --project WifiSpeedWidget
```

Right-click the widget for options. Speed tests use Cloudflare's speed test endpoints.
