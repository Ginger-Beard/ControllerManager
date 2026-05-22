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
        _acquisitionWatcherActive = false;
        Logger.Write($"[Orchestrator] RunFlow — profile='{profile.Name}' exe='{profile.GameExecutablePath}' trigger={profile.AcquisitionTrigger}");

        FirstDeviceAcquisitionWatcher? watcher = null;
        var hasExe = !string.IsNullOrWhiteSpace(profile.GameExecutablePath);

        try
        {
            HideDevices(profile, ct);
            ct.ThrowIfCancellationRequested();

            Process? gameProc = null;
            if (hasExe)
            {
                gameProc = await LaunchGame(profile, ct);
                ct.ThrowIfCancellationRequested();

                // Correct the HidHide deny-list entry with the game's actual kernel NT path.
                // No-op for normal C:\... paths (already correct); only opens the game process
                // when Win32ToNtPath couldn't resolve (UNC / WSL paths). Avoids touching
                // anti-cheat-protected processes unnecessarily.
                _hidHide.UpdateSessionGameNtPath(gameProc.Id);
            }
            else
            {
                // Sunshine/Apollo path: the streaming host launches the game,
                // CM is invoked just to hide/reveal. There's no game PID to
                // wait on or to scope the ETW watcher to — the flow ends when
                // something calls --restore (Sunshine Undo) or the user clicks
                // Restore in the Dashboard / End Session in the tray.
                Log("No game executable configured — hide/reveal-only mode (Sunshine/Apollo). " +
                    "Fire --restore (or click Restore on the Dashboard) to end this session.");
            }

            // Acquisition watcher needs a real game PID to filter ETW events.
            // Without one, Acquisition mode silently degrades to Timer mode for
            // the reveal phase (see RevealDisableThenRestore).
            if (gameProc is not null
                && profile.AcquisitionTrigger == AcquisitionTrigger.FirstDeviceOpened
                && profile.DisableThenRestore.Count > 0)
            {
                watcher = StartAcquisitionWatcher(profile, gameProc.Id);
                _acquisitionWatcherActive = watcher is not null;
            }
            else if (gameProc is not null && Logger.CurrentLevel >= LogLevel.Verbose)
            {
                // Pure observation when verbose+ logging is on: attach a watcher
                // to every HID device so the log shows exactly when (and by which
                // process) each one is opened. Doesn't drive any orchestrator
                // decisions; the signal fires harmlessly.
                watcher = StartDiagnosticWatcher(gameProc.Id);
            }
            else if (gameProc is null
                && profile.AcquisitionTrigger == AcquisitionTrigger.FirstDeviceOpened
                && profile.DisableThenRestore.Count > 0)
            {
                Log("Acquisition mode needs a game PID to listen on — falling back to Timer mode for this session. " +
                    "Set per-device T+Xs times to control reveal timing.");
            }

            if (profile.DisableThenRestore.Count > 0)
            {
                await RevealDisableThenRestore(profile, ct);
                ct.ThrowIfCancellationRequested();
            }

            if (gameProc is not null)
                await MonitorUntilExit(profile, gameProc, ct);
            else
                await WaitUntilCancelled(ct);
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

    // ── Acquisition watcher (optional, drives the wait phase in acquisition mode) ───

    private bool _firstDeviceAcquired;
    // True only when a real ETW watcher started for this session. When false,
    // RevealDisableThenRestore skips the 60s acquisition wait and runs Timer-
    // mode semantics directly — used for no-exe (Sunshine/Apollo) profiles
    // where there's no game PID to scope ETW to.
    private bool _acquisitionWatcherActive;

    /// <summary>
    /// Starts the ETW watcher and returns it. The watcher fires
    /// <see cref="FirstDeviceAcquisitionWatcher.Acquired"/> when the game's PID
    /// (or a named broker process — e.g. GameInputSvc for WGI titles) opens any
    /// HID device file matching one of the profile's Always-Visible devices.
    /// In acquisition mode, <c>RevealDisableThenRestore</c> blocks until the
    /// signal fires (up to 60s) and then applies the post-acquisition grace
    /// before per-device reveals begin.
    ///
    /// Returns null when the watcher can't be started (no resolvable Always-
    /// Visible paths, ETW session creation failed, etc.) — the reveal phase
    /// then falls back to Timer-mode semantics, treating per-row times as
    /// absolute seconds from reveal-phase start.
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
        bool started = false;
        try
        {
            watcher.Acquired += () => _firstDeviceAcquired = true;

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

            started = watcher.Start(gamePid, triggerDevices, diagnosticDevices, brokerProcessNames);
            if (!started)
                Log("Acquisition: ETW unavailable — per-device T+Xs values control timing.");
            else
                Log("Acquisition watcher started. Per-device T+Xs serve as safety net if ETW doesn't fire.");
        }
        finally
        {
            if (!started) watcher.Dispose();
        }
        return started ? watcher : null;
    }

    /// <summary>
    /// Verbose-mode observation watcher — runs only when Logger.CurrentLevel is
    /// Verbose AND no acquisition signal is needed for slot ordering. Watches
    /// every HID device on the system; logs every open with friendly name +
    /// PID + opener process + elapsed timestamp. The signal fires when the
    /// first watched device is opened but nothing listens for it.
    /// </summary>
    private FirstDeviceAcquisitionWatcher? StartDiagnosticWatcher(int gamePid)
    {
        var allDevices = _enumerator.GetAll(showAllHid: true);
        var devices    = new List<FirstDeviceAcquisitionWatcher.DeviceToWatch>();
        foreach (var d in allDevices)
        {
            if (string.IsNullOrEmpty(d.DeviceInterfacePath)) continue;
            devices.Add(new(d.DeviceInterfacePath, d.FriendlyName));
        }

        if (devices.Count == 0)
        {
            Log("Diagnostic watcher: no HID devices to observe.");
            return null;
        }

        var watcher = new FirstDeviceAcquisitionWatcher();
        bool started = false;
        try
        {
            var brokerProcessNames = new[] { "GameInputService", "GameInputSvc" };

            // All devices passed as triggers so the first-open log line includes
            // a "[Acquisition] Signal fired by ..." too — handy "winner" marker
            // even though no reveal loop is listening.
            //
            // logAllHidOpens=true: also log any HID-looking path the kernel reports
            // even when it doesn't match a resolved watched path — this is how we
            // discover path-format mismatches and broker pre-opens.
            started = watcher.Start(gamePid, devices, null, brokerProcessNames, logAllHidOpens: true);
            if (!started)
                Log("Diagnostic watcher: ETW unavailable.");
            else
                Log($"Diagnostic watcher: observing {devices.Count} HID device(s) (verbose logging only — no slot-fix effect).");
        }
        finally
        {
            if (!started) watcher.Dispose();
        }
        return started ? watcher : null;
    }

    // ── Phase 3: reveal devices one by one ───────────────────────────────────────
    //
    // Two modes, controlled by Profile.AcquisitionTrigger:
    //
    //   • Timer mode (checkbox OFF): each device's DelaySeconds is an ABSOLUTE
    //     time from when this phase started (effectively "seconds after game
    //     launch + hide setup"). Reveal fires at max(DelaySeconds, lastReveal)
    //     so list order is preserved even with non-monotonic user values.
    //
    //   • Acquisition mode (checkbox ON): the phase first waits for the ETW
    //     watcher to fire (with a 60s hard timeout fallback). Then a grace
    //     period (PostAcquisitionDelaySeconds) elapses. After that, each
    //     device's DelaySeconds is treated as an OFFSET added to the
    //     "grace ended" moment — so the user can stagger reveals after grace
    //     by setting per-row values (e.g. vJoy=0s, shifter=1s, handbrake=2s).
    //     Setting all rows to 0 makes them fire back-to-back at grace+0.
    //
    // The semantics of DelaySeconds intentionally differ between modes (absolute
    // in Timer, relative-to-grace in Acquisition). The "Explain this profile"
    // expander spells this out to the user.
    //
    // Fallback: if Acquisition mode is on and the signal never arrives within
    // 60 seconds, we log clearly and treat DelaySeconds as absolute for the
    // rest of the phase. That way Forza Horizon and other WGI titles (where
    // the signal can't fire because the broker pre-opened the device files)
    // don't leave the user without their devices — they just get the Timer-
    // mode behavior with a one-line warning to switch the checkbox off.

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

        // In Timer mode this stays 0 (per-row times are absolute). In Acquisition
        // mode, it's set to the moment grace ends — per-row times are then added
        // to this baseline.
        double phaseOffsetMs = 0;

        // ── If Acquisition mode is on AND a watcher is actually listening,
        //    wait for signal + grace upfront. The watcher flag is false for
        //    no-exe profiles (Sunshine/Apollo) where there's no game PID to
        //    scope ETW to — in that case skip straight to Timer-mode semantics
        //    instead of burning 60s waiting for a signal that can't fire.
        if (_acquisitionWatcherActive
            && profile.AcquisitionTrigger == AcquisitionTrigger.FirstDeviceOpened
            && profile.DisableThenRestore.Count > 0)
        {
            const int SignalTimeoutMs = 60_000;
            var deadline = DateTime.UtcNow.AddMilliseconds(SignalTimeoutMs);

            while (!_firstDeviceAcquired)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline) break;
                await Task.Delay(250, ct);
            }

            if (_firstDeviceAcquired)
            {
                var graceMs = (int)Math.Max(0, profile.PostAcquisitionDelaySeconds * 1000.0);
                if (graceMs > 0)
                {
                    Log($"Acquisition signal received — holding {profile.PostAcquisitionDelaySeconds:0.##}s grace before revealing.");
                    await Task.Delay(graceMs, ct);
                }
                else
                {
                    Log("Acquisition signal received — beginning reveals now.");
                }
                phaseOffsetMs = (DateTime.UtcNow - phaseStart).TotalMilliseconds;
            }
            else
            {
                Log($"WARNING: Acquisition signal did not fire within {SignalTimeoutMs / 1000}s. " +
                    "Falling back to per-row absolute times. " +
                    "Tip: for games using Windows GameInput (Forza Horizon, etc.), " +
                    "uncheck 'Wait until the game opens the first device' and set the per-row times directly.");
                // phaseOffsetMs stays 0 — Timer-mode semantics for the rest.
            }
        }

        // ── Per-device timer loop ───────────────────────────────────────────
        foreach (var dev in profile.DisableThenRestore)
        {
            ct.ThrowIfCancellationRequested();

            // Target = phase offset (0 in Timer, signal+grace in Acquisition)
            // + per-row delay, clamped so reveals stay monotonically ordered.
            var perRowMs   = Math.Max(0, dev.DelaySeconds * 1000.0);
            var targetAtMs = Math.Max(phaseOffsetMs + perRowMs, lastRevealAtMs);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var elapsedMs = (DateTime.UtcNow - phaseStart).TotalMilliseconds;
                var waitMs    = targetAtMs - elapsedMs;
                if (waitMs <= 0) break;

                // 250ms granularity is fine — we're no longer racing a signal,
                // just waiting for an absolute timestamp.
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

    // No-exe session-end: nothing to poll, just block until the orchestrator
    // is told to stop (--restore, Dashboard Restore button, tray End Session).
    private async Task WaitUntilCancelled(CancellationToken ct)
    {
        State = OrchestratorState.Monitoring;
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
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
