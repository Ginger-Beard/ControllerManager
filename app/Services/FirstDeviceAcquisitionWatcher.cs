using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace ControllerManager.Services;

/// <summary>
/// Watches for the moment a target game process opens its first HID device file.
///
/// Why this exists: many games (Forza Horizon, etc.) have a hard device-detection
/// window — they open up a 2-3 second period during which any HID device the game
/// can access gets a controller slot, and after the window closes, no further
/// devices are accepted. With a fixed-timer reveal, the user has to manually
/// discover both edges of that window per game per system, which is brittle.
///
/// This watcher subscribes to the kernel ETW file-create stream, filters by the
/// game's PID, and fires <see cref="Acquired"/> the first time it sees the game
/// open a HID device. That's the signal "the game is now inside its detection
/// window — reveal the rest of the devices NOW, tightly packed, before the
/// window closes."
///
/// EAC-safe: ETW is one-way kernel telemetry. We never touch the game's process,
/// handles, or memory — we just consume an event stream the kernel was emitting
/// anyway. Anti-cheats use ETW themselves; it's not a cheat vector.
///
/// Requirements:
///   - Admin (we have it)
///   - Windows 8.1+ for the modern Microsoft-Windows-Kernel-File provider
/// </summary>
public sealed class FirstDeviceAcquisitionWatcher : IDisposable
{
    // The session name must be globally unique on the system; we use a stable
    // string so we can find/clean up stale sessions on next launch.
    private const string SessionName = "ControllerManager_DeviceAcquisition";

    private TraceEventSession?       _session;
    private Task?                    _processTask;
    private int                      _targetPid = -1;
    private int                      _fired;     // 0/1; flips to 1 the first time Acquired fires

    /// <summary>
    /// Fires exactly once per <see cref="Start"/>, on the first kernel file-create
    /// event for a HID device matching <paramref name="targetPid"/>. Dispatched on
    /// the ETW worker thread — the orchestrator marshals to the right context.
    /// </summary>
    public event Action? Acquired;

    /// <summary>
    /// Subscribe to the kernel file-create stream filtered for the game's PID
    /// opening any HID device. Returns true if the ETW session started cleanly;
    /// false on failure (in which case the caller should fall back to the
    /// timer-based behavior).
    /// </summary>
    public bool Start(int targetPid)
    {
        if (_session != null)
            throw new InvalidOperationException("FirstDeviceAcquisitionWatcher already started");

        _targetPid = targetPid;
        _fired     = 0;

        try
        {
            // Clean up any leftover session from a previous crashed run before
            // creating a new one with the same name (ETW refuses duplicates).
            try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); } catch { }

            _session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true,
            };

            // Modern kernel providers (Win8.1+) — coexist with other ETW sessions
            // unlike the legacy NT Kernel Logger, which is a system singleton.
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit);

            _session.Source.Kernel.FileIOCreate += OnFileCreate;

            // Source.Process is blocking; run on a background thread. The session
            // is stopped via Stop() / Dispose() which unblocks Process().
            _processTask = Task.Run(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex)
                {
                    Logger.WriteException("ETW Source.Process", ex);
                }
            });

            Logger.WriteVerbose($"[Acquisition] Watching PID {targetPid} for HID device opens");
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteException("FirstDeviceAcquisitionWatcher.Start", ex);
            CleanupSession();
            return false;
        }
    }

    private void OnFileCreate(FileIOCreateTraceData data)
    {
        if (_targetPid < 0 || data.ProcessID != _targetPid) return;

        var path = data.FileName;
        if (string.IsNullOrEmpty(path)) return;

        // Filter: kernel-mode HID device opens. Modern ETW reports the NT object
        // path; HID devices live under \Device\<something HID-ish>. The exact
        // format varies (raw HID is "\Device\HID00000###", but newer Windows
        // sometimes uses different naming). Matching on the substring "HID"
        // anywhere in the path catches both shapes and isn't slow enough to
        // matter at the event rate we care about.
        if (path.IndexOf("HID", StringComparison.OrdinalIgnoreCase) < 0) return;

        // First match only; subsequent events ignored. Interlocked guards against
        // racing on the ETW worker thread if multiple opens arrive in the same tick.
        if (Interlocked.Exchange(ref _fired, 1) != 0) return;

        Logger.Write($"[Acquisition] Game opened HID device: {path}");
        try { Acquired?.Invoke(); } catch (Exception ex) { Logger.WriteException("Acquired handler", ex); }
    }

    public void Stop()
    {
        if (_session == null) return;
        Logger.WriteVerbose("[Acquisition] Stopping ETW session");
        try { _session.Source.Kernel.FileIOCreate -= OnFileCreate; } catch { }
        CleanupSession();
        try { _processTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _processTask = null;
    }

    private void CleanupSession()
    {
        try { _session?.Stop(); } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;
    }

    public void Dispose() => Stop();
}
