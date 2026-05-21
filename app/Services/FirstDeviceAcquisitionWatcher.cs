using System.Runtime.InteropServices;
using System.Text;
using ControllerManager.Native;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32.SafeHandles;

namespace ControllerManager.Services;

/// <summary>
/// Watches for the moment a target game process (or its WGI broker) opens one
/// of the always-visible HID device files configured for the profile.
///
/// EAC-safe: ETW is one-way kernel telemetry — we never touch the game's
/// process, handles, or memory.
///
/// Path matching: ETW reports file names in NT object form
/// (e.g. <c>\Device\HID00000123</c>), not the Win32 symbolic link form we
/// have on <see cref="Models.HidDevice.DeviceInterfacePath"/>. <see cref="Start"/>
/// translates each Win32 path to its NT path via <c>NtQueryObject</c> at watch
/// start. Only events whose <c>FileName</c> matches one of those NT paths fire
/// the <see cref="Acquired"/> signal.
///
/// Opener matching: Modern Xbox/UWP titles (Forza Horizon, anything using
/// Windows.Gaming.Input) don't open HID files directly — they go through the
/// <c>GameInputService</c> broker. The signal fires for opens from EITHER the
/// game's PID OR any process whose name matches <see cref="_watchedProcessNames"/>.
///
/// All file creates targeting watched paths are also logged verbosely (PID +
/// process name) so unknown brokers can be discovered post-hoc.
/// </summary>
public sealed class FirstDeviceAcquisitionWatcher : IDisposable
{
    private const string SessionName = "ControllerManager_DeviceAcquisition";

    // Microsoft-Windows-Input-HIDCLASS — fires events for HID IOCTL flow,
    // which is what happens against already-open handles (game ↔ broker IPC).
    // FileIOCreate misses these entirely because no new CreateFile is involved.
    private static readonly Guid HidClassProviderGuid =
        new("6465da78-e7a0-4f39-b084-8f53c7c30dc6");

    public readonly record struct DeviceToWatch(string Win32Path, string FriendlyName);

    private TraceEventSession?    _session;
    private Task?                 _processTask;
    private int                   _targetPid = -1;
    // path → friendly name. Used for all logging.
    private Dictionary<string, string> _pathToName = new(StringComparer.OrdinalIgnoreCase);
    // Subset of _pathToName whose opens should also fire Acquired (AV devices).
    private HashSet<string>       _triggerPaths         = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>       _watchedProcessNames  = new(StringComparer.OrdinalIgnoreCase);
    private System.Diagnostics.Stopwatch _sessionClock  = new();
    private int                   _fired;
    // When true, log every kernel file-create whose path looks HID-related —
    // even if it doesn't match the resolved watched paths. Used in diagnostic
    // mode to discover path-format mismatches and broker-only opens.
    private bool                  _logAllHidOpens;

    public event Action? Acquired;

