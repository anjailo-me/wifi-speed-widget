using System.Net.NetworkInformation;

namespace WifiSpeedWidget;

public record PingStats(double? LatencyMs, double? JitterMs, double LossPercent);

public sealed class PingMonitor
{
    private const int Window = 20;
    private readonly Queue<long?> _samples = new();
    private readonly string[] _hosts = { "1.1.1.1", "8.8.8.8" };

    public async Task<PingStats> ProbeAsync()
    {
        long? rtt = null;
        foreach (var host in _hosts)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 1500);
                if (reply.Status == IPStatus.Success)
                {
                    rtt = reply.RoundtripTime;
                    break;
                }
            }
            catch (PingException)
            {
            }
        }

        _samples.Enqueue(rtt);
        while (_samples.Count > Window) _samples.Dequeue();

        var ok = _samples.Where(s => s.HasValue).Select(s => (double)s!.Value).ToList();
        var loss = 100.0 * (_samples.Count - ok.Count) / _samples.Count;
        double? jitter = null;
        if (ok.Count >= 2)
            jitter = ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average();

        return new PingStats(rtt, jitter, loss);
    }
}
