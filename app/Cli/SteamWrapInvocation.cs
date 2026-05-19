using System.Diagnostics;
using ControllerManager.Services;

namespace ControllerManager.Cli;

/// <summary>
/// Handles --steam-wrap &lt;profileId&gt; -- &lt;game args...&gt;
///
/// Usage in Steam Launch Options:
///   "C:\path\ControllerManager.exe" --steam-wrap {profileId} -- %command%
///
/// Flow:
///   1. Hide profile devices via HidHide (session blacklist — auto-cleans if we crash)
///   2. Launch the real game command (args after --)
///   3. Wait for the game process to exit
///   4. Clear HidHide session
///
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

        var toHide = profile.DisableThenRestore.Concat(profile.KeepDisabled).ToList();
        if (toHide.Count > 0 && hidHide.IsAvailable)
            hidHide.BeginGameSession(toHide.Select(d => d.InstanceId), gameExe);

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName        = gameExe,
            Arguments       = gameArgs.Length > 1 ? string.Join(" ", gameArgs[1..]) : "",
            UseShellExecute = true,
        });

        if (proc is not null)
            await proc.WaitForExitAsync();

        if (toHide.Count > 0 && hidHide.IsAvailable)
            hidHide.EndGameSession();
    }
}
