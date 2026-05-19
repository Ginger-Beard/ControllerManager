using System.Diagnostics;
using ControllerManager.Models;

namespace ControllerManager.Services;

/// <summary>
/// Background safety-net: detects game launches that didn't go through
/// the Launch button, shortcut, or Steam wrapper, and triggers the flow.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly ProfileStore       _profiles;
    private readonly LaunchOrchestrator _orchestrator;
    private readonly HashSet<int>       _handledPids = [];
    private CancellationTokenSource?    _cts;
    private Task?                       _task;

    public bool IsRunning { get; private set; }

    public ProcessWatcher(ProfileStore profiles, LaunchOrchestrator orchestrator)
    {
        _profiles     = profiles;
        _orchestrator = orchestrator;
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts  = new CancellationTokenSource();
        _task = Task.Run(() => PollLoop(_cts.Token));
        IsRunning = true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(2000); } catch { }
        IsRunning = false;
    }

    public void Dispose() => Stop();

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch { }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private void Tick()
    {
        var profiles = _profiles.Load()
            .Where(p => p.ProcessWatcherEnabled && !string.IsNullOrEmpty(p.GameExecutableName))
            .ToList();

        if (_orchestrator.IsRunning)
        {
            // While a flow is running, mark any matching game PIDs as handled so
            // they don't re-trigger when the flow stops or is aborted.
            foreach (var profile in profiles)
            {
                var exeName = profile.GameExecutableName
                    .Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
                foreach (var proc in Process.GetProcessesByName(exeName))
                    _handledPids.Add(proc.Id);
            }
            return;
        }

        foreach (var profile in profiles)
        {
            var exeName = profile.GameExecutableName
                .Replace(".exe", "", StringComparison.OrdinalIgnoreCase);

            var procs = Process.GetProcessesByName(exeName);
            foreach (var proc in procs)
            {
                if (_handledPids.Contains(proc.Id)) continue;
                _handledPids.Add(proc.Id);
                _orchestrator.Start(profile);
                return;
            }
        }

        // Prune dead PIDs so they can re-trigger if game restarts
        _handledPids.RemoveWhere(pid =>
        {
            try { Process.GetProcessById(pid); return false; }
            catch { return true; }
        });
    }
}
