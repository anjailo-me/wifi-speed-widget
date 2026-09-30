using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WifiSpeedWidget;

namespace SpeedlineChecks;

public static class Harness
{
    private static readonly List<string> Failures = new();
    private static readonly List<string> Notes = new();
    private static int _passes;
    private static bool _mlabOk = true;
    private static MockNdt7? _mock;
    private const string RealLocate = "https://locate.measurementlab.net/v2/nearest/ndt/ndt7";

    private static void SetLocate(string url) =>
        typeof(SpeedTester).GetField("LocateBase", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, url);
    private static readonly List<string> Skipped = new();
    private static string _phase = "start";
    private static int _ignoreGaps;
    private static readonly List<string> BigGaps = new();
    private static string _out = "";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WifiSpeedWidget", "settings.json");
    private static readonly string LogPath = Path.Combine(Path.GetDirectoryName(SettingsPath)!, "errors.log");

    public static int Run(string outDir)
    {
        _out = outDir;
        Directory.CreateDirectory(outDir);
        var code = 1;
        var thread = new Thread(() =>
        {
            try { code = RunAll(); }
            catch (Exception ex) { Console.WriteLine("HARNESS CRASH: " + ex); code = 2; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return code;
    }

    private static int RunAll()
    {
        var original = File.Exists(SettingsPath) ? File.ReadAllBytes(SettingsPath) : null;
        var logBefore = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
        Console.WriteLine($"packaged (inherited identity): {InvokePlatform<bool>("get_IsPackaged")}");
        var only = Environment.GetEnvironmentVariable("CHECKS_ONLY") ?? "";
        try
        {
            if (only != "ui" && only != "shots")
            {
                Section("Settings");
                TestSettings();
                logBefore = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
                Section("Platform");
                TestPlatform();
                Section("Network monitor");
                TestNetwork().GetAwaiter().GetResult();
                Section("Ping monitor");
                TestPing().GetAwaiter().GetResult();
            }
            if (only != "shots")
            {
                _mlabOk = CheckMlab().GetAwaiter().GetResult();
                _mock = new MockNdt7();
                SetLocate(_mock.LocateUrl);
            }
            if (only == "")
            {
                Section("Speed tester (live M-Lab)");
                TestSpeed().GetAwaiter().GetResult();
            }
            if (only == "shots")
            {
                Section("Screenshots from the real window");
                TestShots();
            }
            else if (only != "engine")
            {
                Section("Widget window (real UI)");
                TestUi();
            }
        }
        finally
        {
            SetLocate(RealLocate);
            _mock?.Dispose();
            if (original != null) File.WriteAllBytes(SettingsPath, original);
            else if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
        }

        var logAfter = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
        Section("Error log");
        var newLog = logAfter > logBefore ? Tail(LogPath, (int)(logAfter - logBefore)) : "";
        var entries = System.Text.RegularExpressions.Regex.Split(newLog, "(?=[[][0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:)")
            .Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        var unexpected = entries.Where(e => !(e.Contains("429") || e.Contains("No test server was available"))).ToList();
        Check("the only errors logged are the busy and no-server replies the checks provoked on purpose",
            unexpected.Count == 0, string.Join(" | ", unexpected.Select(e => e.Split(Environment.NewLine.ToCharArray())[0].Trim())));
        Notes.Add($"errors.log gained {entries.Count} entries, all from deliberately provoked busy or no-server replies");

        Console.WriteLine();
        foreach (var n in Notes) Console.WriteLine("  NOTE  " + n);
        Console.WriteLine($"\nRESULT: {_passes} passed, {Failures.Count} failed, {Skipped.Count} skipped");
        foreach (var f in Failures) Console.WriteLine("  FAILED: " + f);
        foreach (var k in Skipped) Console.WriteLine("  SKIPPED: " + k);
        return Failures.Count == 0 ? 0 : 1;
    }

    private static string Tail(string path, int bytes)
    {
        var text = File.ReadAllText(path);
        return text.Substring(Math.Max(0, text.Length - bytes)).Trim();
    }

    private static void Skip(string name, string reason)
    {
        Skipped.Add($"{name} ({reason})");
        Console.WriteLine($"  SKIP  {name}  ({reason})");
    }

    private static async Task<bool> CheckMlab()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var r = await http.GetAsync("https://locate.measurementlab.net/v2/nearest/ndt/ndt7?client_name=speedline-checks&client_version=1");
            if (r.StatusCode == System.Net.HttpStatusCode.OK) return true;
            Notes.Add($"M-Lab answered HTTP {(int)r.StatusCode}: it is rate limiting this connection, so live speed checks were skipped");
            return false;
        }
        catch (Exception ex)
        {
            Notes.Add("M-Lab could not be reached (" + ex.GetType().Name + "): live speed checks were skipped");
            return false;
        }
    }

    private static void Section(string name) => Console.WriteLine($"\n== {name} ==");

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _passes++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            Failures.Add($"{name} {detail}".Trim());
            Console.WriteLine($"  FAIL  {name}  {detail}");
        }
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static T InvokePlatform<T>(string method, params object[] args)
    {
        var type = typeof(App).Assembly.GetType("WifiSpeedWidget.Platform")!;
        var m = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return (T)m.Invoke(null, args)!;
    }

