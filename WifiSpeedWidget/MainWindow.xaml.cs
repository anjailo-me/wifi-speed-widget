using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WifiSpeedWidget;

public partial class MainWindow : Window
{
    private const int HistoryMax = 40;
    private const int VisibleBars = 30;

    private readonly NetworkMonitor _monitor = new();
    private readonly SpeedTester _tester = new();
    private readonly PingMonitor _pingMonitor = new();
    private readonly WidgetSettings _settings = WidgetSettings.Load();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _wifiTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _pingTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _testCts;
    private DateTime? _nextTestAt;
    private PingStats? _lastPing;
    private string? _wifiDetail;
    private string? _failureText;
    private int _busyStrikes;
    private bool _pinging;

    public MainWindow()
    {
        InitializeComponent();

        Topmost = _settings.Topmost;
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Top + 16;
        ShowLastResult();
        UpdateScheduleText();

        _clock.Tick += (_, _) => OnClockTick();
        _wifiTimer.Tick += async (_, _) => await UpdateWifiAsync();
        _pingTimer.Tick += async (_, _) => await UpdatePingAsync();

        Loaded += async (_, _) =>
        {
            _monitor.Sample();
            _clock.Start();
            _wifiTimer.Start();
            _pingTimer.Start();
            DrawHistory();
            ScheduleNextTest(TimeSpan.FromSeconds(3));
            await UpdateWifiAsync();
            await UpdatePingAsync();
        };
        Closing += (_, _) =>
        {
            _testCts?.Cancel();
            SavePlacement();
        };
        Closed += (_, _) => Theme.Changed -= OnThemeChanged;
        Theme.Changed += OnThemeChanged;

        MainMenu.Opened += (_, _) =>
        {
            TopmostItem.IsChecked = Topmost;
            StartupItem.IsChecked = SafeStartupState();
            TestMenuItem.Header = _testCts != null ? "Stop test" : "Test now";
        };
        MainMenu.Closed += (_, _) =>
        {
            MainMenu.Placement = PlacementMode.MousePoint;
            MainMenu.PlacementTarget = null;
        };
        ScheduleMenu.Opened += (_, _) =>
        {
            foreach (var item in ScheduleMenu.Items.OfType<MenuItem>())
                item.IsChecked = item.Tag is string tag && int.Parse(tag) == _settings.AutoTestMinutes;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(hwnd);
        if (_settings.WindowX is { } x && _settings.WindowY is { } y) Native.TryMove(hwnd, x, y);
        ApplyFrame();
    }

    private void ApplyFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Native.ApplyFrame(hwnd, Theme.IsDark, Theme.BorderColor);
    }

    private void OnThemeChanged()
    {
        ApplyFrame();
        DrawHistory();
    }

