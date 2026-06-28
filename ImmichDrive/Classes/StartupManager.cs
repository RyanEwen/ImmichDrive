using Microsoft.Win32;
using Windows.ApplicationModel;

namespace ImmichDrive.Classes;

/// <summary>
/// Toggles "run at sign-in". For the packaged app this drives the MSIX <c>windows.startupTask</c>
/// (declared in the manifest, enabled by default) so the cloud provider is always running to
/// hydrate files. Falls back to the per-user Run key for unpackaged dev builds.
/// </summary>
internal static class StartupManager
{
    private const string TaskId = "ImmichDriveAutoStart";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ImmichDrive";

    public static void SetRunAtStartup(bool enabled)
    {
        // Fire-and-forget on a background thread so we never block the UI thread on a WinRT async.
        _ = Task.Run(async () =>
        {
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (enabled) await task.RequestEnableAsync();
                else task.Disable();
            }
            catch
            {
                SetRunKey(enabled); // unpackaged / API unavailable
            }
        });
    }

    public static bool IsEnabled()
    {
        try
        {
            var task = Task.Run(async () => await StartupTask.GetAsync(TaskId)).GetAwaiter().GetResult();
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch
        {
            return IsRunKeyEnabled();
        }
    }

    private static void SetRunKey(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (exe != null) key.SetValue(ValueName, $"\"{exe}\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best effort */ }
    }

    private static bool IsRunKeyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }
}
