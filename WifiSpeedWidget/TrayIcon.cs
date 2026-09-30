using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace WifiSpeedWidget;

public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x0401;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0403;
    private const int WmContextMenu = 0x007B;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifInfo = 0x10;
    private const uint NifShowTip = 0x80;
    private const uint NiifRespectQuietTime = 0x80;
    private const uint MfString = 0;
    private const uint MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x100;
    private const uint TpmRightButton = 0x2;
    private const uint TpmBottomAlign = 0x20;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49;
    private const int CmdShow = 1;
    private const int CmdTest = 2;
    private const int CmdExit = 3;

    private static string AssetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");

    private readonly MainWindow _window;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private bool _ownsIcon;
    private NotifyIconData _data;

    public bool Visible { get; private set; }
    public bool ThemedIcon { get; private set; }
    public string Tooltip { get; private set; }
    public int NoticesShown { get; private set; }

    public TrayIcon(MainWindow window)
    {
        _window = window;
        Tooltip = window.TrayText;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _source = new HwndSource(new HwndSourceParameters("SpeedlineTray")
        {
            ParentWindow = new IntPtr(-3),
            Width = 0,
            Height = 0,
            WindowStyle = 0
        });
        _source.AddHook(WndProc);
        LoadIcon();
        Add();
        Theme.Changed += OnThemeChanged;
    }

    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    private void LoadIcon()
    {
        var size = GetSystemMetrics(SmCxSmIcon);
        var file = Path.Combine(AssetsDir, TaskbarIsLight() ? "tray-light.ico" : "tray-dark.ico");
        if (File.Exists(file))
        {
            var themed = LoadImage(IntPtr.Zero, file, ImageIcon, size, size, LrLoadFromFile);
            if (themed != IntPtr.Zero)
            {
                SetIcon(themed, true);
                ThemedIcon = true;
                return;
            }
        }
        ThemedIcon = false;
        var path = Environment.ProcessPath;
        if (path != null && ExtractIconEx(path, 0, IntPtr.Zero, out var small, 1) > 0 && small != IntPtr.Zero)
        {
            SetIcon(small, true);
            return;
        }
        SetIcon(LoadIcon(IntPtr.Zero, new IntPtr(32512)), false);
    }

    private void SetIcon(IntPtr icon, bool owned)
    {
        var old = _icon;
        var oldOwned = _ownsIcon;
        _icon = icon;
        _ownsIcon = owned;
        if (oldOwned && old != IntPtr.Zero) DestroyIcon(old);
    }

    private static string Clip(string text, int max) => text.Length < max ? text : text[..(max - 1)];

    private void Add()
    {
        _data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = Clip(Tooltip, 128),
            szInfo = "",
            szInfoTitle = ""
        };
        Visible = Shell_NotifyIcon(NimAdd, ref _data);
        if (Visible)
        {
            _data.uVersionOrTimeout = 4;
            Shell_NotifyIcon(NimSetVersion, ref _data);
        }
    }

    private void Modify(uint flags)
    {
        if (!Visible) return;
        _data.uFlags = flags;
        _data.hIcon = _icon;
        _data.szTip = Clip(Tooltip, 128);
        Shell_NotifyIcon(NimModify, ref _data);
    }

    public void SetTooltip(string text)
    {
        if (text == Tooltip) return;
        Tooltip = text;
        Modify(NifTip | NifShowTip);
    }

    public void ShowNotice(string title, string text)
    {
        if (!Visible) return;
        var notice = _data;
        notice.uFlags = NifInfo;
        notice.szInfoTitle = Clip(title, 64);
        notice.szInfo = Clip(text, 256);
        notice.dwInfoFlags = NiifRespectQuietTime;
        if (Shell_NotifyIcon(NimModify, ref notice)) NoticesShown++;
    }

    private void OnThemeChanged()
    {
        LoadIcon();
        Modify(NifIcon | NifTip | NifShowTip);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated)
        {
            LoadIcon();
            Add();
            return IntPtr.Zero;
        }
        if (msg != CallbackMessage) return IntPtr.Zero;

        var evt = (int)((long)lParam & 0xFFFF);
        if (evt is NinSelect or NinKeySelect)
        {
            handled = true;
            _window.ShowWidget();
        }
        else if (evt == WmContextMenu)
        {
            handled = true;
            ShowMenu();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MfString, CmdShow, "Show " + AppInfo.Name);
            AppendMenu(menu, MfString, CmdTest, _window.IsTesting ? "Stop test" : "Test now");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, CmdExit, "Exit");
            GetCursorPos(out var pt);
            SetForegroundWindow(_source.Handle);
            var cmd = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | TpmBottomAlign, pt.X, pt.Y, _source.Handle, IntPtr.Zero);
            PostMessage(_source.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            switch (cmd)
            {
                case CmdShow: _window.ShowWidget(); break;
                case CmdTest: _window.ToggleTest(); break;
                case CmdExit: ((App)Application.Current).ExitApp(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        Theme.Changed -= OnThemeChanged;
        if (Visible)
        {
            Shell_NotifyIcon(NimDelete, ref _data);
            Visible = false;
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
        SetIcon(IntPtr.Zero, false);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr reserved);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