    private void SavePlacement()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || Native.GetPosition(hwnd) is not { } p) return;
        _settings.WindowX = p.X;
        _settings.WindowY = p.Y;
        _settings.Save();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
        SavePlacement();
    }

    private async void OnClockTick()
    {
        var (down, up) = _monitor.Sample();
        LiveDown.Text = FormatRate(down);
        LiveUp.Text = FormatRate(up);

        if (_testCts == null)
        {
            UpdateStatusLine();
            if (_nextTestAt is { } due && DateTime.Now >= due)
            {
                await RunTestAsync();
                return;
            }
        }
        UpdateNextText();
    }

    private static string FormatRate(double mbps) =>
        mbps < 1 ? $"{mbps * 1000:F0} Kbps" : $"{FormatMbps(mbps)} Mbps";

    private static string FormatMbps(double mbps) =>
        mbps >= 100 ? mbps.ToString("F0") : mbps >= 10 ? mbps.ToString("F1") : mbps.ToString("F2");

    private static string Relative(DateTime utc)
    {
        var ago = DateTime.UtcNow - utc;
        if (ago.TotalSeconds < 10) return "just now";
        if (ago.TotalSeconds < 60) return $"{(int)ago.TotalSeconds} sec ago";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes} min ago";
        if (ago.TotalHours < 24) return $"{(int)ago.TotalHours} hr ago";
        return utc.ToLocalTime().ToString("MMM d, HH:mm");
    }

    private void UpdateStatusLine()
    {
        if (_failureText != null)
            StatusLine.Text = _failureText;
        else if (_settings.LastRun is { } last)
            StatusLine.Text = $"Tested {Relative(last)}";
        else
            StatusLine.Text = "No tests yet";
    }

    private void UpdateNextText()
    {
        if (_testCts != null)
        {
            NextText.Text = "  ·  testing";
            return;
        }
        if (_nextTestAt is not { } due || _settings.AutoTestMinutes == 0)
        {
            NextText.Text = "";
            return;
        }
        var left = due - DateTime.Now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        NextText.Text = left.TotalHours >= 1 ? $"  ·  next in {left:h\\:mm\\:ss}" : $"  ·  next in {left:m\\:ss}";
    }

    private void UpdateScheduleText()
    {
        ScheduleText.Text = _settings.AutoTestMinutes switch
        {
            < 0 => "Continuous",
            0 => "Auto-test off",
            1 => "Every minute",
            60 => "Every hour",
            var m => $"Every {m} min"
        };
    }

    private void ShowLastResult()
    {
        HeroValue.Text = _settings.LastDown is { } d ? FormatMbps(d) : "—";
        UploadValue.Text = _settings.LastUp is { } u ? FormatMbps(u) : "—";
        UpdateStatusLine();
    }

    private async void OnRunTest(object sender, RoutedEventArgs e)
    {
        if (_testCts != null)
        {
            _testCts.Cancel();
            return;
        }
        await RunTestAsync();
    }

    private async Task RunTestAsync()
    {
        if (_testCts != null) return;
        _nextTestAt = null;
        _testCts = new CancellationTokenSource();
        SetTesting(true);

        var cancelled = false;
        var failed = false;
        try
        {
            var result = await _tester.RunAsync(new Progress<TestProgress>(OnTestProgress), _testCts.Token);
            _failureText = null;
            _busyStrikes = 0;
            StatusLine.ToolTip = null;
            _settings.LastPing = result.PingMs;
            _settings.LastDown = result.DownloadMbps;
            _settings.LastUp = result.UploadMbps;
            _settings.LastRun = DateTime.UtcNow;
            _settings.History.Add(new TestRecord(DateTime.UtcNow, result.PingMs, result.DownloadMbps, result.UploadMbps));
            while (_settings.History.Count > HistoryMax) _settings.History.RemoveAt(0);
            _settings.Save();
            DrawHistory();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            failed = true;
            _busyStrikes++;
            _failureText = "Test server is busy, waiting before the next test";
            StatusLine.ToolTip = "The speed test server asked for fewer requests. The widget backs off automatically.";
            WidgetSettings.LogError(ex);
        }
        catch (Exception ex)
        {
            failed = true;
            _failureText = "Couldn't reach the test server";
            StatusLine.ToolTip = ex.Message;
            WidgetSettings.LogError(ex);
        }
        finally
        {
            _testCts.Dispose();
            _testCts = null;
            SetTesting(false);
            ShowLastResult();
            ScheduleAfterTest(failed, cancelled);
        }
    }

    private void SetTesting(bool on)
    {
        ProgressTrack.Opacity = on ? 1 : 0;
        ProgressFill.Width = 0;
        RunButton.Content = on ? "Stop" : "Test now";
        RunButton.Style = (Style)FindResource(on ? "StandardButton" : "AccentButton");
        HeroValue.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
        UploadValue.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
        if (on) StatusLine.Text = "Starting test…";
        UpdateNextText();
    }

    private void OnTestProgress(TestProgress p)
    {
        if (_testCts == null) return;
        switch (p.Phase)
        {
            case TestPhase.Ping:
                StatusLine.Text = "Measuring latency…";
                SetProgress(p.Fraction * 0.1);
                break;
            case TestPhase.Download:
                StatusLine.Text = "Measuring download…";
                if (p.Value > 0)
                {
                    HeroValue.Text = FormatMbps(p.Value);
                    HeroValue.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                }
                SetProgress(0.1 + p.Fraction * 0.45);
                break;
            case TestPhase.Upload:
                StatusLine.Text = "Measuring upload…";
                HeroValue.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
                if (p.Value > 0)
                {
                    UploadValue.Text = FormatMbps(p.Value);
                    UploadValue.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                }
                SetProgress(0.55 + p.Fraction * 0.45);
                break;
        }
    }

    private void SetProgress(double fraction) =>
        ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * Math.Clamp(fraction, 0, 1));

    private void ScheduleAfterTest(bool failed, bool cancelled)
    {
        var minutes = _settings.AutoTestMinutes;
        if (minutes == 0)
        {
            _nextTestAt = null;
            UpdateNextText();
            return;
        }
        TimeSpan delay;
        if (minutes < 0)
            delay = TimeSpan.FromSeconds(cancelled ? 60 : failed ? 30 : 5);
        else if (failed)
            delay = TimeSpan.FromSeconds(Math.Min(60, minutes * 60));
        else
            delay = TimeSpan.FromMinutes(minutes);
        if (_busyStrikes > 0)
        {
            var backoff = TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, _busyStrikes)));
            if (backoff > delay) delay = backoff;
        }
        ScheduleNextTest(delay);
    }

    private void ScheduleNextTest(TimeSpan delay)
    {
        _nextTestAt = _settings.AutoTestMinutes == 0 ? null : DateTime.Now + delay;
        UpdateNextText();
    }

    private void OnScheduleClick(object sender, RoutedEventArgs e)
    {
        ScheduleMenu.PlacementTarget = ScheduleButton;
        ScheduleMenu.IsOpen = true;
    }

    private void OnAutoInterval(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        _settings.AutoTestMinutes = int.Parse(tag);
        _settings.Save();
        UpdateScheduleText();
        if (_testCts != null) return;
        var minutes = _settings.AutoTestMinutes;
        ScheduleNextTest(minutes < 0 ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(Math.Max(minutes, 0)));
    }

    private async Task UpdatePingAsync()
    {
        if (_pinging || _testCts != null) return;
        _pinging = true;
        try
        {
            _lastPing = await _pingMonitor.ProbeAsync();
            PingValue.Text = _lastPing.LatencyMs is { } l ? l.ToString("F0") : "—";
            JitterValue.Text = _lastPing.JitterMs is { } j ? j.ToString("F0") : "—";
            UpdateConnectionStatus();
        }
        finally
        {
            _pinging = false;
        }
    }

    private void UpdateConnectionStatus()
    {
        string verdict;
        string brush;
        var p = _lastPing;
        if (_monitor.Adapter == null || p is { LatencyMs: null, LossPercent: >= 50 })
        {
            verdict = "Offline";
            brush = "Critical";
        }
        else if (p == null)
        {
            verdict = "Checking";
            brush = "TextTertiary";
        }
        else if (p.LossPercent >= 5 || p.LatencyMs > 150)
        {
            verdict = "Poor";
            brush = "Critical";
        }
        else if (p.LossPercent > 0 || p.LatencyMs > 80 || p.JitterMs > 30)
        {
            verdict = "Fair";
            brush = "Caution";
        }
        else if (p.LatencyMs > 40 || p.JitterMs > 10)
        {
            verdict = "Good";
            brush = "Good";
        }
        else
        {
            verdict = "Excellent";
            brush = "Good";
        }

        StatusDot.SetResourceReference(Shape.FillProperty, brush);
        NetworkDetail.Text = string.IsNullOrEmpty(_wifiDetail) ? verdict : $"{verdict}  ·  {_wifiDetail}";
        StatusRow.ToolTip = p == null
            ? "Checking connection quality"
            : $"Ping {(p.LatencyMs is { } l ? $"{l:F0} ms" : "timed out")}  ·  Jitter {(p.JitterMs is { } j ? $"{j:F0} ms" : "—")}  ·  Loss {p.LossPercent:F0}%";
    }

    private async Task UpdateWifiAsync()
    {
        var adapter = _monitor.Adapter;
        var isWireless = adapter?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        var wifi = adapter == null || isWireless ? await NetworkMonitor.GetWifiInfoAsync() : null;

        if (wifi != null)
        {
            NetworkName.Text = string.IsNullOrWhiteSpace(wifi.Ssid) ? "Wi-Fi" : wifi.Ssid;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(wifi.Band)) parts.Add(wifi.Band!);
            if (wifi.SignalPercent is { } s) parts.Add($"{s}% signal");
            _wifiDetail = string.Join("  ·  ", parts);
            NetworkName.ToolTip = string.IsNullOrWhiteSpace(wifi.LinkRate) ? null : $"Link rate {wifi.LinkRate} Mbps";
            SetSignal(wifi.SignalPercent, false, false);
        }
        else if (adapter != null)
        {
            NetworkName.Text = isWireless ? "Wi-Fi" : "Ethernet";
            _wifiDetail = adapter.Speed > 0 ? $"{adapter.Speed / 1_000_000} Mbps link" : adapter.Description;
            NetworkName.ToolTip = adapter.Name;
            SetSignal(null, !isWireless, false);
        }
        else
        {
            NetworkName.Text = "Not connected";
            _wifiDetail = null;
            NetworkName.ToolTip = null;
            SetSignal(0, false, true);
        }
        UpdateConnectionStatus();
    }

    private void SetSignal(int? percent, bool wired, bool offline)
    {
        WifiIcon.Visibility = wired ? Visibility.Collapsed : Visibility.Visible;
        EthernetIcon.Visibility = wired ? Visibility.Visible : Visibility.Collapsed;
        var level = offline ? 0 : percent switch
        {
            null => 3,
            >= 75 => 3,
            >= 45 => 2,
            > 0 => 1,
            _ => 0
        };
        Arc1.SetResourceReference(Shape.StrokeProperty, level >= 1 ? "TextPrimary" : "SignalOff");
        Arc2.SetResourceReference(Shape.StrokeProperty, level >= 2 ? "TextPrimary" : "SignalOff");
        Arc3.SetResourceReference(Shape.StrokeProperty, level >= 3 ? "TextPrimary" : "SignalOff");
        Dot.SetResourceReference(Shape.FillProperty, offline ? "SignalOff" : "TextPrimary");
    }

    private void OnHistorySizeChanged(object sender, SizeChangedEventArgs e) => DrawHistory();

    private void DrawHistory()
    {
        HistoryChart.Children.Clear();
        var items = _settings.History.TakeLast(VisibleBars).ToList();
        HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var w = HistoryChart.ActualWidth;
        var h = HistoryChart.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var baseline = new Rectangle { Width = w, Height = 1, IsHitTestVisible = false };
        baseline.SetResourceReference(Shape.FillProperty, "Divider");
        Canvas.SetTop(baseline, h - 1);
        HistoryChart.Children.Add(baseline);

        var mid = new Line { X1 = 0, X2 = w, Y1 = Math.Round(h / 2) + 0.5, Y2 = Math.Round(h / 2) + 0.5, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 3 }, IsHitTestVisible = false };
        mid.SetResourceReference(Shape.StrokeProperty, "Divider");
        HistoryChart.Children.Add(mid);

        if (items.Count == 0) return;

        var max = NiceCeiling(items.Max(r => Math.Max(r.DownloadMbps, r.UploadMbps)));
        var slot = w / VisibleBars;
        var bar = Math.Max(1.5, Math.Floor((slot - 3) / 2));
        var usable = h - 4;

        for (var i = 0; i < items.Count; i++)
        {
            var r = items[i];
            var x = w - (items.Count - i) * slot;

            var hit = new Rectangle { Width = slot, Height = h + 4, Fill = Brushes.Transparent, RadiusX = 3, RadiusY = 3 };
            hit.ToolTip = $"{r.Time.ToLocalTime():ddd HH:mm}\nDownload  {FormatMbps(r.DownloadMbps)} Mbps\nUpload  {FormatMbps(r.UploadMbps)} Mbps\nLatency  {r.PingMs:F0} ms";
            ToolTipService.SetInitialShowDelay(hit, 150);
            hit.MouseEnter += (_, _) => hit.SetResourceReference(Shape.FillProperty, "SubtleHover");
            hit.MouseLeave += (_, _) => hit.Fill = Brushes.Transparent;
            Canvas.SetLeft(hit, x);
            Canvas.SetTop(hit, -2);
            HistoryChart.Children.Add(hit);

            var left = x + (slot - (bar * 2 + 1)) / 2;
            AddBar(left, bar, Math.Max(2, r.DownloadMbps / max * usable), h, "Accent");
            AddBar(left + bar + 1, bar, Math.Max(2, r.UploadMbps / max * usable), h, "ChartSecondary");
        }
    }

    private void AddBar(double x, double width, double height, double chartHeight, string brushKey)
    {
        var rect = new Rectangle { Width = width, Height = height, RadiusX = 1, RadiusY = 1, IsHitTestVisible = false };
        rect.SetResourceReference(Shape.FillProperty, brushKey);
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, chartHeight - 1 - height);
        HistoryChart.Children.Add(rect);
    }

    private static double NiceCeiling(double v)
    {
        if (v <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
            if (step * magnitude >= v) return step * magnitude;
        return 10 * magnitude;
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        MainMenu.PlacementTarget = MoreButton;
        MainMenu.Placement = PlacementMode.Bottom;
        MainMenu.IsOpen = true;
    }

    private void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostItem.IsChecked;
        _settings.Topmost = Topmost;
        _settings.Save();
    }

    private void OnToggleStartup(object sender, RoutedEventArgs e)
    {
        try
        {
            WidgetSettings.StartsWithWindows = StartupItem.IsChecked;
        }
        catch
        {
            StartupItem.IsChecked = SafeStartupState();
        }
    }

    private static bool SafeStartupState()
    {
        try { return WidgetSettings.StartsWithWindows; } catch { return false; }
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        _settings.History.Clear();
        _settings.Save();
        DrawHistory();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
