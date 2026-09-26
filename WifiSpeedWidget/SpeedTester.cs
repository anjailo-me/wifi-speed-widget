using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace WifiSpeedWidget;

public enum TestPhase { Ping, Download, Upload }

public record TestProgress(TestPhase Phase, double Value, double Fraction);

public record TestResult(double PingMs, double DownloadMbps, double UploadMbps);

public sealed class SpeedTester
{
    private const string DownUrl = "https://speed.cloudflare.com/__down?bytes=";
    private const string UpUrl = "https://speed.cloudflare.com/__up";
    private static readonly TimeSpan PhaseDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(1.5);
    private const int Streams = 4;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 16,
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async Task<TestResult> RunAsync(IProgress<TestProgress> progress, CancellationToken ct)
    {
        var ping = await MeasurePingAsync(progress, ct);
        var down = await MeasureThroughputAsync(TestPhase.Download, DownloadWorker, progress, ct);
        var up = await MeasureThroughputAsync(TestPhase.Upload, UploadWorker, progress, ct);
        return new TestResult(ping, down, up);
    }

    private static async Task<double> MeasurePingAsync(IProgress<TestProgress> progress, CancellationToken ct)
    {
        const int rounds = 8;
        var samples = new List<double>();
        for (var i = 0; i < rounds; i++)
        {
            var sw = Stopwatch.StartNew();
            using (var resp = await Http.GetAsync(DownUrl + "0", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
            }
            sw.Stop();
            if (i > 0) samples.Add(sw.Elapsed.TotalMilliseconds);
            var current = samples.Count > 0 ? samples.Min() : 0;
            progress.Report(new TestProgress(TestPhase.Ping, current, (i + 1.0) / rounds));
        }
        return samples.Min();
    }

    private static async Task<double> MeasureThroughputAsync(
        TestPhase phase,
        Func<Counter, CancellationToken, Task> worker,
        IProgress<TestProgress> progress,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PhaseDuration);
        var counter = new Counter();
        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, Streams).Select(_ => Task.Run(() => worker(counter, cts.Token))).ToArray();
        var all = Task.WhenAll(workers);

        long warmBytes = -1;
        double warmTime = 0;
        long lastBytes = 0;
        double lastTime = 0;
        double smoothed = 0;

        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(200, CancellationToken.None));
            var t = sw.Elapsed.TotalSeconds;
            var bytes = counter.Value;
            if (warmBytes < 0 && sw.Elapsed >= Warmup)
            {
                warmBytes = bytes;
                warmTime = t;
            }
            var dt = t - lastTime;
            if (dt > 0)
            {
                var instant = (bytes - lastBytes) * 8 / dt / 1_000_000;
                smoothed = smoothed == 0 ? instant : smoothed * 0.7 + instant * 0.3;
            }
            lastBytes = bytes;
            lastTime = t;
            progress.Report(new TestProgress(phase, smoothed, Math.Min(1, t / PhaseDuration.TotalSeconds)));
        }

        try { await all; } catch (OperationCanceledException) { }
        ct.ThrowIfCancellationRequested();

        var total = sw.Elapsed.TotalSeconds;
        var finalBytes = counter.Value;
        if (warmBytes < 0 || total - warmTime < 0.5)
            return finalBytes * 8 / Math.Max(total, 0.001) / 1_000_000;
        return (finalBytes - warmBytes) * 8 / (total - warmTime) / 1_000_000;
    }

    private static async Task DownloadWorker(Counter counter, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var resp = await Http.GetAsync(DownUrl + "50000000", HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                    counter.Add(read);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
        catch (HttpRequestException) when (ct.IsCancellationRequested) { }
    }

    private static async Task UploadWorker(Counter counter, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var content = new CountingContent(8 * 1024 * 1024, counter);
                using var resp = await Http.PostAsync(UpUrl, content, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
        catch (HttpRequestException) when (ct.IsCancellationRequested) { }
    }

    public sealed class Counter
    {
        private long _value;
        public long Value => Interlocked.Read(ref _value);
        public void Add(long n) => Interlocked.Add(ref _value, n);
    }

    private sealed class CountingContent : HttpContent
    {
        private static readonly byte[] Chunk = CreateChunk();
        private readonly long _length;
        private readonly Counter _counter;

        public CountingContent(long length, Counter counter)
        {
            _length = length;
            _counter = counter;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        private static byte[] CreateChunk()
        {
            var data = new byte[64 * 1024];
            Random.Shared.NextBytes(data);
            return data;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            long sent = 0;
            while (sent < _length)
            {
                var n = (int)Math.Min(Chunk.Length, _length - sent);
                await stream.WriteAsync(Chunk.AsMemory(0, n), ct);
                sent += n;
                _counter.Add(n);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
