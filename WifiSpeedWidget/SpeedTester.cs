using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;

namespace WifiSpeedWidget;

public enum TestPhase { Locate, Download, Upload }

public record TestProgress(TestPhase Phase, double Value, double Fraction);

public record TestResult(double PingMs, double DownloadMbps, double UploadMbps);

public sealed class SpeedTester
{
    private const string Subprotocol = "net.measurementlab.ndt.v7";
    private const string ClientName = "speedline-widget";
    private static readonly TimeSpan TestLength = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HardLimit = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(10);
    private static readonly string ClientVersion =
        typeof(SpeedTester).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static readonly HttpClient Http = CreateHttpClient();

    private sealed record Target(Uri Download, Uri Upload);

    private sealed class ServerCounters
    {
        private readonly object _gate = new();
        private long _bytes;
        private double _seconds;

        public void Update(long bytes, double seconds)
        {
            if (bytes <= 0 || seconds <= 0) return;
            lock (_gate)
            {
                _bytes = bytes;
                _seconds = seconds;
            }
        }

        public bool TryGetMbps(out double mbps)
        {
            lock (_gate)
            {
                mbps = _seconds > 0 ? _bytes * 8 / _seconds / 1_000_000 : 0;
                return _seconds > 0;
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SpeedlineWidget/{ClientVersion}");
        return client;
    }

    public async Task<TestResult> RunAsync(IProgress<TestProgress> progress, CancellationToken ct)
    {
        progress.Report(new TestProgress(TestPhase.Locate, 0, 0));
        var targets = await LocateAsync(ct);
        progress.Report(new TestProgress(TestPhase.Locate, 0, 1));

        Exception? lastError = null;
        foreach (var target in targets)
        {
            try
            {
                var (downMbps, minRttMs) = await DownloadAsync(target.Download, progress, ct);
                var upMbps = await UploadAsync(target.Upload, progress, ct);
                return new TestResult(minRttMs, downMbps, upMbps);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or WebSocketException or IOException or TimeoutException
                                           && !ct.IsCancellationRequested)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new HttpRequestException("No test server was available");
    }

    private static async Task<List<Target>> LocateAsync(CancellationToken ct)
    {
        var url = $"https://locate.measurementlab.net/v2/nearest/ndt/ndt7?client_name={ClientName}&client_version={ClientVersion}";
        string body;
        try
        {
            using var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out finding a test server");
        }
        using var doc = JsonDocument.Parse(body);

        var targets = new List<Target>();
        if (doc.RootElement.TryGetProperty("results", out var results))
        {
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("urls", out var urls)) continue;
                var down = ReadUrl(urls, "wss:///ndt/v7/download");
                var up = ReadUrl(urls, "wss:///ndt/v7/upload");
                if (down != null && up != null) targets.Add(new Target(down, up));
            }
        }
        if (targets.Count == 0) throw new HttpRequestException("No test server was available");
        return targets;
    }

    private static Uri? ReadUrl(JsonElement urls, string key) =>
        urls.TryGetProperty(key, out var value) && value.GetString() is { } s ? new Uri(s) : null;

    private static async Task<ClientWebSocket> ConnectAsync(Uri url, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol(Subprotocol);
        ws.Options.SetBuffer(256 * 1024, 64 * 1024);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ConnectLimit);
        try
        {
            await ws.ConnectAsync(url, limit.Token);
            return ws;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new TimeoutException("Timed out connecting to the test server");
        }
        catch (WebSocketException ex)
        {
            var status = ws.HttpStatusCode;
            ws.Dispose();
            if (status == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("The test server is busy", ex, HttpStatusCode.TooManyRequests);
            throw;
        }
    }

    private static async Task<(double Mbps, double MinRttMs)> DownloadAsync(
        Uri url, IProgress<TestProgress> progress, CancellationToken ct)
    {
        using var ws = await ConnectAsync(url, ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(HardLimit);

        var buffer = new byte[256 * 1024];
        var text = new MemoryStream();
        long total = 0;
        double minRttMs = 0;
        double smoothed = 0, lastReport = 0, lastTime = 0;
        long lastBytes = 0;
        var clock = Stopwatch.StartNew();

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var message = await ws.ReceiveAsync(buffer, limit.Token);
                if (message.MessageType == WebSocketMessageType.Close) break;
                total += message.Count;

                if (message.MessageType == WebSocketMessageType.Text)
                {
                    text.Write(buffer, 0, message.Count);
                    if (message.EndOfMessage)
                    {
                        minRttMs = ReadMinRtt(text.ToArray(), minRttMs);
                        text.SetLength(0);
                    }
                }

                var now = clock.Elapsed.TotalSeconds;
                if (now - lastReport < 0.25) continue;
                var instant = (total - lastBytes) * 8 / (now - lastTime) / 1_000_000;
                smoothed = smoothed == 0 ? instant : smoothed * 0.6 + instant * 0.4;
                lastBytes = total;
                lastTime = now;
                lastReport = now;
                progress.Report(new TestProgress(TestPhase.Download, smoothed, Math.Min(1, now / TestLength.TotalSeconds)));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (clock.Elapsed.TotalSeconds < 6) throw new TimeoutException("The download test timed out");
        }
        catch (WebSocketException) when (clock.Elapsed.TotalSeconds > 6)
        {
        }

        var elapsed = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        await CloseQuietlyAsync(ws);
        if (total == 0) throw new HttpRequestException("The test server did not send any data");
        return (total * 8 / elapsed / 1_000_000, minRttMs);
    }

