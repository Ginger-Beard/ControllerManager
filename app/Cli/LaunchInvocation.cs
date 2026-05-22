using ControllerManager.Services;

namespace ControllerManager.Cli;

/// <summary>
/// Handles <c>--launch &lt;profileId&gt;</c> when this is the first instance.
/// Profile-agnostic stop is the separate <c>--restore</c> top-level flag
/// (see <see cref="RestoreInvocation"/>).
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