    private static void TestSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var cases = new[] { (-1, 180), (1, 180), (0, 0), (5, 180), (15, 180), (30, 180), (60, 180), (180, 180), (360, 360), (720, 720), (400, 360), (999, 720), (-30, 180), (7, 180) };
        foreach (var (input, expected) in cases)
        {
            File.WriteAllText(SettingsPath, "{\"AutoTestMinutes\": " + input + "}");
            var s = WidgetSettings.Load();
            Check($"saved interval {input} becomes {expected}", s.AutoTestMinutes == expected, $"got {s.AutoTestMinutes}");
        }

        File.Delete(SettingsPath);
        var fresh = WidgetSettings.Load();
        Check("no settings file gives a 6 hour default", fresh.AutoTestMinutes == 360,
            $"got {fresh.AutoTestMinutes}; file still exists after delete: {File.Exists(SettingsPath)}");

        File.WriteAllText(SettingsPath, "{this is not json");
        var bak = SettingsPath + ".bak";
        if (File.Exists(bak)) File.Delete(bak);
        var recovered = WidgetSettings.Load();
        Check("corrupt settings file falls back to defaults", recovered.AutoTestMinutes == 360 && recovered.Topmost);
        Check("corrupt settings file is backed up", File.Exists(bak));

        var s2 = new WidgetSettings { AutoTestMinutes = 720, WindowX = -1200, WindowY = 200, Topmost = false };
        s2.History.Add(new TestRecord(DateTime.UtcNow, 20, 100, 50));
        s2.Save();
        var back = WidgetSettings.Load();
        Check("settings round trip keeps position, topmost, history and interval",
            back.AutoTestMinutes == 720 && back.WindowX == -1200 && back.WindowY == 200 && !back.Topmost && back.History.Count == 1);