    private static async Task<double> UploadAsync(Uri url, IProgress<TestProgress> progress, CancellationToken ct)
    {
        using var ws = await ConnectAsync(url, ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(HardLimit);

        var counters = new ServerCounters();
        var receiver = Task.Run(() => ReceiveMeasurementsAsync(ws, counters, limit.Token));

        var payload = new byte[1 << 20];
        Random.Shared.NextBytes(payload);
        var size = 1 << 13;
        long sent = 0, lastSent = 0;
        double smoothed = 0, lastReport = 0, lastTime = 0;
        var clock = Stopwatch.StartNew();

        try
        {
            while (clock.Elapsed < TestLength && ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(payload, 0, size), WebSocketMessageType.Binary, true, limit.Token);
                sent += size;
                if (size < payload.Length && size < sent / 16) size = Math.Min(payload.Length, size * 2);

                var now = clock.Elapsed.TotalSeconds;
                if (now - lastReport < 0.25) continue;
                double shown;
                if (counters.TryGetMbps(out var serverMbps))
                {
                    shown = serverMbps;
                }
                else
                {
                    var instant = (sent - lastSent) * 8 / (now - lastTime) / 1_000_000;
                    smoothed = smoothed == 0 ? instant : smoothed * 0.6 + instant * 0.4;
                    shown = smoothed;
                }
                lastSent = sent;
                lastTime = now;
                lastReport = now;
                progress.Report(new TestProgress(TestPhase.Upload, shown, Math.Min(1, now / TestLength.TotalSeconds)));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (clock.Elapsed.TotalSeconds < 6) throw new TimeoutException("The upload test timed out");
        }
        catch (WebSocketException) when (clock.Elapsed.TotalSeconds > 6)
        {
        }

        var elapsed = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        await CloseQuietlyAsync(ws);
        await Task.WhenAny(receiver, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));

        if (counters.TryGetMbps(out var finalMbps)) return finalMbps;
        if (sent == 0) throw new HttpRequestException("The test server did not accept any data");
        return sent * 8 / elapsed / 1_000_000;
    }

    private static async Task ReceiveMeasurementsAsync(ClientWebSocket ws, ServerCounters counters, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var text = new MemoryStream();
        try
        {
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var message = await ws.ReceiveAsync(buffer, ct);
                if (message.MessageType == WebSocketMessageType.Close) break;
                if (message.MessageType != WebSocketMessageType.Text) continue;
                text.Write(buffer, 0, message.Count);
                if (!message.EndOfMessage) continue;
                ReadServerCounters(text.ToArray(), counters);
                text.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    private static double ReadMinRtt(byte[] json, double current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("TCPInfo", out var tcp)
                && tcp.TryGetProperty("MinRTT", out var value)
                && value.TryGetDouble(out var micros)
                && micros > 0)
                return micros / 1000.0;
        }
        catch (JsonException)
        {
        }
        return current;
    }

    private static void ReadServerCounters(byte[] json, ServerCounters counters)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (TryRead(root, "TCPInfo", "BytesReceived", "ElapsedTime", out var bytes, out var micros)
                || TryRead(root, "AppInfo", "NumBytes", "ElapsedTime", out bytes, out micros))
                counters.Update(bytes, micros / 1_000_000.0);
        }
        catch (JsonException)
        {
        }
    }

    private static bool TryRead(JsonElement root, string section, string bytesKey, string timeKey, out long bytes, out double micros)
    {
        bytes = 0;
        micros = 0;
        return root.TryGetProperty(section, out var info)
               && info.TryGetProperty(bytesKey, out var b) && b.TryGetInt64(out bytes)
               && info.TryGetProperty(timeKey, out var t) && t.TryGetDouble(out micros);
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket ws)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            ws.Abort();
        }
    }
}
