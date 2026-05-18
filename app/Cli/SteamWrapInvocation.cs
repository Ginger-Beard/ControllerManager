using System.Diagnostics;
using HIDReorder.Services;

namespace HIDReorder.Cli;

/// <summary>
/// Handles --steam-wrap &lt;profileId&gt; -- &lt;game args...&gt;
///
/// Usage in Steam Launch Options:
///   "C:\path\HIDReorder.exe" --steam-wrap {profileId} -- %command%
///
/// Flow:
///   1. Disable profile devices (DisableThenRestore + KeepDisabled)
///   2. Launch the real game command (args after --)
///   3. Wait for the game process to exit
///   4. Re-enable all devices
///
/// This process stays alive so Steam correctly tracks playtime.
/// </summary>
public static class SteamWrapInvocation
{
    public static async Task HandleAsync(string[] args, StateStore state, ProfileStore profiles)
    {
        // Parse: --steam-wrap <profileId> -- <game args...>
        if (args.Length < 3) return;
        if (!Guid.TryParse(args[0], out var id)) return;

        int sep = Array.IndexOf(args, "--");
        if (sep < 0 || sep + 1 >= args.Length) return;

        var gameArgs = args[(sep + 1)..];

        var profile = profiles.Load().FirstOrDefault(p => p.Id == id);
        if (profile is null) return;

        // Disable devices
        var toDisable = profile.DisableThenRestore.Concat(profile.KeepDisabled).ToList();
        foreach (var dev in toDisable)
        {
            try
            {
                state.RecordDisabledRef(dev);
                DeviceController.SetEnabledById(dev.InstanceId, false);
            }
            catch { }
        }

        // Launch the real game command
        // gameArgs[0] is the exe, rest are arguments
        var gameExe  = gameArgs[0];
        var gameRest = gameArgs.Length > 1 ? string.Join(" ", gameArgs[1..]) : "";

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName        = gameExe,
            Arguments       = gameRest,
            UseShellExecute = true,
        });

        if (proc is null) goto restore;

        // Wait for game to exit
        await proc.WaitForExitAsync();

    restore:
        // Re-enable everything we disabled
        foreach (var dev in toDisable)
        {
            try
            {
                DeviceController.SetEnabledById(dev.InstanceId, true);
                state.ClearEnabledById(dev.InstanceId);
            }
            catch { }
        }
    }
}
