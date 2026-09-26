using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WifiSpeedWidget;

public static class Theme
{
    public static bool IsDark { get; private set; } = true;
    public static Color BorderColor { get; private set; }
    public static event Action? Changed;

    public static void Apply()
    {
        IsDark = ReadAppsDark();
        var (accentOnDark, accentOnLight) = ReadAccent();
        var res = Application.Current.Resources;

        void Set(string key, string dark, string light) => res[key] = MakeBrush(Parse(IsDark ? dark : light));

        Set("Surface", "#202020", "#F9F9F9");
        Set("Divider", "#17FFFFFF", "#0F000000");
        Set("TextPrimary", "#FFFFFF", "#1A1A1A");
        Set("TextSecondary", "#C8C8C8", "#5E5E5E");
        Set("TextTertiary", "#8C8C8C", "#8A8A8A");
        Set("SubtleHover", "#0FFFFFFF", "#0A000000");
        Set("SubtlePressed", "#0AFFFFFF", "#06000000");
        Set("ControlFill", "#0FFFFFFF", "#FFFFFF");
        Set("ControlFillHover", "#15FFFFFF", "#F6F6F6");
        Set("ControlStroke", "#17FFFFFF", "#1A000000");
        Set("MenuSurface", "#2C2C2C", "#FCFCFC");
        Set("MenuStroke", "#1FFFFFFF", "#1A000000");
        Set("ChartSecondary", "#52FFFFFF", "#3D000000");
        Set("SignalOff", "#3DFFFFFF", "#2E000000");
        Set("TrackFill", "#1AFFFFFF", "#14000000");
        Set("Good", "#6CCB5F", "#0F7B0F");
        Set("Caution", "#FCE100", "#9D5D00");
        Set("Critical", "#FF99A4", "#C42B1C");

        res["Accent"] = MakeBrush(IsDark ? accentOnDark : accentOnLight);
        res["AccentFg"] = MakeBrush(IsDark ? Colors.Black : Colors.White);
        BorderColor = IsDark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xDD, 0xDD, 0xDD);

        Changed?.Invoke();
    }

    private static SolidColorBrush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static bool ReadAppsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (Color OnDark, Color OnLight) ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] { Length: >= 32 } p)
                return (Color.FromRgb(p[4], p[5], p[6]), Color.FromRgb(p[16], p[17], p[18]));
        }
        catch
        {
        }
        return (Color.FromRgb(0x4C, 0xC2, 0xFF), Color.FromRgb(0x00, 0x5F, 0xB8));
    }
}

internal static class Native
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int WsExAppWindow = 0x40000;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public static void MakeToolWindow(IntPtr hwnd)
    {
        var ex = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, (ex | WsExToolWindow) & ~WsExAppWindow);
    }

    public static void ApplyFrame(IntPtr hwnd, bool dark, Color border)
    {
        var darkValue = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref darkValue, sizeof(int));
        var round = 2;
        DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref round, sizeof(int));
        var colorRef = border.R | (border.G << 8) | (border.B << 16);
        DwmSetWindowAttribute(hwnd, DwmBorderColor, ref colorRef, sizeof(int));
    }
}
