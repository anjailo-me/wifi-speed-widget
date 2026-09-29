using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace SpeedlineChecks;

public sealed class MockNdt7 : IDisposable
{
    private const string Subprotocol = "net.measurementlab.ndt.v7";
    private static readonly TimeSpan TestLength = TimeSpan.FromSeconds(10);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly int _port;
    private int _locateHits;
    private int _downloadHits;
    private int _uploadHits;

    public volatile string LocateMode = "ok";
    public volatile string TestMode = "ok";
    public double DownloadMbps = 240;
    public double UploadMbps;

    public int LocateHits => Volatile.Read(ref _locateHits);
    public int DownloadHits => Volatile.Read(ref _downloadHits);
    public int UploadHits => Volatile.Read(ref _uploadHits);

    public string LocateUrl => $"http://127.0.0.1:{_port}/v2/nearest/ndt/ndt7";

    public MockNdt7()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (path.StartsWith("/v2/nearest"))
            {
                Interlocked.Increment(ref _locateHits);
                await WriteLocate(ctx);
            }
            else if (path == "/ndt/v7/download" && ctx.Request.IsWebSocketRequest)
            {
                Interlocked.Increment(ref _downloadHits);
                var ws = (await ctx.AcceptWebSocketAsync(Subprotocol)).WebSocket;
                await ServeDownload(ws);
            }
            else if (path == "/ndt/v7/upload" && ctx.Request.IsWebSocketRequest)
            {
                Interlocked.Increment(ref _uploadHits);
                var ws = (await ctx.AcceptWebSocketAsync(Subprotocol)).WebSocket;
                await ServeUpload(ws);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private async Task WriteLocate(HttpListenerContext ctx)
    {
        string body;
        switch (LocateMode)
        {
            case "busy":
                ctx.Response.StatusCode = 429;
                body = "{\"error\":{\"type\":\"client\",\"title\":\"Too many periodic requests.\",\"status\":429}}";
                break;
            case "empty":
                ctx.Response.StatusCode = 200;
                body = "{\"results\":[]}";
                break;
            default:
                ctx.Response.StatusCode = 200;
                var baseUrl = $"ws://127.0.0.1:{_port}";
                body = "{\"results\":[{\"machine\":\"mock\",\"location\":{\"city\":\"Mock\",\"country\":\"XX\"},\"urls\":{"
                       + $"\"wss:///ndt/v7/download\":\"{baseUrl}/ndt/v7/download?access_token=t\","
                       + $"\"wss:///ndt/v7/upload\":\"{baseUrl}/ndt/v7/upload?access_token=t\"}}}}]}}";
                break;
        }
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private double CutoffSeconds() => TestMode switch
    {
        "drop3" => 3,
        "drop7" => 7,
        _ => TestLength.TotalSeconds
    };

    private static ArraySegment<byte> Text(string json) => new(Encoding.UTF8.GetBytes(json));

    private async Task ServeDownload(WebSocket ws)
    {
        var buffer = new byte[1 << 20];
        Random.Shared.NextBytes(buffer);
        var size = 1 << 13;
        long sent = 0;
        var clock = Stopwatch.StartNew();
        var lastMeasurement = 0.0;
        var cutoff = CutoffSeconds();
        try
        {
            while (clock.Elapsed.TotalSeconds < cutoff && ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(buffer, 0, size), WebSocketMessageType.Binary, true, _stop.Token);
                sent += size;
                if (size < buffer.Length && size < sent / 16) size = Math.Min(buffer.Length, size * 2);

                var ahead = sent * 8 / (DownloadMbps * 1_000_000) - clock.Elapsed.TotalSeconds;
                if (ahead > 0.002) await Task.Delay(TimeSpan.FromSeconds(Math.Min(ahead, 0.05)), _stop.Token);

                var now = clock.Elapsed.TotalSeconds;
                if (TestMode != "nomeasure" && now - lastMeasurement >= 0.25)
                {
                    lastMeasurement = now;
                    var micros = (long)(now * 1_000_000);
                    await ws.SendAsync(Text($"{{\"TCPInfo\":{{\"MinRTT\":43000,\"RTT\":52000,\"ElapsedTime\":{micros},\"BytesAcked\":{sent}}}}}"),
                        WebSocketMessageType.Text, true, _stop.Token);
                }
            }
            if (TestMode.StartsWith("drop"))
            {
                ws.Abort();
                return;
            }
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", _stop.Token);
        }
        catch
        {
            ws.Abort();
        }
    }

    private async Task ServeUpload(WebSocket ws)
    {
        var buffer = new byte[1 << 20];
        long received = 0;
        var clock = Stopwatch.StartNew();
        var lastMeasurement = 0.0;
        var cutoff = CutoffSeconds();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(cutoff + 0.5));
            while (clock.Elapsed.TotalSeconds < cutoff && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, limit.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                received += result.Count;
                if (UploadMbps > 0)
                {
                    var ahead = received * 8 / (UploadMbps * 1_000_000) - clock.Elapsed.TotalSeconds;
                    if (ahead > 0.002) await Task.Delay(TimeSpan.FromSeconds(Math.Min(ahead, 0.05)), limit.Token);
                }
                var now = clock.Elapsed.TotalSeconds;
                if (TestMode != "nomeasure" && now - lastMeasurement >= 0.25)
                {
                    lastMeasurement = now;
                    var micros = (long)(now * 1_000_000);
                    await ws.SendAsync(Text($"{{\"TCPInfo\":{{\"BytesReceived\":{received},\"ElapsedTime\":{micros}}}}}"),
                        WebSocketMessageType.Text, true, _stop.Token);
                }
            }
            if (TestMode.StartsWith("drop"))
            {
                ws.Abort();
                return;
            }
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", _stop.Token);
        }
        catch
        {
            ws.Abort();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
