using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HIDReorder.Models;

namespace HIDReorder.Services;

public static class DeviceController
{
    public static event EventHandler? DeviceStateChanged;

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
        DeviceStateChanged?.Invoke(null, EventArgs.Empty);
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
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName               = "pnputil.exe",
                    Arguments              = $"{verb} \"{candidate}\"",
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

                if (!string.IsNullOrEmpty(stdout)) Logger.WriteVerbose($"[DeviceController] stdout: {stdout}");
                if (!string.IsNullOrEmpty(stderr)) Logger.WriteVerbose($"[DeviceController] stderr: {stderr}");

                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("pnputil did not complete within 15 seconds.");
                }

                Logger.WriteVerbose($"[DeviceController] exit code: {p.ExitCode}");

                if (p.ExitCode == 0) return; // success — done

                var msg = !string.IsNullOrEmpty(stderr) ? stderr
                        : !string.IsNullOrEmpty(stdout) ? stdout
                        : $"pnputil exited {p.ExitCode}";
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

    private static string Escape(string s) => s.Replace("'", "''");
}
