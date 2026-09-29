using System.Windows;
using Microsoft.Win32;

namespace WifiSpeedWidget;

public partial class App : Application
{
    private static Mutex? _instanceMutex;

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

        _instanceMutex = new Mutex(true, "WifiSpeedWidget.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        Theme.Apply();
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Dispatcher.BeginInvoke(new Action(Theme.Apply));
        };

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
