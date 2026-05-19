using System.Diagnostics;

namespace ControllerManager.Services;

/// <summary>
/// Creates and removes per-profile Scheduled Tasks so launch shortcuts can avoid
/// the UAC prompt on every click. The app exe has <c>requireAdministrator</c> in
/// its manifest; launching it directly triggers a UAC prompt every time, even
/// when CM is already running in the tray (the second instance still elevates
/// before it can forward IPC and exit). A scheduled task configured with
/// "Run with highest privileges" runs without a prompt when triggered by an
/// authorized user via <c>schtasks /Run /TN ...</c>.
///
/// Task naming: <c>ControllerManager_Launch_{guid}</c>. Created lazily when a
/// shortcut is exported for a profile; deleted when the profile is deleted.
/// Orphaned tasks (shortcut deleted, profile kept) are harmless — they just sit
/// in the scheduler doing nothing.
/// </summary>
public static class LaunchTaskManager
{
    private const string TaskPrefix = "ControllerManager_Launch_";

    public static string TaskName(Guid profileId) =>
        TaskPrefix + profileId.ToString("N");

    /// <summary>
    /// Creates (or updates) the scheduled task that runs
    /// <c>ControllerManager.exe --launch &lt;profileId&gt;</c> elevated, no UAC.
    /// Idempotent — <c>/F</c> overwrites any existing task with the same name.
    /// </summary>
    public static bool EnsureTaskForProfile(Guid profileId)
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe)) return false;

        var args = $"/Create /F /TN \"{TaskName(profileId)}\" " +
                   $"/TR \"\\\"{exe}\\\" --launch {profileId}\" " +
                   $"/SC ONDEMAND /RL HIGHEST /IT";

        return RunSchtasks(args);
    }

    /// <summary>Deletes the per-profile launch task. Idempotent.</summary>
    public static void DeleteTaskForProfile(Guid profileId)
    {
        RunSchtasks($"/Delete /F /TN \"{TaskName(profileId)}\"");
    }

    public static bool TaskExists(Guid profileId)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName               = "schtasks.exe",
                Arguments              = $"/Query /TN \"{TaskName(profileId)}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            })!;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName               = "schtasks.exe",
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            })!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
