using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WifiSpeedWidget;

public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x0401;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0403;
    private const int WmContextMenu = 0x007B;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint MfString = 0;
    private const uint MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x100;
    private const uint TpmRightButton = 0x2;
    private const uint TpmBottomAlign = 0x20;
    private const int CmdShow = 1;
    private const int CmdTest = 2;
    private const int CmdExit = 3;

    private readonly MainWindow _window;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private bool _ownsIcon;
    private NotifyIconData _data;

    public bool Visible { get; private set; }

    public TrayIcon(MainWindow window)
    {
        _window = window;
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
    }

    private void LoadIcon()
    {
        var path = Environment.ProcessPath;
        if (path != null && ExtractIconEx(path, 0, IntPtr.Zero, out var small, 1) > 0 && small != IntPtr.Zero)
        {
            _icon = small;
            _ownsIcon = true;
            return;
        }
        _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    private void Add()
    {
        _data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = AppInfo.Name,
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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated)
        {
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
        if (Visible)
        {
            Shell_NotifyIcon(NimDelete, ref _data);
            Visible = false;
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
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
