using System.Diagnostics;
using ControllerManager.Models;

namespace ControllerManager.Services;

public enum OrchestratorState
{
    Idle,
    HidingDevices,
    LaunchingGame,
    RestoringDevices,
    Monitoring,
}

/// <summary>
/// Drives a profile through hide → launch → wait → reveal → monitor → restore.
///
/// Pre-launch hiding uses HidHide's persistent blacklist (snapshot/restore around the
/// session). Post-launch reveal is timer-based: <see cref="Profile.InitialDelaySeconds"/>
/// before the first reveal, then each <see cref="DeviceRef.DelaySeconds"/> between
/// subsequent reveals.
///
/// HandleWatcher was removed in 2026-05 because its PROCESS_DUP_HANDLE +
/// handle-table enumeration pattern matches anti-cheat (EAC, BattlEye) signatures
/// for memory cheats and got us externally terminated. A fixed delay is functionally
/// equivalent — we just trade precision for safety.
/// </summary>
public sealed class LaunchOrchestrator : IDisposable
{
    private readonly HidHideClient    _hidHide;
    private readonly DeviceEnumerator _enumerator;

    private OrchestratorState        _state = OrchestratorState.Idle;
    private CancellationTokenSource? _cts;
    private Task?                    _flowTask;

    // Instance IDs added to the session blacklist at session start.
    // Used to compute the correct "remaining hidden" set as devices are revealed.
    private HashSet<string> _sessionHiddenIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The profile currently running; null when idle.</summary>
    public Profile? ActiveProfile { get; private set; }

    public OrchestratorState State
    {
        get => _state;
        private set { _state = value; StateChanged?.Invoke(this, value); }
    }

    public bool IsRunning => State != OrchestratorState.Idle;

    public event EventHandler<OrchestratorState>? StateChanged;
    public event EventHandler<string>?            ActivityLogged;