        var now = DateTime.UtcNow;
        var counting = new WidgetSettings();
        counting.TestStarts.AddRange(new[] { now.AddHours(-1), now.AddHours(-23), now.AddHours(-25), now.AddHours(-48) });
        Check("only test starts from the last 24 hours are counted", counting.TestsInLastDay() == 2, $"{counting.TestsInLastDay()}");
        counting.RecordTestStart();
        Check("recording a test start adds one", counting.TestsInLastDay() == 3);
        var slot = counting.NextTestSlotAt();
        Check("the next free test slot is 24 hours after the oldest counted start",
            slot is { } sl && Math.Abs((sl - now.AddHours(-23).AddHours(24)).TotalMinutes) < 1, $"{slot}");
        counting.Save();
        var reloaded = WidgetSettings.Load();
        Check("test starts survive a save and reload", reloaded.TestsInLastDay() == 3, $"{reloaded.TestsInLastDay()}");
        Check("limits match Measurement Lab's rule of 40 a day", WidgetSettings.DailyTestLimit == 40 && WidgetSettings.AutoDailyLimit < 40);
    }

    private static void TestPlatform()
    {
        try
        {
            var metered = InvokePlatform<bool>("IsMeteredConnection");
            Check("metered connection check runs without error", true);
            Notes.Add($"metered connection right now: {metered}");
        }
        catch (Exception ex) { Check("metered connection check runs without error", false, ex.GetBaseException().Message); }
        Check("app name comes from the assembly", AppInfo.Name == "Speedline", AppInfo.Name);
    }

    private static async Task TestNetwork()
    {
        var monitor = new NetworkMonitor();
        monitor.Sample();
        await Task.Delay(1500);
        var (down, up) = monitor.Sample();
        Check("an active adapter is found", monitor.Adapter != null);
        Check("live rates are sane numbers", down >= 0 && up >= 0 && double.IsFinite(down) && double.IsFinite(up), $"{down} {up}");
        var wifi = await NetworkMonitor.GetWifiInfoAsync();
        Check("wifi details are read", wifi != null, "netsh returned nothing usable");
        if (wifi != null)
        {
            Check("network name is present", !string.IsNullOrWhiteSpace(wifi.Ssid), "ssid empty");
            Check("signal is 0-100", wifi.SignalPercent is >= 0 and <= 100, $"{wifi.SignalPercent}");
            Notes.Add($"wifi: signal {wifi.SignalPercent}% band {wifi.Band} link {wifi.LinkRate}");
        }
    }

    private static async Task TestPing()
    {
        var monitor = new PingMonitor();
        PingStats? last = null;
        for (var i = 0; i < 5; i++)
        {
            last = await monitor.ProbeAsync();
            await Task.Delay(200);
        }
        Check("ping gets a reply", last?.LatencyMs is > 0 and < 1000, $"{last?.LatencyMs}");
        Check("jitter is calculated after several probes", last?.JitterMs is >= 0, $"{last?.JitterMs}");
        Check("no packet loss on a healthy link", last?.LossPercent == 0, $"{last?.LossPercent}");
    }

    private static async Task<TestResult?> RunOneTest(string label, bool expectOk = true)
    {
        var events = new List<TestProgress>();
        var tester = new SpeedTester();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await tester.RunAsync(new SyncProgress<TestProgress>(events.Add), CancellationToken.None);
            sw.Stop();
            Check($"{label}: finishes", true);
            Check($"{label}: takes 15-40 seconds", sw.Elapsed.TotalSeconds is > 15 and < 40, $"{sw.Elapsed.TotalSeconds:F1}s");
            Check($"{label}: download is a sane number", result.DownloadMbps is > 0.5 and < 10000 && double.IsFinite(result.DownloadMbps), $"{result.DownloadMbps}");
            Check($"{label}: upload is a sane number", result.UploadMbps is > 0.5 and < 10000 && double.IsFinite(result.UploadMbps), $"{result.UploadMbps}");
            Check($"{label}: latency is reported", result.PingMs is > 0 and < 2000, $"{result.PingMs}");
            var phases = events.Select(e => e.Phase).Distinct().ToList();
            Check($"{label}: phases arrive in order Locate, Download, Upload",
                phases.SequenceEqual(new[] { TestPhase.Locate, TestPhase.Download, TestPhase.Upload }), string.Join(",", phases));
            foreach (var phase in phases)
            {
                var fr = events.Where(e => e.Phase == phase).Select(e => e.Fraction).ToList();
                var monotonic = fr.Zip(fr.Skip(1), (a, b) => b >= a).All(x => x);
                Check($"{label}: {phase} progress never goes backwards and stays within 0-1", monotonic && fr.All(f => f is >= 0 and <= 1));
            }
            Check($"{label}: no NaN or infinite live values", events.All(e => double.IsFinite(e.Value)));
            Check($"{label}: upload live value reaches the end", events.Any(e => e.Phase == TestPhase.Upload && e.Fraction > 0.9));
            Notes.Add($"{label}: {result.DownloadMbps:F0} down / {result.UploadMbps:F0} up / {result.PingMs:F0} ms in {sw.Elapsed.TotalSeconds:F1}s ({events.Count} progress events)");
            return result;
        }
        catch (Exception ex)
        {
            Check($"{label}: finishes", !expectOk, $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task TestSpeed()
    {
        await RunMockSpeedChecks();
        if (_mlabOk && Environment.GetEnvironmentVariable("CHECKS_LIVE") == "1")
        {
            SetLocate(RealLocate);
            await RunLiveSpeedChecks();
            SetLocate(_mock!.LocateUrl);
        }
        else
        {
            Skip("speed checks against the real Measurement Lab servers",
                _mlabOk ? "off by default to spare Measurement Lab's daily limit, set CHECKS_LIVE=1 to include them" : "M-Lab is refusing this connection right now");
        }
        await RunUnreachableServerChecks();
    }

    private static async Task<Exception?> RunExpectingFailure(SpeedTester tester)
    {
        try
        {
            await tester.RunAsync(new SyncProgress<TestProgress>(_ => { }), CancellationToken.None);
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    private static async Task RunMockSpeedChecks()
    {
        var mock = _mock!;
        mock.LocateMode = "ok";
        mock.TestMode = "ok";

        var full = await RunOneTest("full test (local stand-in server)");
        if (full != null)
        {
            Check("latency matches the server's own measurement (43 ms)", Math.Abs(full.PingMs - 43) < 1, $"{full.PingMs:F1}");
            Check("download rate matches what the server sent (240 Mbps, within 25%)", full.DownloadMbps is > 180 and < 300, $"{full.DownloadMbps:F0}");
            Check("upload uses the server's own byte count and gives a sane rate", full.UploadMbps > 50, $"{full.UploadMbps:F0}");
        }

        var cts = new CancellationTokenSource();
        var task = new SpeedTester().RunAsync(new SyncProgress<TestProgress>(_ => { }), cts.Token);
        await Task.Delay(5500);
        var cancelClock = Stopwatch.StartNew();
        cts.Cancel();
        var kind = "none";
        try { await task; }
        catch (OperationCanceledException) { kind = "cancelled"; }
        catch (Exception ex) { kind = ex.GetType().Name + ": " + ex.Message; }
        Check("cancelling mid-download stops with OperationCanceledException", kind == "cancelled", kind);
        Check("cancelling is quick (under 3 seconds)", cancelClock.Elapsed.TotalSeconds < 3, $"{cancelClock.Elapsed.TotalSeconds:F1}s");

        await RunOneTest("test straight after a cancel (local stand-in server)");

        mock.TestMode = "nomeasure";
        var bare = await new SpeedTester().RunAsync(new SyncProgress<TestProgress>(_ => { }), CancellationToken.None);
        Check("without server measurements the client counters still give sane rates", bare.DownloadMbps > 1 && bare.UploadMbps > 1, $"{bare.DownloadMbps:F0} / {bare.UploadMbps:F0}");
        Check("without server measurements the latency is reported as 0 (unknown)", bare.PingMs == 0, $"{bare.PingMs}");

        mock.TestMode = "drop7";
        var late = await RunExpectingFailure(new SpeedTester());
        Check("a server dropping the connection after 7 seconds still gives a result", late == null, late?.GetType().Name + ": " + late?.Message);

        mock.TestMode = "drop3";
        var early = await RunExpectingFailure(new SpeedTester());
        Check("a server dropping the connection after 3 seconds fails cleanly, not with a cancel",
            early is WebSocketException or System.IO.IOException or HttpRequestException or TimeoutException, early?.GetType().Name ?? "no error");

        mock.TestMode = "ok";
        mock.LocateMode = "busy";
        var busy = await RunExpectingFailure(new SpeedTester());
        Check("a busy reply from the lookup service surfaces as HTTP 429", busy is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }, busy?.GetType().Name ?? "no error");

        mock.LocateMode = "empty";
        var empty = await RunExpectingFailure(new SpeedTester());
        Check("a lookup with no servers fails cleanly", empty is HttpRequestException, empty?.GetType().Name ?? "no error");
        mock.LocateMode = "ok";
    }

    private static async Task RunLiveSpeedChecks()
    {
        await RunOneTest("full test");

        var cts = new CancellationTokenSource();
        var tester = new SpeedTester();
        var events = new List<TestProgress>();
        var task = tester.RunAsync(new SyncProgress<TestProgress>(events.Add), cts.Token);
        await Task.Delay(5500);
        var cancelledAt = Stopwatch.StartNew();
        cts.Cancel();
        var kind = "none";
        try { await task; }
        catch (OperationCanceledException) { kind = "cancelled"; }
        catch (Exception ex) { kind = ex.GetType().Name + ": " + ex.Message; }
        cancelledAt.Stop();
        Check("cancelling mid-download stops with OperationCanceledException", kind == "cancelled", kind);
        Check("cancelling is quick (under 3 seconds)", cancelledAt.Elapsed.TotalSeconds < 3, $"{cancelledAt.Elapsed.TotalSeconds:F1}s");

        await RunOneTest("test straight after a cancel");
    }

    private static async Task RunUnreachableServerChecks()
    {
        var download = typeof(SpeedTester).GetMethod("DownloadAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var bad in new[] { "wss://127.0.0.1:1/ndt/v7/download", "wss://does-not-exist.invalid/ndt/v7/download" })
        {
            var sw = Stopwatch.StartNew();
            var outcome = "no error";
            try
            {
                var t = (Task)download.Invoke(null, new object[] { new Uri(bad), new SyncProgress<TestProgress>(_ => { }), CancellationToken.None })!;
                await t;
            }
            catch (Exception ex) { outcome = ex.GetType().Name; }
            Check($"unreachable server {bad} fails cleanly, not with a cancel", outcome is "WebSocketException" or "HttpRequestException" or "TimeoutException", outcome);
            Check("unreachable server fails within 12 seconds", sw.Elapsed.TotalSeconds < 12, $"{sw.Elapsed.TotalSeconds:F1}s");
        }
    }

    private static T Find<T>(Window w, string name) where T : class =>
        w.FindName(name) as T ?? throw new InvalidOperationException("control not found: " + name);

    private static void Click(Button b) =>
        ((IInvokeProvider)new ButtonAutomationPeer(b).GetPattern(PatternInterface.Invoke)!).Invoke();

    private static void Click(MenuItem m) =>
        ((IInvokeProvider)new MenuItemAutomationPeer(m).GetPattern(PatternInterface.Invoke)!).Invoke();

    private static async Task<bool> WaitFor(Func<bool> condition, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private static void Shot(Window w, string name)
    {
        try
        {
            var fe = (FrameworkElement)w.Content;
            var dpi = VisualTreeHelper.GetDpi(w);
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(fe.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(fe.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bmp.Render(fe);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(_out, name + ".png"));
            enc.Save(fs);
        }
        catch (Exception ex) { Notes.Add("screenshot failed: " + ex.Message); }
    }

    private static void ShotAt(FrameworkElement fe, string name, double scale = 1.25)
    {
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(fe.ActualWidth * scale), (int)Math.Ceiling(fe.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(fe);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(_out, name + ".png"));
        enc.Save(fs);
        Notes.Add($"saved {name}.png ({bmp.PixelWidth}x{bmp.PixelHeight})");
    }

    private static void TestShots()
    {
        var live = CheckMlab().GetAwaiter().GetResult();
        if (!live)
        {
            _mock = new MockNdt7 { DownloadMbps = 186, UploadMbps = 94 };
            SetLocate(_mock.LocateUrl);
        }
        var seed = System.Text.Json.Nodes.JsonNode.Parse(File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : "{}")!.AsObject();
        seed["AutoTestMinutes"] = 360;
        seed["TestStarts"] = new System.Text.Json.Nodes.JsonArray();
        File.WriteAllText(SettingsPath, seed.ToJsonString());
        try { Application.ResourceAssembly = typeof(App).Assembly; } catch (InvalidOperationException) { }
        var app = new App();
        app.InitializeComponent();
        app.Startup += (_, _) => app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var w = (MainWindow)app.MainWindow!;
                await WaitFor(() => w.IsLoaded, 5);
                if (w.Content is Panel root && root.Background == null) root.Background = w.Background;
                var run = Find<Button>(w, "RunButton");
                var status = Find<TextBlock>(w, "StatusLine");
                await Task.Delay(6000);
                w.ContextMenu.PlacementTarget = w;
                w.ContextMenu.Placement = PlacementMode.Relative;
                w.ContextMenu.HorizontalOffset = 180;
                w.ContextMenu.VerticalOffset = 200;
                w.ContextMenu.IsOpen = true;
                await Task.Delay(1200);
                ShotAt(w.ContextMenu, "menu_main");
                w.ContextMenu.IsOpen = false;
                await Task.Delay(500);
                var scheduleMenu = Find<ContextMenu>(w, "ScheduleMenu");
                scheduleMenu.PlacementTarget = Find<Button>(w, "ScheduleButton");
                scheduleMenu.IsOpen = true;
                await Task.Delay(1200);
                ShotAt(scheduleMenu, "menu_schedule");
                scheduleMenu.IsOpen = false;
                await Task.Delay(500);
                Notes.Add(live ? "screenshots use the real Measurement Lab service" : "M-Lab is refusing this connection, so the screenshots use the local stand-in server (sample numbers)");
                Click(run);
                await WaitFor(() => status.Text.Contains("download"), 12);
                await Task.Delay(3500);
                ShotAt((FrameworkElement)VisualTreeHelper.GetChild(w, 0), "testing");
                Check("test finished for the idle screenshot", await WaitFor(() => run.Content?.ToString() == "Test now", 45));
                await Task.Delay(800);
                ShotAt((FrameworkElement)VisualTreeHelper.GetChild(w, 0), "idle");
                Notes.Add("status at idle screenshot: " + status.Text);
            }
            catch (Exception ex) { Check("screenshot scenario ran", false, ex.ToString()); }
            finally { app.Shutdown(); }
        }, DispatcherPriority.ApplicationIdle);
        app.Run();
    }

    private static JsonElement ReadSettings()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        return doc.RootElement.Clone();
    }

    private static void TestUi()
    {
        Environment.SetEnvironmentVariable("SPEEDLINE_INSTANCE_SUFFIX", ".checks");
        _mlabOk = true;
        File.WriteAllText(SettingsPath, "{\"AutoTestMinutes\": 360, \"Topmost\": true}");
        try { Application.ResourceAssembly = typeof(App).Assembly; } catch (InvalidOperationException) { }
        var app = new App();
        app.InitializeComponent();
        var unhandled = new List<string>();
        app.DispatcherUnhandledException += (_, e) =>
        {
            unhandled.Add(e.Exception.ToString());
            e.Handled = true;
        };
        app.Startup += (_, _) => app.Dispatcher.InvokeAsync(async () =>
        {
            try { await UiScenario(app, unhandled); }
            catch (Exception ex) { Check("UI scenario ran to the end", false, ex.ToString()); }
            finally { app.Shutdown(); }
        }, DispatcherPriority.ApplicationIdle);
        var exited = false;
        app.Exit += (_, _) => exited = true;
        app.Run();
        Check(_mlabOk ? "exiting from the tray menu quits the app, even during a test" : "exiting from the tray menu quits the app", exited);
        Check("no unhandled exceptions reached the UI thread", unhandled.Count == 0, string.Join(" | ", unhandled));
    }

    private static object GetField(MainWindow w, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;

    private static int HoursIn(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, "next in ([0-9]+):([0-9]{2}):");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    private static async Task UiScenario(App app, List<string> unhandled)
    {
        var w = (MainWindow)app.MainWindow!;
        Check("main window was created", app.MainWindow is MainWindow);
        await WaitFor(() => w.IsLoaded, 5);
        var run = Find<Button>(w, "RunButton");
        var hero = Find<Run>(w, "HeroValue");
        var upload = Find<Run>(w, "UploadValue");
        var status = Find<TextBlock>(w, "StatusLine");
        var nextText = Find<TextBlock>(w, "NextText");
        var scheduleText = Find<Run>(w, "ScheduleText");
        var network = Find<TextBlock>(w, "NetworkName");
        var ping = Find<Run>(w, "PingValue");
        var jitter = Find<Run>(w, "JitterValue");
        var progressTrack = Find<Border>(w, "ProgressTrack");
        var scheduleMenu = Find<ContextMenu>(w, "ScheduleMenu");
        var historyEmpty = Find<TextBlock>(w, "HistoryEmpty");
        var chart = Find<Canvas>(w, "HistoryChart");
        var settings = (WidgetSettings)GetField(w, "_settings");

        var gaps = new List<double>();
        var last = Stopwatch.StartNew();
        var watchdog = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        watchdog.Tick += (_, _) =>
        {
            var gap = last.Elapsed.TotalMilliseconds;
            last.Restart();
            if (_ignoreGaps > 0) { _ignoreGaps--; return; }
            gaps.Add(gap);
            if (gap > 300) BigGaps.Add($"{gap:F0} ms during '{_phase}'");
        };
        watchdog.Start();

        var privacyItem = Find<MenuItem>(w, "PrivacyItem");
        var menuHeaders = w.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
        Check("options menu lists the expected items", new[] { "Test now", "Keep on top", "Start with Windows", "Clear history", "Privacy policy", "About", "Close widget" }.All(menuHeaders.Contains), string.Join(",", menuHeaders));
        Check("privacy item is hidden when no policy address was built in", privacyItem.Visibility == Visibility.Collapsed);
        var firstDelay = (TimeSpan)typeof(MainWindow).GetMethod("FirstTestDelay", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null)!;
        Check("with no earlier test the first automatic test comes after 20 to 60 seconds", firstDelay.TotalSeconds is >= 20 and < 61, $"{firstDelay.TotalSeconds:F0}s");
        Check("window title is the app name", w.Title == "Speedline", w.Title);
        Check("window is always on top by default", w.Topmost);
        Check("first-run status says no tests yet", status.Text == "No tests yet", status.Text);
        Check("first-run download shows a dash", hero.Text == "—", hero.Text);
        Check("schedule shows every 6 hours", scheduleText.Text == "Every 6 hours", scheduleText.Text);
        Check("run button says Test now", run.Content?.ToString() == "Test now");
        Check("progress bar is hidden while idle", progressTrack.Opacity == 0);

        _phase = "startup and first idle";
        await Task.Delay(4000);
        Check("network name is shown", !string.IsNullOrWhiteSpace(network.Text) && network.Text != "Connecting…", network.Text);
        Check("live ping is numeric", int.TryParse(ping.Text, out var p) && p > 0, ping.Text);
        Check("jitter is numeric", int.TryParse(jitter.Text, out _), jitter.Text);
        Check("countdown to the first test is shown", nextText.Text.Contains("next in"), nextText.Text);
        Shot(w, "ui-1-idle");

        if (_mlabOk)
        {
            var seenStatus = new List<string>();
            var poller = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            poller.Tick += (_, _) => { if (seenStatus.Count == 0 || seenStatus[^1] != status.Text) seenStatus.Add(status.Text); };
            poller.Start();

            _phase = "first automatic test";
            Check("the first automatic test starts by itself within 75 seconds", await WaitFor(() => run.Content?.ToString() == "Stop", 75));
            Check("progress bar shows while testing", progressTrack.Opacity == 1);
            await WaitFor(() => status.Text.Contains("download"), 12);
            await Task.Delay(1500);
            Shot(w, "ui-2-downloading");
            Check("the run button finishes the test", await WaitFor(() => run.Content?.ToString() == "Test now", 45));
            poller.Stop();
            Check("status went through 'Finding a nearby test server…'", seenStatus.Any(t => t.StartsWith("Finding a nearby")), string.Join(" > ", seenStatus));
            Check("status went through 'Measuring download…'", seenStatus.Any(t => t.StartsWith("Measuring download")));
            Check("status went through 'Measuring upload…'", seenStatus.Any(t => t.StartsWith("Measuring upload")));
            Check("finished status says Tested just now", status.Text.StartsWith("Tested"), status.Text);
            Check("download result is a number", double.TryParse(hero.Text, out var dl) && dl > 0, hero.Text);
            Check("upload result is a number", double.TryParse(upload.Text, out var ul) && ul > 0, upload.Text);
            Check("progress bar is hidden again", progressTrack.Opacity == 0);
            Check("next automatic test is about 6 hours ahead (with random timing)", HoursIn(nextText.Text) is >= 4 and <= 7, nextText.Text);
            Check("history chart draws bars after a test", chart.Children.Count > 4, $"{chart.Children.Count} children");
            Check("history placeholder text is gone", historyEmpty.Visibility == Visibility.Collapsed);
            Check("result was saved to the settings file", ReadSettings().GetProperty("History").GetArrayLength() == 1);
            Check("the test start was counted for the daily limit", settings.TestsInLastDay() == 1, $"{settings.TestsInLastDay()}");
            var laterDelay = (TimeSpan)typeof(MainWindow).GetMethod("FirstTestDelay", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null)!;
            Check("after a recent test a relaunch would wait out the rest of the 6 hours", laterDelay.TotalHours is > 5.4 and <= 6, $"{laterDelay.TotalHours:F2} h");
            Shot(w, "ui-3-finished");
        }
        else
        {
            Skip("first automatic test and the full test flow in the window", "M-Lab is refusing this connection right now");
        }

        _phase = "schedule menu";
        var menuItems = scheduleMenu.Items.OfType<MenuItem>().ToList();
        Check("schedule menu has exactly 4 choices", menuItems.Count == 4, string.Join(",", menuItems.Select(m => m.Header)));
        Check("schedule menu has no choice more often than every 3 hours",
            menuItems.All(m => (string?)m.Tag is "0" or "180" or "360" or "720"), string.Join(",", menuItems.Select(m => m.Tag)));
        foreach (var (tag, text, saved, lo, hi) in new[] { ("180", "Every 3 hours", 180, 2, 3), ("720", "Every 12 hours", 720, 9, 15), ("0", "Auto-test off", 0, 0, 0), ("360", "Every 6 hours", 360, 4, 7) })
        {
            var item = menuItems.First(m => (string?)m.Tag == tag);
            Click(item);
            await Task.Delay(400);
            Check($"choosing '{item.Header}' updates the label to '{text}'", scheduleText.Text == text, scheduleText.Text);
            Check($"choosing '{item.Header}' is saved as {saved}", ReadSettings().GetProperty("AutoTestMinutes").GetInt32() == saved);
            if (saved == 0) Check("turning auto-test off clears the countdown", nextText.Text.Trim() == "" || !nextText.Text.Contains("next in"), nextText.Text);
            else Check($"'{item.Header}' starts a countdown within the random timing range", HoursIn(nextText.Text) >= lo && HoursIn(nextText.Text) <= hi, nextText.Text);
        }

        if (_mlabOk)
        {
            _phase = "manual test and stop";
            Click(run);
            Check("manual Test now starts a test", await WaitFor(() => run.Content?.ToString() == "Stop", 3));
            await Task.Delay(5500);
            var stopTimer = Stopwatch.StartNew();
            Click(run);
            Check("pressing Stop ends the test quickly", await WaitFor(() => run.Content?.ToString() == "Test now", 5), $"{stopTimer.Elapsed.TotalSeconds:F1}s");
            await Task.Delay(500);
            Check("stopping does not show an error", !status.Text.Contains("Couldn't") && !status.Text.Contains("busy"), status.Text);
            Check("stopping hides the progress bar", progressTrack.Opacity == 0);
            Check("stopping schedules the next test", nextText.Text.Contains("next in"), nextText.Text);
            Check("history is unchanged by a stopped test", ReadSettings().GetProperty("History").GetArrayLength() == 1);
        }
        else
        {
            Skip("manual Test now and Stop in the window", "M-Lab is refusing this connection right now");
        }

        _phase = "server busy handling";
        _mock!.LocateMode = "busy";
        Click(run);
        Check("with the server busy the test fails quickly and the button returns", await WaitFor(() => run.Content?.ToString() == "Test now" && status.Text.Contains("busy"), 8), status.Text);
        Check("the busy message tells the user what is happening", status.Text.Contains("Test server is busy"), status.Text);
        Check("busy tests back off for about 10 minutes", nextText.Text.Contains("next in 9:") || nextText.Text.Contains("next in 10:"), nextText.Text);
        Check("history is unchanged after a busy reply", ReadSettings().GetProperty("History").GetArrayLength() == 1);
        _mock.LocateMode = "empty";
        Click(run);
        Check("no available server shows a clear message", await WaitFor(() => status.Text.Contains("Couldn't reach the test server"), 8), status.Text);
        _mock.LocateMode = "ok";

        _phase = "clear history and toggles";
        if (settings.History.Count == 0) settings.History.Add(new TestRecord(DateTime.UtcNow, 20, 100, 50));
        settings.Save();
        var clear = w.ContextMenu.Items.OfType<MenuItem>().First(m => (string?)m.Header == "Clear history");
        Click(clear);
        await Task.Delay(400);
        Check("Clear history empties the saved history", ReadSettings().GetProperty("History").GetArrayLength() == 0);
        Check("Clear history shows the placeholder again", historyEmpty.Visibility == Visibility.Visible);

        var top = w.ContextMenu.Items.OfType<MenuItem>().First(m => (string?)m.Header == "Keep on top");
        top.IsChecked = w.Topmost;
        Click(top);
        await Task.Delay(300);
        Check("Keep on top can be switched off", !w.Topmost && !ReadSettings().GetProperty("Topmost").GetBoolean());
        top.IsChecked = w.Topmost;
        Click(top);
        await Task.Delay(300);
        Check("Keep on top can be switched back on", w.Topmost && ReadSettings().GetProperty("Topmost").GetBoolean());

        typeof(MainWindow).GetMethod("SavePlacement", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null);
        var saved2 = ReadSettings();
        Check("window position is saved", saved2.TryGetProperty("WindowX", out var wx) && wx.ValueKind == JsonValueKind.Number, saved2.ToString());

        _phase = "daily limit";
        settings.TestStarts.Clear();
        for (var i = 0; i < WidgetSettings.DailyTestLimit; i++) settings.RecordTestStart();
        settings.Save();
        Click(run);
        await Task.Delay(600);
        Check("a manual test is refused once 40 tests were used in a day", status.Text.Contains("Daily test limit reached"), status.Text);
        Check("the refused test does not start", run.Content?.ToString() == "Test now" && progressTrack.Opacity == 0);
        Check("the refused test is not counted", settings.TestsInLastDay() == WidgetSettings.DailyTestLimit, $"{settings.TestsInLastDay()}");

        settings.TestStarts.Clear();
        for (var i = 0; i < WidgetSettings.AutoDailyLimit; i++) settings.RecordTestStart();
        settings.Save();
        Click(menuItems.First(m => (string?)m.Tag == "360"));
        await Task.Delay(400);
        typeof(MainWindow).GetField("_nextTestAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, (DateTime?)DateTime.Now.AddSeconds(-1));
        await Task.Delay(2600);
        Check("an automatic test does not start once 30 tests were used in a day", run.Content?.ToString() == "Test now" && settings.TestsInLastDay() == WidgetSettings.AutoDailyLimit, $"{settings.TestsInLastDay()}");
        Check("the automatic test is postponed by about 30 minutes", nextText.Text.Contains("next in 29:") || nextText.Text.Contains("next in 30:"), nextText.Text);
        settings.TestStarts.Clear();
        settings.Save();

        if (_mlabOk)
        {
            _phase = "second full test";
            Click(run);
            await WaitFor(() => run.Content?.ToString() == "Stop", 3);
            Check("a second full test completes after all of that", await WaitFor(() => run.Content?.ToString() == "Test now", 45));
            Check("second test added exactly one history entry", ReadSettings().GetProperty("History").GetArrayLength() == 1);
            Check("second test shows no error", !status.Text.Contains("Couldn't") && !status.Text.Contains("busy") && !status.Text.Contains("limit"), status.Text);
        }
        else
        {
            Skip("a second full test after all of that", "M-Lab is refusing this connection right now");
        }

        Notes.Add("soak: watching memory for 150 seconds");
        var proc = Process.GetCurrentProcess();
        var samples = new List<(double Managed, double Private)>();
        int handles0 = 0, threads0 = 0;
        for (var n = 0; n < 6; n++)
        {
            _ignoreGaps = 3;
            var managed = GC.GetTotalMemory(true) / 1048576.0;
            proc.Refresh();
            samples.Add((managed, proc.PrivateMemorySize64 / 1048576.0));
            if (n == 0) { handles0 = proc.HandleCount; threads0 = proc.Threads.Count; }
            if (n < 5)
            {
                _phase = "idle soak";
                await Task.Delay(30000);
            }
        }
        proc.Refresh();
        var managedGrowth = samples[^1].Managed - samples[0].Managed;
        var privateGrowth = samples[^1].Private - samples[0].Private;
        Check("managed memory stays flat over 150 seconds idle (under 12 MB growth)", managedGrowth < 12, $"{samples[0].Managed:F0} -> {samples[^1].Managed:F0} MB");
        Check("private memory stays flat over 150 seconds idle (under 80 MB growth)", privateGrowth < 80, $"{samples[0].Private:F0} -> {samples[^1].Private:F0} MB");
        Check("handles stay flat over 150 seconds idle", proc.HandleCount - handles0 < 60, $"{handles0} -> {proc.HandleCount}");
        Check("threads stay flat over 150 seconds idle", proc.Threads.Count - threads0 < 10, $"{threads0} -> {proc.Threads.Count}");
        Notes.Add("soak managed heap MB every 30 s: " + string.Join(", ", samples.Select(x => x.Managed.ToString("F1"))));
        Notes.Add("soak private memory MB every 30 s: " + string.Join(", ", samples.Select(x => x.Private.ToString("F0"))));

        var sorted = gaps.OrderBy(x => x).ToList();
        var worst = sorted.Count > 0 ? sorted[^1] : 0;
        Check("the window never froze for more than 700 ms (worst gap between UI ticks)", worst < 700, $"{worst:F0} ms");
        foreach (var g in BigGaps) Notes.Add("UI pause over 300 ms: " + g);
        Notes.Add($"UI responsiveness: worst gap {worst:F0} ms over {gaps.Count} ticks, median {(sorted.Count > 0 ? sorted[sorted.Count / 2] : 0):F0} ms");
        watchdog.Stop();

        _phase = "closing";
        if (_mlabOk)
        {
            Click(run);
            await WaitFor(() => run.Content?.ToString() == "Stop", 3);
            await Task.Delay(3000);
        }
        var tray = ((App)app).Tray;
        Check("the tray icon is showing", tray is { Visible: true });
        w.Close();
        Check("closing the widget hides it instead of quitting", await WaitFor(() => !w.IsVisible, 3));
        Check("the tray icon stays after the widget is closed", ((App)app).Tray is { Visible: true });
        if (_mlabOk) Check("a test in progress keeps running while the widget is hidden", w.IsTesting);
        w.ShowWidget();
        Check("the tray icon brings the widget back", await WaitFor(() => w.IsVisible, 3));
        w.Close();
        await WaitFor(() => !w.IsVisible, 3);
        using (var signal = EventWaitHandle.OpenExisting(App.ShowSignalName)) signal.Set();
        Check("starting the app again brings the hidden widget back", await WaitFor(() => w.IsVisible, 5));
        ((App)app).ExitApp();
    }
}
