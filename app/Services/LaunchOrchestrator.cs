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
        ActiveProfile             = profile;
        _firstDeviceAcquired      = false;
        _firstDeviceAcquiredAtMs  = 0;
        Logger.Write($"[Orchestrator] RunFlow — profile='{profile.Name}' exe='{profile.GameExecutablePath}' trigger={profile.AcquisitionTrigger}");

        FirstDeviceAcquisitionWatcher? watcher = null;

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

            // In acquisition mode, start the ETW watcher *concurrent* with the reveal
            // phase. The watcher signal short-circuits the per-device T+Xs waits when
            // it fires. If the signal never fires (game uses RawInput/WGI, no file
            // open observable), the reveal phase still proceeds using the user's
            // per-device T+Xs as the safety net — that's why those fields are kept
            // in the UI in both modes.
            if (profile.AcquisitionTrigger == AcquisitionTrigger.FirstDeviceOpened
                && profile.DisableThenRestore.Count > 0)
            {
                watcher = StartAcquisitionWatcher(profile, gameProc.Id);
            }

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
            try { watcher?.Stop(); watcher?.Dispose(); } catch { }
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

        // Profile references store a single instance ID per DeviceRef. Expand to all
        // sibling interfaces of the same physical device — composite controllers with
        // multiple HID interfaces (MI_00 + MI_01 etc) need every child explicitly
        // hidden, since HidHide's kernel filter does direct string compare.
        //
        // Enumerate the FULL HID device set, not just the strict gaming-class filter.
        // The strict filter (UsagePage 0x05 or UsagePage 0x01+Usage 0x04/0x05) misses
        // devices declared with vendor-defined pages, multi-axis usage (0x08), or
        // other off-spec descriptors — but games will happily treat any of those as
        // a "controller" and assign them a slot. If those weren't in our hide list,
        // they stayed visible at game launch and could grab slot #1 ahead of the
        // configured AlwaysVisible device.
        //
        // We then filter out keyboards and mice (no game will assign them as a
        // controller slot, and hiding text input mid-game would be catastrophic)
        // and devices with no inputs (Lian Li fans, audio devices, USB hubs that
        // present as HID — can't compete for a controller slot anyway).
        var allDevices = _enumerator.GetAll(showAllHid: true);

        var keepIds = ExpandToChildren(profile.KeepEnabled.Select(d => d.InstanceId), allDevices);

        var toHide = allDevices
            .Where(d => !keepIds.Contains(d.InstanceId))
            .Where(d => !d.IsKeyboardOrMouse)
            .Where(d => d.AxisCount > 0 || d.ButtonCount > 0) // skip devices with no inputs
            .SelectMany(d => d.ChildInstanceIds.Count > 0 ? d.ChildInstanceIds : [d.InstanceId])
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

        foreach (var d in allDevices.Where(d => !keepIds.Contains(d.InstanceId)))
            Logger.WriteVerbose($"[Orchestrator]   Hidden: {d.FriendlyName}");
    }

    // Given a set of "primary" instance IDs (as stored in the profile), expand to the
    // full set of sibling interface IDs for each matching device. IDs that don't match
    // any current device are passed through unchanged (so profiles with stale IDs still
    // get applied even when ProfileHealer hasn't run).
    private static HashSet<string> ExpandToChildren(
        IEnumerable<string> primaryIds, List<HidDevice> allDevices)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var primary in primaryIds)
        {
            var match = allDevices.FirstOrDefault(d =>
                d.InstanceId.Equals(primary, StringComparison.OrdinalIgnoreCase) ||
                d.ChildInstanceIds.Contains(primary, StringComparer.OrdinalIgnoreCase));

            if (match is not null && match.ChildInstanceIds.Count > 0)
                foreach (var child in match.ChildInstanceIds) result.Add(child);
            else
                result.Add(primary);
        }
        return result;
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

    // ── Acquisition watcher (optional, runs concurrent with reveal phase) ───────

    private bool   _firstDeviceAcquired;
    private double _firstDeviceAcquiredAtMs;   // ms since reveal phase started

    /// <summary>
    /// Starts the ETW watcher and returns it. The watcher fires
    /// <see cref="FirstDeviceAcquisitionWatcher.Acquired"/> when the game's PID
    /// opens any HID device file matching one of the profile's Always-Visible
    /// devices. The reveal loop short-circuits its per-device T+Xs wait when
    /// the signal arrives.
    ///
    /// Returns null when the watcher can't be started (no resolvable Always-
    /// Visible paths, ETW session creation failed, etc.) — the reveal phase
    /// then operates as pure Timer mode using the user's per-device T+Xs.
    /// </summary>
    private FirstDeviceAcquisitionWatcher? StartAcquisitionWatcher(Profile profile, int gamePid)
    {
        var allDevices       = _enumerator.GetAll(showAllHid: true);
        var alwaysVisible    = profile.KeepEnabled.Select(d => d.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var revealAfterStart = profile.DisableThenRestore.Select(d => d.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var triggerDevices    = new List<FirstDeviceAcquisitionWatcher.DeviceToWatch>();
        var diagnosticDevices = new List<FirstDeviceAcquisitionWatcher.DeviceToWatch>();

        foreach (var d in allDevices)
        {
            if (string.IsNullOrEmpty(d.DeviceInterfacePath)) continue;
            if (alwaysVisible.Contains(d.InstanceId))
                triggerDevices.Add(new(d.DeviceInterfacePath, d.FriendlyName));
            else if (revealAfterStart.Contains(d.InstanceId))
                diagnosticDevices.Add(new(d.DeviceInterfacePath, d.FriendlyName));
        }

        if (triggerDevices.Count == 0)
        {
            Log("Acquisition: no Always-Visible devices to watch — per-device T+Xs values control timing.");
            return null;
        }

        var watcher = new FirstDeviceAcquisitionWatcher();

        // Capture the moment of the signal. The reveal loop tracks
        // _firstDeviceAcquiredAtMs relative to its own phaseStart, so we record
        // wall-clock ticks here and let the reveal loop convert.
        var startTicks = DateTime.UtcNow.Ticks;
        watcher.Acquired += () =>
        {
            var elapsedMs = (DateTime.UtcNow.Ticks - startTicks) / TimeSpan.TicksPerMillisecond;
            _firstDeviceAcquiredAtMs = elapsedMs;
            _firstDeviceAcquired     = true;
            Logger.Write($"[Acquisition] Signal fired at watcher-start + {elapsedMs}ms");
        };

        // Modern Xbox/UWP titles open HID files via a system broker rather
        // than from the game's own process. Two confirmed broker binaries:
        //   - GameInputService.exe — original WGI broker (built into Windows)
        //   - GameInputSvc.exe     — newer GDK GameInput Host Service
        //                            (C:\Program Files (x86)\Microsoft GameInput\x64)
        // ETW ProcessName strips the .exe suffix; match is case-insensitive.
        //
        // Intentionally NOT included (would cause false positives):
        //   - Steam.exe / gameoverlayui.exe — Steam Input opens devices at
        //     Steam launch (i.e. always-on), not at game launch. Signal would
        //     fire instantly on profile start.
        //   - Companion apps (Razer Synapse, G HUB, SimHub, etc.) — they
        //     open devices for config/telemetry, not on-behalf-of a game.
        //   - GamingServices(Net).exe — MS Store package management, not input.
        //   - svchost.exe / system processes — HID class enumeration noise.
        //
        // Legacy DirectInput / XInput / RawInput games (Richard Burns Rally,
        // GTR2, rFactor, Live for Speed, anything pre-WGI) open HID files
        // directly from the game's own PID — handled by the gamePid match,
        // no broker needed.
        var brokerProcessNames = new[] { "GameInputService", "GameInputSvc" };

        if (!watcher.Start(gamePid, triggerDevices, diagnosticDevices, brokerProcessNames))
        {
            Log("Acquisition: ETW unavailable — per-device T+Xs values control timing.");
            watcher.Dispose();
            return null;
        }

        Log("Acquisition watcher started. Per-device T+Xs serve as safety net if ETW doesn't fire.");
        return watcher;
    }

    // ── Phase 3: reveal devices one by one ───────────────────────────────────────
    //
    // DeviceRef.DelaySeconds is an ABSOLUTE time from when this phase started
    // (effectively "seconds after game launch + hide-setup"). Two paths through
    // the same loop:
    //
    //   • Timer mode (no acquisition signal): each reveal waits until T+Xs, then
    //     fires. List order + clamping ensures monotonically increasing reveals.
    //
    //   • Acquisition signal short-circuit: while we're waiting on a device's
    //     T+Xs, if the ETW watcher fires, we cut the wait short, apply the post-
    //     acquisition grace period once, then fire all remaining reveals
    //     back-to-back. The user's T+Xs values become a safety net — if ETW
    //     never fires (game uses RawInput/WGI/etc.), the reveals still happen
    //     at the configured times rather than running 30s late.

    private async Task RevealDisableThenRestore(Profile profile, CancellationToken ct)
    {
        State = OrchestratorState.RestoringDevices;
        Log($"Revealing {profile.DisableThenRestore.Count} device(s) in order...");

        // Re-enumerate so we can expand each DeviceRef.InstanceId to all of its
        // sibling HID interfaces — composite devices need every MI_NN child revealed.
        // Use showAllHid: true so the expansion can resolve siblings even for
        // non-gaming-class devices that HideDevices may have hidden.
        var allDevices = _enumerator.GetAll(showAllHid: true);

        var revealed       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phaseStart     = DateTime.UtcNow;
        double lastRevealAtMs = 0;
        bool   acquisitionHandled = false; // true once we've applied the post-signal grace

        foreach (var dev in profile.DisableThenRestore)
        {
            ct.ThrowIfCancellationRequested();

            double targetAtMs;
            if (_firstDeviceAcquired && acquisitionHandled)
            {
                // Signal already fired and grace applied — pack remaining reveals
                // back-to-back, paced only by IOCTL latency.
                targetAtMs = lastRevealAtMs;
            }
            else
            {
                // Either no signal yet (or we're checking before it arrives), or
                // signal just fired and we haven't yet applied the grace period.
                // Default target = max(user T+Xs, previous reveal).
                targetAtMs = Math.Max(Math.Max(0, dev.DelaySeconds * 1000.0), lastRevealAtMs);
            }

            // Wait until target — but ALSO wake immediately if acquisition fires
            // during the wait. Poll _firstDeviceAcquired in a short loop instead
            // of one big Task.Delay so we can react to the signal without a
            // CancellationToken-style plumbing through the watcher.
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (_firstDeviceAcquired && !acquisitionHandled)
                {
                    // Signal just arrived. Apply the post-acquisition grace once,
                    // then short-circuit this device + everything after it.
                    var graceMs = (int)Math.Max(0, profile.PostAcquisitionDelaySeconds * 1000.0);
                    if (graceMs > 0)
                    {
                        Log($"Acquisition signal received — holding {profile.PostAcquisitionDelaySeconds:0.##}s grace before revealing remaining devices.");
                        await Task.Delay(graceMs, ct);
                    }
                    else
                    {
                        Log("Acquisition signal received — revealing remaining devices now.");
                    }
                    acquisitionHandled = true;
                    targetAtMs = (DateTime.UtcNow - phaseStart).TotalMilliseconds;
                    break;
                }

                var elapsedMs = (DateTime.UtcNow - phaseStart).TotalMilliseconds;
                var waitMs    = targetAtMs - elapsedMs;
                if (waitMs <= 0) break;

                // 250ms granularity: short enough to react to the ETW signal
                // promptly, long enough that we're not busy-looping.
                await Task.Delay(Math.Min(250, (int)waitMs), ct);
            }

            Log($"  Revealing: {dev.FriendlyName}  (T+{targetAtMs / 1000.0:0.##}s)");
            foreach (var id in ExpandToChildren([dev.InstanceId], allDevices))
                revealed.Add(id);
            lastRevealAtMs = targetAtMs;

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
