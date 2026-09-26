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
    public int AutoTestMinutes { get; set; } = -1;
    public List<TestRecord> History { get; set; } = new();

    public static WidgetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new WidgetSettings();
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
