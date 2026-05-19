using System.Diagnostics;
using System.Text.RegularExpressions;
using ControllerManager.Models;

namespace ControllerManager.Services;

public enum OrchestratorState
{
    Idle,
    HidingDevices,
    LaunchingGame,
    WaitingForAcquisition,
    RestoringDevices,
    Monitoring,
}

public sealed class LaunchOrchestrator : IDisposable
{
    private static readonly Regex VidPidRx =
        new(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HidHideClient _hidHide;
    private readonly HandleWatcher _watcher = new();

    private OrchestratorState _state = OrchestratorState.Idle;
    private CancellationTokenSource? _cts;
    private Task? _flowTask;

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

    public LaunchOrchestrator(HidHideClient hidHide)
    {
        _hidHide = hidHide;
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
        _watcher.Stop();

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
        _watcher.Dispose();
    }

    // ── State machine ────────────────────────────────────────────────────────────

    private async Task RunFlow(Profile profile, CancellationToken ct)
    {
        ActiveProfile = profile;
        Logger.Write($"[Orchestrator] RunFlow — profile='{profile.Name}' exe='{profile.GameExecutablePath}' trigger={profile.TriggerMode}");
        try
        {
            HideDevices(profile, ct);
            ct.ThrowIfCancellationRequested();

            var gameProc = await LaunchGame(profile, ct);
            ct.ThrowIfCancellationRequested();

            if (profile.DisableThenRestore.Count > 0)
            {
                _watcher.Start(gameProc.Id);

                await WaitForAcquisition(profile, ct);
                ct.ThrowIfCancellationRequested();

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
            _watcher.Stop();
            ActiveProfile = null;
            State = OrchestratorState.Idle;
        }
    }

    // ── Phase 1: hide devices ─────────────────────────────────────────────────

    private void HideDevices(Profile profile, CancellationToken ct)
    {
        State = OrchestratorState.HidingDevices;
        var toHide = profile.DisableThenRestore.Concat(profile.KeepDisabled).ToList();
        if (toHide.Count == 0) return;

        if (!_hidHide.IsAvailable)
        {
            Log("Warning: HidHide is not installed — devices will not be hidden.");
            return;
        }

        Log($"Hiding {toHide.Count} device(s)...");
        _hidHide.BeginGameSession(toHide.Select(d => d.InstanceId), profile.GameExecutablePath);
        foreach (var dev in toHide)
            Log($"  Hidden: {dev.FriendlyName}");
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

    // ── Phase 3: wait for acquisition ────────────────────────────────────────────

    private async Task WaitForAcquisition(Profile profile, CancellationToken ct)
    {
        State = OrchestratorState.WaitingForAcquisition;

        if (profile.TriggerMode == TriggerMode.Timer)
        {
            Log($"Waiting {profile.TimerSeconds}s for game to acquire devices...");
            await Task.Delay(profile.TimerSeconds * 1000, ct);
            Log("Timer elapsed — revealing devices.");
            return;
        }

        Log("Watching for game to acquire first device (HandleWatcher)...");
        var ev = await WaitForAnyDeviceEvent(timeoutMs: 120_000, ct);
        if (ev is not null)
            Log($"Acquisition signal: {ev.DevicePath}");
        else
            LogVerbose("Acquisition timeout — proceeding anyway.");
    }

    // ── Phase 4: reveal DisableThenRestore devices one by one ────────────────────

    private async Task RevealDisableThenRestore(Profile profile, CancellationToken ct)
    {
        State = OrchestratorState.RestoringDevices;
        Log($"Revealing {profile.DisableThenRestore.Count} device(s) in order...");

        var revealed   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keepHidden = profile.KeepDisabled.Select(d => d.InstanceId).ToList();

        foreach (var dev in profile.DisableThenRestore)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                Log($"  Revealing: {dev.FriendlyName}");
                revealed.Add(dev.InstanceId);

                var remaining = profile.DisableThenRestore
                    .Where(d => !revealed.Contains(d.InstanceId))
                    .Select(d => d.InstanceId)
                    .Concat(keepHidden)
                    .ToList();
                _hidHide.UpdateSessionBlacklist(remaining);
            }
            catch (Exception ex)
            {
                Log($"  Warning: {dev.FriendlyName} — {ex.Message}");
                continue;
            }

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

            if (dev.DelaySeconds > 0)
                await Task.Delay(dev.DelaySeconds * 1000, ct);
        }

        Log("All Disable→Restore devices revealed.");
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
        _hidHide.EndGameSession();
        Log("Session ended — devices restored.");
    }

    // ── HandleWatcher helpers ────────────────────────────────────────────────────

    private Task<HidHandleEvent?> WaitForAnyDeviceEvent(int timeoutMs, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<HidHandleEvent?>();

        EventHandler<HidHandleEvent> handler = null!;
        handler = (_, e) => { _watcher.HidHandleOpened -= handler; tcs.TrySetResult(e); };
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

    private void Log(string message) =>
        ActivityLogged?.Invoke(this, $"{DateTime.Now:HH:mm:ss}  {message}");

    private void LogVerbose(string message)
    {
        Logger.Write($"{DateTime.Now:HH:mm:ss}  {message}");
        if (Logger.CurrentLevel >= LogLevel.Verbose)
            ActivityLogged?.Invoke(this, $"{DateTime.Now:HH:mm:ss}  {message}");
    }
}
