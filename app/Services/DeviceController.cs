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
                RedirectStandardError  = true,
            })!;
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                var err = p.StandardError.ReadToEnd().Trim();
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(err) ? $"PowerShell exited {p.ExitCode}" : err);
            }
        }
        finally { File.Delete(tmp); }
    }

    private static string Escape(string s) => s.Replace("'", "''");
}