    public LaunchOrchestrator(HidHideClient hidHide, DeviceEnumerator? enumerator = null)
    {
        _hidHide    = hidHide;
        _enumerator = enumerator ?? new DeviceEnumerator();
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    public void Start(Profile profile)
    {
        if (IsRunning) return;
        _cts      = new CancellationTokenSource();
        _flowTask = Task.Run(() => RunFlow(profile, _cts.Token));
    }

    public async Task AbortAsync()
    {
        _cts?.Cancel();

        if (_flowTask is not null)
            try { await _flowTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }

        _hidHide.EndGameSession();
        State = OrchestratorState.Idle;
        Log("Session ended — devices restored.");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _flowTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
    }

    // ── State machine ────────────────────────────────────────────────────────────

    private async Task RunFlow(Profile profile, CancellationToken ct)
    {
        ActiveProfile = profile;
        Logger.Write($"[Orchestrator] RunFlow — profile='{profile.Name}' exe='{profile.GameExecutablePath}'");
        try
        {
            HideDevices(profile, ct);
            ct.ThrowIfCancellationRequested();

            var gameProc = await LaunchGame(profile, ct);
            ct.ThrowIfCancellationRequested();

            // Correct the HidHide deny-list entry with the game's actual kernel NT path.
            // No-op for normal C:\... paths (already correct); only opens the game process
            // when Win32ToNtPath couldn't resolve (UNC / WSL paths). Avoids touching
            // anti-cheat-protected processes unnecessarily.
            _hidHide.UpdateSessionGameNtPath(gameProc.Id);

            if (profile.DisableThenRestore.Count > 0)
            {
                await RevealDisableThenRestore(profile, ct);
                ct.ThrowIfCancellationRequested();
            }

            await MonitorUntilExit(profile, gameProc, ct);
        }
        catch (OperationCanceledException)
        {
            Log("Flow cancelled.");
        }
        catch (Exception ex)
        {
            Logger.WriteException("Orchestrator.RunFlow", ex);
            Log($"Error: {ex.Message}");
        }
        finally
        {
            // Always clean up the HidHide session — even if the flow threw or was cancelled
            // mid-way (e.g. game launch timeout). Prevents stale session blacklist state.
            _hidHide.EndGameSession();
            ActiveProfile = null;
            State = OrchestratorState.Idle;
        }
    }

    // ── Phase 1: hide devices ─────────────────────────────────────────────────

    private void HideDevices(Profile profile, CancellationToken ct)
    {
        State = OrchestratorState.HidingDevices;

        if (!_hidHide.IsAvailable)
        {
            Log("Warning: HidHide is not installed — devices will not be hidden.");
            return;
        }

        // Hide ALL gaming devices except those explicitly kept visible.
        // Unassigned devices, Reveal-After-Start, and Always-Hidden are all hidden.
        var keepIds = profile.KeepEnabled
            .Select(d => d.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allDevices = _enumerator.GetAll(showAllHid: false);
        var toHide     = allDevices
            .Where(d => !keepIds.Contains(d.InstanceId))
            .Select(d => d.InstanceId)
            .ToList();

        if (toHide.Count == 0)
        {
            Log("No devices to hide.");
            _sessionHiddenIds.Clear();
            return;
        }

        _sessionHiddenIds = new HashSet<string>(toHide, StringComparer.OrdinalIgnoreCase);

        Log($"Hiding {toHide.Count} device(s) (all except {keepIds.Count} always-visible)...");
        _hidHide.BeginGameSession(toHide, keepIds, profile.GameExecutablePath);

        foreach (var d in allDevices.Where(d => toHide.Contains(d.InstanceId)))
            Logger.WriteVerbose($"[Orchestrator]   Hidden: {d.FriendlyName}");
    }

    // ── Phase 2: launch ──────────────────────────────────────────────────────────

    private async Task<Process> LaunchGame(Profile profile, CancellationToken ct)
    {
        State = OrchestratorState.LaunchingGame;
        Log($"Launching {profile.Name}...");

        var launched = Process.Start(new ProcessStartInfo
        {
            FileName        = profile.GameExecutablePath,
            UseShellExecute = true,
        });
        Logger.WriteVerbose($"[Orchestrator] Process.Start returned {(launched is null ? "null (shell launch)" : launched.Id.ToString())}");

        var procName = ProcessName(profile.GameExecutableName);
        Log($"Waiting for {procName}.exe...");

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var procs = Process.GetProcessesByName(procName);
            if (procs.Length > 0)
            {
                for (int i = 1; i < procs.Length; i++) procs[i].Dispose();
                Log($"Found {procName}.exe (PID {procs[0].Id})");
                return procs[0];
            }
            await Task.Delay(500, ct);
        }

        throw new TimeoutException($"'{procName}.exe' did not start within 60 seconds.");
    }

    // ── Phase 3: reveal devices one by one ───────────────────────────────────────
    //
    // DeviceRef.DelaySeconds is now an ABSOLUTE time from when this phase started
    // (effectively "seconds after game launch + hide-setup"). The first device's
    // value gates FFB-sensitive games: the wheel has time to claim slot #1 before
    // any other device is revealed. List order is the reveal order; if a device's
    // delay is less than the previous one's actual reveal time, it reveals
    // immediately after the previous (clamped).

    private async Task RevealDisableThenRestore(Profile profile, CancellationToken ct)
    {
        State = OrchestratorState.RestoringDevices;
        Log($"Revealing {profile.DisableThenRestore.Count} device(s) in order...");

        var revealed   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phaseStart = DateTime.UtcNow;
        var lastRevealAtS = 0; // seconds since phaseStart of the most recent reveal

        foreach (var dev in profile.DisableThenRestore)
        {
            ct.ThrowIfCancellationRequested();

            // Target time = max(device's configured time, time of previous reveal).
            // Then wait until target.
            var targetAtS = Math.Max(Math.Max(0, dev.DelaySeconds), lastRevealAtS);
            var elapsedS  = (int)(DateTime.UtcNow - phaseStart).TotalSeconds;
            var waitS     = targetAtS - elapsedS;

            if (waitS > 0)
            {
                LogVerbose($"  Waiting {waitS}s (until T+{targetAtS}s) before revealing {dev.FriendlyName}...");
                await Task.Delay(waitS * 1000, ct);
            }

            Log($"  Revealing: {dev.FriendlyName}  (T+{targetAtS}s)");
            revealed.Add(dev.InstanceId);
            lastRevealAtS = targetAtS;

            // Remaining = everything that was hidden at session start, minus what's revealed so far.
            // This preserves unassigned and Always-Hidden devices as hidden throughout.
            var remaining = _sessionHiddenIds
                .Where(id => !revealed.Contains(id))
                .ToList();
            _hidHide.UpdateSessionBlacklist(remaining);
        }

        Log("All Reveal-After-Start devices revealed.");
    }

    // ── Phase 5: monitor until exit ──────────────────────────────────────────────

    private async Task MonitorUntilExit(Profile profile, Process gameProc, CancellationToken ct)
    {
        State = OrchestratorState.Monitoring;
        var procName = ProcessName(profile.GameExecutableName);
        Log($"Monitoring {procName}.exe...");

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { if (gameProc.HasExited) break; }
            catch { break; }
            await Task.Delay(1000, ct);
        }

        Log($"{procName}.exe exited.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string ProcessName(string executableName) =>
        executableName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);

    private void Log(string message) =>
        ActivityLogged?.Invoke(this, $"{DateTime.Now:HH:mm:ss}  {message}");

    private void LogVerbose(string message)
    {
        Logger.Write($"{DateTime.Now:HH:mm:ss}  {message}");
        if (Logger.CurrentLevel >= LogLevel.Verbose)
            ActivityLogged?.Invoke(this, $"{DateTime.Now:HH:mm:ss}  {message}");
    }
}
