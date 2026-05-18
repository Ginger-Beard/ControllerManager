using System.Diagnostics;
using HIDReorder.Models;

namespace HIDReorder.Services;

public static class DeviceController
{
    public static void SetEnabled(HidDevice device, bool enable)
        => SetEnabledById(device.InstanceId, enable);

    public static void SetEnabledById(string instanceId, bool enable)
    {
        var action = enable ? "Enable-PnpDevice" : "Disable-PnpDevice";
        var script = $"$ErrorActionPreference = 'Stop'\n" +
                     $"{action} -InstanceId '{Escape(instanceId)}' -Confirm:$false";

        Logger.WriteVerbose($"[DeviceController] {action} → {instanceId}");

        var tmp = Path.ChangeExtension(Path.GetTempFileName(), ".ps1");
        File.WriteAllText(tmp, script);
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = $"-ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"{tmp}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            })!;

            // Read async to avoid deadlock if buffers fill up
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            var exited     = p.WaitForExit(30_000);

            var stdout = stdoutTask.Result.Trim();
            var stderr = stderrTask.Result.Trim();

            if (!string.IsNullOrEmpty(stdout))
                Logger.WriteVerbose($"[DeviceController] stdout: {stdout}");
            if (!string.IsNullOrEmpty(stderr))
                Logger.WriteVerbose($"[DeviceController] stderr: {stderr}");

            if (!exited)
            {
                try { p.Kill(); } catch { }
                Logger.Write("[DeviceController] PowerShell timed out after 30s — process killed");
                throw new TimeoutException("PowerShell did not complete within 30 seconds.");
            }

            Logger.WriteVerbose($"[DeviceController] exit code: {p.ExitCode}");

            if (p.ExitCode != 0)
            {
                var msg = !string.IsNullOrEmpty(stderr) ? stderr : $"PowerShell exited {p.ExitCode}";
                throw new InvalidOperationException(msg);
            }
        }
        finally { File.Delete(tmp); }
    }

    private static string Escape(string s) => s.Replace("'", "''");
}
