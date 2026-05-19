using System.Diagnostics;
using ControllerManager.Services;

namespace ControllerManager.Cli;

/// <summary>
/// Handles --steam-wrap &lt;profileId&gt; -- &lt;game args...&gt;
///
/// Hides ALL gaming devices except those in KeepEnabled (same as the orchestrator),
/// launches the real game command, waits for exit, then restores.
/// This process stays alive so Steam correctly tracks playtime.
/// </summary>
public static class SteamWrapInvocation
{
    public static async Task HandleAsync(string[] args, HidHideClient hidHide, ProfileStore profiles)
    {
        if (args.Length < 1) return;
        if (!Guid.TryParse(args[0], out var id)) return;

        int sep = Array.IndexOf(args, "--");
        if (sep < 0 || sep + 1 >= args.Length) return;

        var gameArgs = args[(sep + 1)..];
        var gameExe  = gameArgs[0];

        var profile = profiles.Load().FirstOrDefault(p => p.Id == id);
        if (profile is null) return;

        if (hidHide.IsAvailable)
        {
            var allDevices    = new DeviceEnumerator().GetAll(showAllHid: false);
            var keepPrimaries = profile.KeepEnabled
                .Select(d => d.InstanceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Expand both sets to include every sibling HID interface — composite
            // devices need every child either kept visible or explicitly hidden,
            // since HidHide's kernel filter does direct string compare.
            var keepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in allDevices)
            {
                if (!keepPrimaries.Contains(d.InstanceId)) continue;
                if (d.ChildInstanceIds.Count > 0)
                    foreach (var c in d.ChildInstanceIds) keepIds.Add(c);
                else
                    keepIds.Add(d.InstanceId);
            }

            var toHide = allDevices
                .Where(d => !keepPrimaries.Contains(d.InstanceId))
                .SelectMany(d => d.ChildInstanceIds.Count > 0 ? d.ChildInstanceIds : [d.InstanceId])
                .ToList();

            if (toHide.Count > 0)
                hidHide.BeginGameSession(toHide, keepIds, gameExe);
        }

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName        = gameExe,
            Arguments       = gameArgs.Length > 1 ? string.Join(" ", gameArgs[1..]) : "",
            UseShellExecute = true,
        });

        if (proc is not null)
            await proc.WaitForExitAsync();

        if (hidHide.IsAvailable)
            hidHide.EndGameSession();
    }
}
