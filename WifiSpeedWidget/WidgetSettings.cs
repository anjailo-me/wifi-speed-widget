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

    public double? Left { get; set; }
    public double? Top { get; set; }
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
        catch
        {
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
