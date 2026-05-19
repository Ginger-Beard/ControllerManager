using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ControllerManager.Models;
using Microsoft.Win32;

namespace ControllerManager.Services;

public static class DeviceController
{
    public static void SetEnabled(HidDevice device, bool enable)
    {
        // Prefer the USB function node when available — some drivers (e.g. MOZA Windows
        // Driver) protect their HID child from Disable-PnpDevice but allow the USB
        // function node, which is what the pre-filter code was implicitly using.
        var startId = !string.IsNullOrEmpty(device.AlternativeInstanceId)
            ? device.AlternativeInstanceId
            : device.InstanceId;
        SetEnabledById(startId, enable);
    }

    public static void SetEnabledById(string instanceId, bool enable)
    {
        Logger.WriteVerbose($"[DeviceController] {(enable ? "Enable" : "Disable")} → {instanceId}");

        // Build the candidate chain (HID child → USB function → USB composite root).
        // Pass all candidates to a single PowerShell invocation that tries each in order —
        // avoids 2-3 separate process startups when the first node is protected.
        var candidates = BuildCandidateChain(instanceId);
        RunPnpCommandWithFallback(candidates, enable);
    }

    // Walks up the device tree collecting: the device itself, then each ancestor with a
    // VID/PID (USB function nodes, USB composite root). Stops at the composite root —
    // the first ancestor that has VID/PID but no interface tag (&MI_ / &IG_ / &Col).
    // This prevents us from ever reaching USB hubs, which also carry VID/PID.
    private static List<string> BuildCandidateChain(string instanceId)
    {
        var chain = new List<string> { instanceId };

        // Extract the VID from the starting node — we will NEVER add any ancestor
        // to the chain unless it carries the same VID. This is an absolute safety
        // guard: a USB hub (e.g. Realtek VID_0BDA) can never appear in a chain
        // built for a MOZA device (VID_346E), regardless of tree depth.
        var vidMatch = VidPidRx.Match(instanceId);
        if (!vidMatch.Success) return chain;
        var originalVid = vidMatch.Value; // e.g. "VID_346E"

        try
        {
            if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != CR_SUCCESS) return chain;

            for (int depth = 0; depth < 5; depth++)
            {
                if (CM_Get_Parent(out uint parent, node, 0) != CR_SUCCESS) break;
                if (CM_Get_Device_ID_Size(out uint size, parent, 0) != CR_SUCCESS) break;

                var sb = new StringBuilder((int)(size + 1));
                if (CM_Get_Device_IDW(parent, sb, size + 1, 0) != CR_SUCCESS) break;

                var parentId = sb.ToString();

                // Stop if no VID/PID (generic bus) or different VID (different device — e.g. USB hub)
                if (!parentId.Contains(originalVid, StringComparison.OrdinalIgnoreCase)) break;

                // Only add nodes that still have an interface tag (&MI_, &IG_, &Col).
                // The composite root (VID/PID present, no tag) is intentionally excluded —
                // some drivers (MOZA) have a buggy disable path where pnputil returns
                // "not supported" but the device still ends up disabled in Windows.
                // Never touching the composite root prevents that entirely.
                bool hasInterfaceTag =
                    parentId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) ||
                    parentId.Contains("&IG_", StringComparison.OrdinalIgnoreCase) ||
                    parentId.Contains("&Col", StringComparison.OrdinalIgnoreCase);
                if (!hasInterfaceTag) break;

                chain.Add(parentId);

                node = parent;
            }
        }
        catch { }
        return chain;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static readonly Regex VidPidRx = new(
        @"VID_[0-9A-Fa-f]{4}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Calls pnputil.exe directly for each candidate — pnputil uses the Configuration
    // Manager API rather than WMI, which bypasses driver-level WMI blocks that cause
    // Disable-PnpDevice to return HRESULT 0x80041001 (e.g. MOZA Windows Driver).
    // pnputil starts much faster than PowerShell so separate invocations are fine.
    private static void RunPnpCommandWithFallback(List<string> candidates, bool enable)
    {
        var verb = enable ? "/enable-device" : "/disable-device";

        Exception? firstException = null;
        foreach (var candidate in candidates)
        {
            Logger.WriteVerbose($"[DeviceController] pnputil {verb} {candidate}");
            try
            {
                var (exitCode, stdout, stderr) = RunPnputil($"{verb} \"{candidate}\"");

                if (!string.IsNullOrEmpty(stdout)) Logger.WriteVerbose($"[DeviceController] stdout: {stdout}");
                if (!string.IsNullOrEmpty(stderr)) Logger.WriteVerbose($"[DeviceController] stderr: {stderr}");
                Logger.WriteVerbose($"[DeviceController] exit code: {exitCode}");

                if (exitCode == 0) return;

                // exit 3010 = ERROR_SUCCESS_REBOOT_REQUIRED: the MOZA Windows Driver (and some
                // others) can't fully stop their driver stack while it's in use, but the device
                // IS immediately invisible to DirectInput/games — ConfigFlags=1 is written and
                // the HID interface disappears from the enumeration. The driver unloads on next
                // reboot. For our purposes the disable succeeded.
                if (!enable && exitCode == 3010) return;

                // exit 50 on disable with "pending system reboot": two possible states.
                // (a) ConfigFlags=1 — a previous 3010 already disabled the device; it's
                //     invisible to games. Treat as success.
                // (b) ConfigFlags=0 — the driver's internal pending state survived a
                //     registry clear+rescan recovery, but the device is NOT actually
                //     invisible. Report an error so the user knows to reboot.
                if (!enable && exitCode == 50 &&
                    stdout.Contains("pending system reboot", StringComparison.OrdinalIgnoreCase))
                {
                    if (ReadConfigFlags(candidate) == 1) return;
                    throw new InvalidOperationException(
                        "The driver did not release its pending state after recovery. " +
                        "Reboot Windows to restore normal operation.");
                }

                // exit 50 on enable with "pending system reboot" = device was disabled via the
                // 3010 path above. pnputil refuses to re-enable it in that state. Clear
                // ConfigFlags in the registry and trigger a hardware rescan, which re-enumerates
                // the device without a full reboot.
                if (enable && exitCode == 50 &&
                    stdout.Contains("pending system reboot", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.WriteVerbose($"[DeviceController] pending-reboot enable fallback → {candidate}");
                    ClearConfigFlagsAndScan(candidate);
                    return;
                }

                var msg = !string.IsNullOrEmpty(stderr) ? stderr
                        : !string.IsNullOrEmpty(stdout) ? stdout
                        : $"pnputil exited {exitCode}";
                throw new InvalidOperationException(msg);
            }
            catch (Exception ex)
            {
                Logger.Write($"[DeviceController] Failed ({candidate}): {ex.Message}");
                firstException ??= ex;
            }
        }

        throw firstException ?? new InvalidOperationException("Failed to change device state.");
    }

    private static (int exitCode, string stdout, string stderr) RunPnputil(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName               = "pnputil.exe",
            Arguments              = arguments,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        })!;

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        var exited     = p.WaitForExit(15_000);

        var stdout = stdoutTask.Result.Trim();
        var stderr = stderrTask.Result.Trim();

        if (!exited)
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException("pnputil did not complete within 15 seconds.");
        }

        return (p.ExitCode, stdout, stderr);
    }

    // Clears ConfigFlags=1 (disabled marker) directly in the registry, then triggers a
    // hardware rescan to re-enumerate the device — bypasses the pnputil block on devices
    // stuck in "pending system reboot" state after a 3010 disable.
    private static void ClearConfigFlagsAndScan(string instanceId)
    {
        var regPath = $@"SYSTEM\CurrentControlSet\Enum\{instanceId}";
        using (var key = Registry.LocalMachine.OpenSubKey(regPath, writable: true))
        {
            if (key != null)
            {
                key.SetValue("ConfigFlags", 0, RegistryValueKind.DWord);
                Logger.WriteVerbose($"[DeviceController] Cleared ConfigFlags for {instanceId}");
            }
            else
            {
                Logger.Write($"[DeviceController] Warning: registry key not found for {instanceId}");
            }
        }

        Logger.WriteVerbose("[DeviceController] pnputil /scan-devices");
        var (exitCode, stdout, _) = RunPnputil("/scan-devices");
        if (!string.IsNullOrEmpty(stdout)) Logger.WriteVerbose($"[DeviceController] stdout: {stdout}");
        Logger.WriteVerbose($"[DeviceController] exit code: {exitCode}");

        if (exitCode != 0)
            throw new InvalidOperationException($"pnputil /scan-devices failed (exit {exitCode})");
    }

    private static int ReadConfigFlags(string instanceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            return Convert.ToInt32(key?.GetValue("ConfigFlags") ?? 0);
        }
        catch { return 0; }
    }

    // ── CfgMgr32 parent lookup ───────────────────────────────────────────────────

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint dnDevInst, StringBuilder Buffer, uint BufferLen, uint ulFlags);

    private const int CR_SUCCESS = 0;
}
