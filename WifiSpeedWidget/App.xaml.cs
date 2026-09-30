using System.Windows;
using Microsoft.Win32;

namespace WifiSpeedWidget;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private TrayIcon? _tray;

    private static string InstanceSuffix => Environment.GetEnvironmentVariable("SPEEDLINE_INSTANCE_SUFFIX") ?? "";

    public static string ShowSignalName => "WifiSpeedWidget.ShowSignal" + InstanceSuffix;

    public static bool Exiting { get; private set; }

    public TrayIcon? Tray => _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WidgetSettings.LogError(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WidgetSettings.LogError(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WidgetSettings.LogError(args.Exception);
            args.SetObserved();
        };

        _instanceMutex = new Mutex(true, "WifiSpeedWidget.SingleInstance" + InstanceSuffix, out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        Exiting = false;
        Theme.Apply();
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Dispatcher.BeginInvoke(new Action(Theme.Apply));
        };

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        try
        {
            _tray = new TrayIcon(window);
        }
        catch (Exception ex)
        {
            WidgetSettings.LogError(ex);
        }
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(new Action(window.ShowWidget)), null, Timeout.Infinite, false);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Exiting = true;
        base.OnSessionEnding(e);
    }

    public void ExitApp()
    {
        Exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _tray?.Dispose();
        _tray = null;
        base.OnExit(e);
    }
}
