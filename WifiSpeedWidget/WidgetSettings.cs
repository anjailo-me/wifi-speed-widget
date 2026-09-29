using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WifiSpeedWidget;

public record TestRecord(DateTime Time, double PingMs, double DownloadMbps, double UploadMbps);

public sealed class WidgetSettings
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WifiSpeedWidget");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "WifiSpeedWidget";

    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool Topmost { get; set; } = true;
    public double? LastPing { get; set; }
    public double? LastDown { get; set; }
    public double? LastUp { get; set; }
    public DateTime? LastRun { get; set; }
    public int AutoTestMinutes { get; set; } = 360;
    public List<TestRecord> History { get; set; } = new();
    public List<DateTime> TestStarts { get; set; } = new();

    public const int DailyTestLimit = 40;
    public const int AutoDailyLimit = 30;

    private static readonly int[] AllowedIntervals = { 0, 180, 360, 720 };

    private static int NormalizeInterval(int minutes)
    {
        if (Array.IndexOf(AllowedIntervals, minutes) >= 0) return minutes;
        if (minutes < 0) return 180;
        return AllowedIntervals.Where(a => a > 0)
            .OrderBy(a => Math.Abs(a - minutes))
            .ThenByDescending(a => a)
            .First();
    }

    public int TestsInLastDay()
    {
        PruneStarts();
        return TestStarts.Count;
    }

    public DateTime? NextTestSlotAt()
    {
        PruneStarts();
        return TestStarts.Count == 0 ? null : TestStarts.Min().ToUniversalTime().AddHours(24);
    }

    public void RecordTestStart()
    {
        PruneStarts();
        TestStarts.Add(DateTime.UtcNow);
    }

    private void PruneStarts()
    {
        TestStarts ??= new List<DateTime>();
        var cutoff = DateTime.UtcNow.AddHours(-24);
        TestStarts.RemoveAll(t => t.ToUniversalTime() < cutoff);
    }

    public static WidgetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new WidgetSettings();
                loaded.AutoTestMinutes = NormalizeInterval(loaded.AutoTestMinutes);
                loaded.History ??= new List<TestRecord>();
                loaded.PruneStarts();
                return loaded;
            }
        }
        catch (Exception ex)
        {
            LogError(ex);
            try { File.Copy(FilePath, FilePath + ".bak", true); } catch { }
        }
        return new WidgetSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    public static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, "errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } path)
                key.SetValue(RunName, $"\"{path}\"");
            else
                key.DeleteValue(RunName, false);
        }
    }
}