    /// <summary>
    /// Subscribe to the kernel file-create stream. Two device sets are watched:
    /// <list type="bullet">
    /// <item><b>trigger devices</b> — the Always Visible devices for the profile.
    /// Opens on these by the game PID (or a named broker) fire <see cref="Acquired"/>
    /// once per session.</item>
    /// <item><b>diagnostic devices</b> — the Reveal-After-Start devices. The game
    /// can't see these during the wait phase (HidHide blocks them), so opens on
    /// them should be zero or near-zero. Any opens that DO occur are logged
    /// verbosely as a sanity check on the hiding pipeline.</item>
    /// </list>
    /// Returns true if ETW + path resolution succeeded; false on any failure
    /// (caller should fall back to timer-based reveal).
    /// </summary>
    /// <param name="targetPid">The game's PID.</param>
    /// <param name="triggerDevices">Always Visible devices — their opens fire the signal.</param>
    /// <param name="diagnosticDevices">Reveal-After-Start devices — opens logged only.</param>
    /// <param name="brokerProcessNames">Additional process names (without .exe) whose opens of the trigger paths should also fire the signal — e.g. <c>GameInputService</c> for WGI titles.</param>
    /// <param name="logAllHidOpens">Diagnostic mode: log every kernel file-create on any HID-looking path, regardless of whether it matches the resolved watched set. Use when investigating path-format issues or broker pre-open scenarios.</param>
    public bool Start(int targetPid,
                      IEnumerable<DeviceToWatch> triggerDevices,
                      IEnumerable<DeviceToWatch>? diagnosticDevices = null,
                      IEnumerable<string>? brokerProcessNames = null,
                      bool logAllHidOpens = false)
    {
        if (_session != null)
            throw new InvalidOperationException("FirstDeviceAcquisitionWatcher already started");

        _targetPid           = targetPid;
        _fired               = 0;
        _pathToName          = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _triggerPaths        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _watchedProcessNames = new HashSet<string>(brokerProcessNames ?? Array.Empty<string>(),
                                                   StringComparer.OrdinalIgnoreCase);
        _logAllHidOpens      = logAllHidOpens;

        // Resolve each Win32 device interface path (\\?\HID#xxx#{guid}) to its
        // NT object path (\Device\xxx) so the substring match below is exact.
        foreach (var dev in triggerDevices)
        {
            var nt = ResolveNtPath(dev.Win32Path);
            if (string.IsNullOrEmpty(nt)) { Logger.WriteVerbose($"[Acquisition] Could not resolve NT path for trigger '{dev.FriendlyName}' ({dev.Win32Path})"); continue; }
            _pathToName[nt] = dev.FriendlyName;
            _triggerPaths.Add(nt);
            Logger.WriteVerbose($"[Acquisition] Trigger watch: '{dev.FriendlyName}' → {nt}");
        }

        if (diagnosticDevices is not null)
        {
            foreach (var dev in diagnosticDevices)
            {
                var nt = ResolveNtPath(dev.Win32Path);
                if (string.IsNullOrEmpty(nt)) continue;
                if (_pathToName.ContainsKey(nt)) continue; // already a trigger
                _pathToName[nt] = dev.FriendlyName;
                Logger.WriteVerbose($"[Acquisition] Diagnostic watch (hidden): '{dev.FriendlyName}' → {nt}");
            }
        }

        if (_triggerPaths.Count == 0)
        {
            Logger.Write("[Acquisition] No resolvable always-visible device paths — ETW watcher won't help.");
            return false;
        }

        _sessionClock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); } catch { }

            _session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true,
            };

            // Enable Process keyword too so data.ProcessName resolves reliably
            // for broker-name matching and diagnostic logging.
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.FileIOInit |
                KernelTraceEventParser.Keywords.Process);
            _session.Source.Kernel.FileIOCreate += OnFileCreate;

            // Diagnostic: also subscribe to the user-mode HIDCLASS provider.
            // This catches the IOCTL flow we care about for WGI titles (game ↔
            // GameInputSvc IPC ends up here as HID class IOCTLs against the
            // broker's already-open handles).
            if (_logAllHidOpens)
            {
                _session.EnableProvider(HidClassProviderGuid, TraceEventLevel.Verbose);
                var dyn = new DynamicTraceEventParser(_session.Source);
                dyn.All += OnHidClassEvent;
            }

            _processTask = Task.Run(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Logger.WriteException("ETW Source.Process", ex); }
            });

            var brokers = _watchedProcessNames.Count == 0
                ? "(none)"
                : string.Join(",", _watchedProcessNames);
            Logger.WriteVerbose(
                $"[Acquisition] Watching PID {targetPid} (+ brokers: {brokers}) — {_triggerPaths.Count} trigger path(s), {_pathToName.Count - _triggerPaths.Count} diagnostic path(s)");
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
        var path = data.FileName;
        if (string.IsNullOrEmpty(path)) return;

        if (!_pathToName.TryGetValue(path, out var friendly))
        {
            // In diagnostic mode, log any HID-looking path the kernel reports
            // so we can spot path-format mismatches between NtQueryObject's
            // view and the ETW kernel-file event view.
            if (_logAllHidOpens && LooksLikeHidPath(path))
            {
                var elapsed = _sessionClock.Elapsed.TotalSeconds;
                Logger.WriteVerbose(
                    $"[Acquisition] +{elapsed:0.000}s UNMATCHED HID open: '{path}' by PID={data.ProcessID} Process='{data.ProcessName}'");
            }
            return;
        }

        var elapsedSec = _sessionClock.Elapsed.TotalSeconds;
        bool isTrigger = _triggerPaths.Contains(path);

        // Always log opens on watched paths with friendly name + relative time —
        // useful for both real-time debugging and future calibration features.
        Logger.WriteVerbose(
            $"[Acquisition] +{elapsedSec:0.000}s '{friendly}' opened by PID={data.ProcessID} Process='{data.ProcessName}' (trigger={isTrigger})");

        if (!isTrigger) return;

        bool pidMatch = (_targetPid >= 0 && data.ProcessID == _targetPid)
                     || (_watchedProcessNames.Count > 0
                         && _watchedProcessNames.Contains(data.ProcessName));
        if (!pidMatch) return;

        if (Interlocked.Exchange(ref _fired, 1) != 0) return;

        Logger.Write(
            $"[Acquisition] Signal fired by '{friendly}' at +{elapsedSec:0.000}s (PID={data.ProcessID}, Process='{data.ProcessName}')");
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

    // Diagnostic handler for Microsoft-Windows-Input-HIDCLASS events. The
    // provider is undocumented externally — we don't know the event schema
    // ahead of time — so dump every accessible payload field by name.
    private void OnHidClassEvent(TraceEvent data)
    {
        if (data.ProviderGuid != HidClassProviderGuid) return;

        var elapsed = _sessionClock.Elapsed.TotalSeconds;
        var sb = new StringBuilder(256);
        sb.Append($"[HIDCLASS] +{elapsed:0.000}s ");
        sb.Append($"Event='{data.EventName}' ID={(int)data.ID} Opcode={data.Opcode} ");
        sb.Append($"PID={data.ProcessID} Process='{data.ProcessName}'");

        var names = data.PayloadNames;
        if (names != null)
        {
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    var val = data.PayloadValue(i);
                    sb.Append(' ').Append(names[i]).Append('=').Append(val);
                }
                catch { /* unparseable payload field — skip */ }
            }
        }

        Logger.WriteVerbose(sb.ToString());
    }

    // ETW reports HID device opens in a few NT path forms. The exact form
    // depends on which driver layer is being opened (raw HID, HID class, USB
    // HID minidriver, etc.). Match on common substrings so we catch any of
    // them in diagnostic mode.
    private static bool LooksLikeHidPath(string path) =>
        path.Contains(@"\Device\HID",          StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\Device\HumanInterface", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\Device\KMDF",         StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\Device\Usb",          StringComparison.OrdinalIgnoreCase);

    // ── NT path resolution ──────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    private const uint FILE_SHARE_READ_WRITE_DELETE = 0x7;
    private const uint OPEN_EXISTING                = 3;

    /// <summary>
    /// Translates a Win32 device interface path (<c>\\?\HID#xxx#{guid}</c>) to
    /// the underlying NT object path (<c>\Device\xxx</c>) the kernel reports
    /// in ETW. Returns null on any failure.
    /// </summary>
    private static string? ResolveNtPath(string win32Path)
    {
        if (string.IsNullOrEmpty(win32Path)) return null;

        // Open with 0 access — works even when the device is owned exclusively
        // by another process. We just need a handle to query the object name.
        using var h = CreateFileW(win32Path, 0,
            FILE_SHARE_READ_WRITE_DELETE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        // OBJECT_NAME_INFORMATION is a UNICODE_STRING (Length, MaxLen, Buffer)
        // immediately followed by the string data. Query for buffer size first.
        const int bufSize = 1024;
        var buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            int status = NtDll.NtQueryObject(h.DangerousGetHandle(),
                NtDll.ObjectNameInformation, buf, bufSize, out _);
            if (status != 0) return null;

            // UNICODE_STRING on x64: Length(2) + MaxLen(2) + pad(4) + Buffer(8) = 16 bytes
            short length = Marshal.ReadInt16(buf, 0);
            IntPtr strPtr = Marshal.ReadIntPtr(buf, 8);
            if (length <= 0 || strPtr == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(strPtr, length / 2);
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
