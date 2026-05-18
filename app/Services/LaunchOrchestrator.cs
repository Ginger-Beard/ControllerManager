using System.Diagnostics;
using System.Text.RegularExpressions;
using HIDReorder.Models;

namespace HIDReorder.Services;

public enum OrchestratorState
{
    Idle,
    DisablingDevices,
    LaunchingGame,
    WaitingForAcquisition,
    RestoringDevices,
    Monitoring,
}

public sealed class LaunchOrchestrator : IDisposable
{
    private static readonly Regex VidPidRx =
        new(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly StateStore    _stateStore;
    private readonly HandleWatcher _watcher = new();

    private OrchestratorState _state = OrchestratorState.Idle;
    private CancellationTokenSource? _cts;
    private Task? _flowTask;

    public OrchestratorState State
    {
        get => _state;
        private set { _state = value; StateChanged?.Invoke(this, value); }
    }

    public bool IsRunning => State != OrchestratorState.Idle;

    public event EventHandler<OrchestratorState>? StateChanged;
    public event EventHandler<string>?            ActivityLogged;

    public LaunchOrchestrator(StateStore stateStore)
    {
        _stateStore = stateStore;
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    public void Start(Profile profile)
    {
        if (IsRunning) return;
        _cts      = new CancellationTokenSource();
        _flowTask = Task.Run(() => RunFlow(profile, _cts.Token));
    }

    public void Abort()
    {
        _cts?.Cancel();
        _watcher.Stop();
        try { _flowTask?.Wait(3000); } catch { }
        _stateStore.RestoreAll();
        State = OrchestratorState.Idle;
        Log("Aborted — all devices restored.");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _watcher.Dispose();
    }

    // ── State machine ────────────────────────────────────────────────────────────

    private async Task RunFlow(Profile profile, CancellationToken ct)
    {
        Logger.Write($"[Orchestrator] RunFlow started — profile='{profile.Name}' exe='{profile.GameExecutablePath}' trigger={profile.TriggerMode}");
        try
        {
            DisableDevices(profile, ct);
            ct.ThrowIfCancellationRequested();

            var gameProc = await LaunchGame(profile, ct);
            ct.ThrowIfCancellationRequested();

            if (profile.DisableThenRestore.Count > 0)
            {
                _watcher.Start(gameProc.Id);

                await WaitForAcquisition(profile, ct);
                ct.ThrowIfCancellationRequested();

                await RestoreDevices(profile, ct);
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
            _watcher.Stop();
            State = OrchestratorState.Idle;
        }
    }

    // ── Phase 1: disable ─────────────────────────────────────────────────────────

    private void DisableDevices(Profile profile, CancellationToken ct)
    {
        State = OrchestratorState.DisablingDevices;
        var toDisable = profile.DisableThenRestore.Concat(profile.KeepDisabled).ToList();
        if (toDisable.Count == 0) return;

        Log($"Disabling {toDisable.Count} device(s)...");
        foreach (var dev in toDisable)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _stateStore.RecordDisabledRef(dev);
                DeviceController.SetEnabledById(dev.InstanceId, false);
                Log($"  Disabled: {dev.FriendlyName}");
            }
            catch (Exception ex)
            {
                Log($"  Warning: could not disable {dev.FriendlyName} — {ex.Message}");
            }
        }
    }

    // ── Phase 2: launch ──────────────────────────────────────────────────────────

    private async Task<Process> LaunchGame(Profile profile, CancellationToken ct)
    {
        State = OrchestratorState.LaunchingGame;
        Log($"Launching {profile.Name}...");

        Process.Start(new ProcessStartInfo
        {
            FileName        = profile.GameExecutablePath,
            UseShellExecute = true,
        });

        var procName = ProcessName(profile.GameExecutableName);
        Log($"Waiting for {procName}.exe...");

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var procs = Process.GetProcessesByName(procName);
            if (procs.Length > 0)
            {
                Log($"Found {procName}.exe (PID {procs[0].Id})");
                return procs[0];
            }
            await Task.Delay(500, ct);
        }

        throw new TimeoutException($"'{procName}.exe' did not start within 60 seconds.");
    }

    // ── Phase 3: wait for FFB/acquisition ────────────────────────────────────────

    private async Task WaitForAcquisition(Profile profile, CancellationToken ct)
    {
        State = OrchestratorState.WaitingForAcquisition;

        if (profile.TriggerMode == TriggerMode.Timer)
        {
            Log($"Waiting {profile.TimerSeconds}s for game to acquire devices...");
            await Task.Delay(profile.TimerSeconds * 1000, ct);
            Log("Timer elapsed — starting re-enable.");
            return;
        }

        // HandleWatcher: wait for first device event (only KeepEnabled devices visible at this point)
        Log("Watching for game to acquire wheel (first DirectInput/HID event)...");
        var ev = await WaitForAnyDeviceEvent(timeoutMs: 120_000, ct);
        if (ev is not null)
            Log($"Acquisition signal: {ev.DevicePath}");
        else
            LogVerbose("Acquisition timeout — proceeding anyway.");
    }

