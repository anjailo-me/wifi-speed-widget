using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel;
using Windows.Networking.Connectivity;

namespace WifiSpeedWidget;

public static class AppInfo
{
    public static string Name { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Speedline";
}

internal static class Platform
{
    private const string StartupTaskId = "SpeedlineStartup";
    private const int NoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    public static bool IsPackaged { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) != NoPackage;
    }

    public static async Task<bool> GetStartupEnabledAsync()
    {
        try
        {
            if (!IsPackaged) return WidgetSettings.StartsWithWindows;
            var task = await StartupTask.GetAsync(StartupTaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception ex)
        {
            WidgetSettings.LogError(ex);
            return false;
        }
    }

    public static async Task<StartupResult> SetStartupAsync(bool enable)
    {
        try
        {
            if (!IsPackaged)
            {
                WidgetSettings.StartsWithWindows = enable;
                return enable ? StartupResult.Enabled : StartupResult.Disabled;
            }

            var task = await StartupTask.GetAsync(StartupTaskId);
            if (!enable)
            {
                task.Disable();
                return StartupResult.Disabled;
            }

            var state = await task.RequestEnableAsync();
            return state switch
            {
                StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => StartupResult.Enabled,
                StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy => StartupResult.BlockedBySettings,
                _ => StartupResult.Disabled
            };
        }
        catch (Exception ex)
        {
            WidgetSettings.LogError(ex);
            return StartupResult.Disabled;
        }
    }

    public static bool IsMeteredConnection()
    {
        try
        {
            var cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            if (cost == null) return false;
            return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
                   || cost.Roaming
                   || cost.OverDataLimit;
        }
        catch (Exception ex)
        {
            WidgetSettings.LogError(ex);
            return false;
        }
    }
}

public enum StartupResult { Enabled, Disabled, BlockedBySettings }
