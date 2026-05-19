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
            var keepIds = profile.KeepEnabled
                .Select(d => d.InstanceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toHide = new DeviceEnumerator()
                .GetAll(showAllHid: false)
                .Where(d => !keepIds.Contains(d.InstanceId))
                .Select(d => d.InstanceId)
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