    // ── Phase 4: restore devices one by one ──────────────────────────────────────

    private async Task RestoreDevices(Profile profile, CancellationToken ct)
    {
        State = OrchestratorState.RestoringDevices;
        Log($"Re-enabling {profile.DisableThenRestore.Count} device(s) in order...");

        foreach (var dev in profile.DisableThenRestore)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                Log($"  Enabling: {dev.FriendlyName}");
                DeviceController.SetEnabledById(dev.InstanceId, true);
                _stateStore.ClearEnabledById(dev.InstanceId);
            }
            catch (Exception ex)
            {
                Log($"  Warning: could not enable {dev.FriendlyName} — {ex.Message}");
                continue;
            }

            // Wait for the game to acknowledge this device
            if (profile.TriggerMode == TriggerMode.HandleWatcher)
            {
                var (vid, pid) = ParseVidPid(dev.InstanceId);
                if (vid is not null)
                {
                    var ev = await WaitForDeviceEvent(vid, pid!, profile.HandleWatcherStepTimeoutMs, ct);
                    if (ev is not null)
                        LogVerbose($"    Game acknowledged {dev.FriendlyName}");
                    else
                        LogVerbose($"    Timeout — advancing to next device");
                }
                else
                {
                    await Task.Delay(profile.HandleWatcherStepTimeoutMs, ct);
                }
            }
        }

        Log("All Disable→Restore devices re-enabled.");
    }

    // ── Phase 5: monitor until exit ──────────────────────────────────────────────

    private async Task MonitorUntilExit(Profile profile, Process gameProc, CancellationToken ct)
    {
        State = OrchestratorState.Monitoring;
        var procName = ProcessName(profile.GameExecutableName);
        Log($"Monitoring {procName}.exe — will restore Keep-Disabled devices on exit.");

        while (!gameProc.HasExited)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);
        }

        Log($"{procName}.exe exited.");

        if (profile.KeepDisabled.Count > 0)
        {
            Log($"Re-enabling {profile.KeepDisabled.Count} Keep-Disabled device(s)...");
            foreach (var dev in profile.KeepDisabled)
            {
                try
                {
                    DeviceController.SetEnabledById(dev.InstanceId, true);
                    _stateStore.ClearEnabledById(dev.InstanceId);
                    Log($"  Enabled: {dev.FriendlyName}");
                }
                catch (Exception ex)
                {
                    Log($"  Warning: could not enable {dev.FriendlyName} — {ex.Message}");
                }
            }
        }
    }

    // ── HandleWatcher helpers ────────────────────────────────────────────────────

    private Task<HidHandleEvent?> WaitForAnyDeviceEvent(int timeoutMs, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<HidHandleEvent?>();

        EventHandler<HidHandleEvent> handler = null!;
        handler = (_, e) =>
        {
            _watcher.HidHandleOpened -= handler;
            tcs.TrySetResult(e);
        };
        _watcher.HidHandleOpened += handler;

        Task.Delay(timeoutMs, ct).ContinueWith(_ =>
        {
            _watcher.HidHandleOpened -= handler;
            tcs.TrySetResult(null);
        });

        return tcs.Task;
    }

    private Task<HidHandleEvent?> WaitForDeviceEvent(string vid, string pid, int timeoutMs, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<HidHandleEvent?>();

        EventHandler<HidHandleEvent> handler = null!;
        handler = (_, e) =>
        {
            var m = VidPidRx.Match(e.DevicePath);
            if (m.Success &&
                m.Groups[1].Value.Equals(vid, StringComparison.OrdinalIgnoreCase) &&
                m.Groups[2].Value.Equals(pid, StringComparison.OrdinalIgnoreCase))
            {
                _watcher.HidHandleOpened -= handler;
                tcs.TrySetResult(e);
            }
        };
        _watcher.HidHandleOpened += handler;

        Task.Delay(timeoutMs, ct).ContinueWith(_ =>
        {
            _watcher.HidHandleOpened -= handler;
            tcs.TrySetResult(null);
        });

        return tcs.Task;
    }

    private static string ProcessName(string executableName) =>
        executableName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);

    private static (string? vid, string? pid) ParseVidPid(string instanceId)
    {
        var m = VidPidRx.Match(instanceId);
        if (!m.Success) return (null, null);
        return (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant());
    }

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        ActivityLogged?.Invoke(this, line);
    }

    private void LogVerbose(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        Logger.Write(line);
        if (Logger.CurrentLevel >= LogLevel.Verbose)
            ActivityLogged?.Invoke(this, line);
    }
}
