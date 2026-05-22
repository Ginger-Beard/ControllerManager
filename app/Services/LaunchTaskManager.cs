using System.Diagnostics;
using System.Security;
using System.Text;

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
    /// Returns <c>(true, null)</c> on success; on failure, returns
    /// <c>(false, &lt;captured stderr/stdout&gt;)</c> so the caller can surface
    /// the real reason from schtasks.exe (access denied, policy block, etc.)
    /// instead of a generic message.
    ///
    /// <para>We register via <c>/XML</c> rather than the inline <c>/SC ...</c>
    /// form because the natural "manual-only" schedule type, <c>/SC ONDEMAND</c>,
    /// isn't accepted on every Windows build/locale (some users get
    /// "ERROR: Invalid Schedule Type specified."). A Task Scheduler XML with
    /// empty <c>&lt;Triggers/&gt;</c> + <c>&lt;AllowStartOnDemand&gt;true&lt;/AllowStartOnDemand&gt;</c>
    /// is the spec-defined equivalent and works regardless of locale.</para>
    /// </summary>
    public static (bool Ok, string? Error) EnsureTaskForProfile(Guid profileId)
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe))
            return (false, "Could not determine the Controller Manager executable path.");

        string? xmlPath = null;
        try
        {
            xmlPath = Path.Combine(Path.GetTempPath(), $"cm-task-{profileId:N}.xml");

            // schtasks /XML expects UTF-16 LE with BOM, matching the
            // encoding="UTF-16" attribute in the XML declaration.
            File.WriteAllText(xmlPath, BuildTaskXml(exe, profileId),
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            var args = $"/Create /F /TN \"{TaskName(profileId)}\" /XML \"{xmlPath}\"";
            return RunSchtasks(args);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (xmlPath is not null)
                try { File.Delete(xmlPath); } catch { /* best-effort cleanup */ }
        }
    }

    // Minimal Task Scheduler v1.2 XML: empty Triggers + AllowStartOnDemand =
    // task only fires when something calls `schtasks /Run /TN ...`. Runs as
    // the current user with HighestAvailable elevation (same effect as the
    // old /RL HIGHEST /IT pair) without needing /RU + a password.
    private static string BuildTaskXml(string exe, Guid profileId)
    {
        var safeExe = SecurityElement.Escape(exe);
        var idStr   = profileId.ToString("N");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Controller Manager — per-profile launch task</Description>
              </RegistrationInfo>
              <Triggers />
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{safeExe}</Command>
                  <Arguments>--launch {idStr}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
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

    private static (bool Ok, string? Error) RunSchtasks(string args)
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

            // Read both streams before WaitForExit to avoid the child blocking
            // on a full pipe when its output is larger than the buffer.
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);

            if (p.ExitCode == 0) return (true, null);

            // schtasks usually writes the real reason to stderr ("ERROR: Access
            // is denied.", policy messages, etc.). Fall back to stdout if
            // stderr is empty, and finally to the bare exit code.
            var detail = (!string.IsNullOrWhiteSpace(stderr) ? stderr
                       : !string.IsNullOrWhiteSpace(stdout) ? stdout
                       : $"schtasks.exe exited with code {p.ExitCode}.").Trim();
            return (false, detail);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
