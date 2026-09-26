using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WifiSpeedWidget;

public record WifiInfo(string? Ssid, int? SignalPercent, string? LinkRate, string? Band);

public sealed class NetworkMonitor
{
    private NetworkInterface? _adapter;
    private long _lastRx;
    private long _lastTx;
    private DateTime _lastSample;
    private DateTime _lastAdapterScan = DateTime.MinValue;

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    static NetworkMonitor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public NetworkInterface? Adapter => _adapter;

    public (double DownMbps, double UpMbps) Sample()
    {
        var now = DateTime.UtcNow;
        if (_adapter == null || now - _lastAdapterScan > TimeSpan.FromSeconds(10))
        {
            var next = PickAdapter();
            _lastAdapterScan = now;
            if (next?.Id != _adapter?.Id)
            {
                _adapter = next;
                _lastSample = default;
            }
            else
            {
                _adapter = next;
            }
        }

        if (_adapter == null) return (0, 0);

        long rx, tx;
        try
        {
            var stats = _adapter.GetIPStatistics();
            rx = stats.BytesReceived;
            tx = stats.BytesSent;
        }
        catch
        {
            _adapter = null;
            return (0, 0);
        }

        double down = 0, up = 0;
        if (_lastSample != default)
        {
            var dt = (now - _lastSample).TotalSeconds;
            if (dt > 0)
            {
                down = Math.Max(0, rx - _lastRx) * 8 / dt / 1_000_000;
                up = Math.Max(0, tx - _lastTx) * 8 / dt / 1_000_000;
            }
        }
        _lastRx = rx;
        _lastTx = tx;
        _lastSample = now;
        return (down, up);
    }

    private static NetworkInterface? PickAdapter()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel
                        && HasGateway(n))
            .ToList();

        return candidates.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
               ?? candidates.FirstOrDefault();
    }

    private static bool HasGateway(NetworkInterface n)
    {
        try
        {
            return n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)
                                                                  && !g.Address.Equals(System.Net.IPAddress.IPv6Any));
        }
        catch
        {
            return false;
        }
    }

    public static async Task<WifiInfo?> GetWifiInfoAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.GetEncoding((int)GetOEMCP())
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            string? ssid = null;
            int? signal = null;
            string? rate = null;
            string? band = null;

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                var idx = line.IndexOf(':');
                if (idx < 0) continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();

                if (ssid == null && Regex.IsMatch(key, @"^SSID$", RegexOptions.IgnoreCase))
                    ssid = value;
                else if (signal == null && Regex.Match(value, @"^(\d{1,3})\s*%$") is { Success: true } m)
                    signal = int.Parse(m.Groups[1].Value);
                else if (rate == null && key.Contains("(Mbps)") && key.Contains("Receive", StringComparison.OrdinalIgnoreCase))
                    rate = value;
                else if (band == null && key.Equals("Band", StringComparison.OrdinalIgnoreCase))
                    band = value;
            }

            if (ssid == null && signal == null) return null;
            return new WifiInfo(ssid, signal, rate, band);
        }
        catch
        {
            return null;
        }
    }
}
