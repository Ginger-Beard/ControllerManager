using ControllerManager.Services;

namespace ControllerManager.Cli;

/// <summary>
/// Handles --launch &lt;profileId&gt; when this is the first instance.
/// Finds the profile and starts the orchestrator. The main window
/// (if shown) will reflect state via the Dashboard VM.
/// </summary>
public static class LaunchInvocation
{
    public static void Handle(string[] args, LaunchOrchestrator orchestrator, ProfileStore profiles)
    {
        if (args.Length == 0) return;
        if (!Guid.TryParse(args[0], out var id)) return;

        var profile = profiles.Load().FirstOrDefault(p => p.Id == id);
        if (profile is null) return;

        orchestrator.Start(profile);
    }
}
