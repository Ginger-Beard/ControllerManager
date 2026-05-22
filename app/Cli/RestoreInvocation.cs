using ControllerManager.Services;

namespace ControllerManager.Cli;

/// <summary>
/// Handles the standalone <c>--restore</c> CLI flag — ends whatever profile is
/// currently active and restores hidden devices. Takes no arguments.
///
/// Paired with <c>--launch &lt;profileId&gt;</c> for streaming hosts:
/// <c>--launch &lt;id&gt;</c> goes in Sunshine/Apollo's Do command,
/// <c>--restore</c> goes in the Undo command — and the Undo doesn't need to
/// know the profile id.
/// </summary>
public static class RestoreInvocation
{
    public static void Handle(LaunchOrchestrator orchestrator)
    {
        if (orchestrator.IsRunning)
            orchestrator.AbortAsync().GetAwaiter().GetResult();
    }
}
