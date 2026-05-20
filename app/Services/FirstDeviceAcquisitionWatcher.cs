using System.Runtime.InteropServices;
using ControllerManager.Native;
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

    private TraceEventSession?    _session;
    private Task?                 _processTask;
    private int                   _targetPid = -1;
    private HashSet<string>       _watchedNtPaths       = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>       _watchedProcessNames  = new(StringComparer.OrdinalIgnoreCase);
    private int                   _fired;

    public event Action? Acquired;

    /// <summary>
    /// Subscribe to the kernel file-create stream filtered to the game's PID
    /// (or one of the named broker processes) AND the NT paths of the supplied
    /// Win32 device interface paths. Returns true if ETW + path resolution
    /// succeeded; false on any failure (caller should fall back to timer-based
    /// reveal).
    /// </summary>
    /// <param name="targetPid">The game's PID.</param>
    /// <param name="alwaysVisibleDevicePaths">Win32 device interface paths to watch.</param>
    /// <param name="brokerProcessNames">Additional process names (without .exe) whose opens of the watched paths should also fire the signal — e.g. <c>GameInputService</c> for WGI titles.</param>
    public bool Start(int targetPid,
                      IEnumerable<string> alwaysVisibleDevicePaths,
                      IEnumerable<string>? brokerProcessNames = null)
    {
        if (_session != null)
            throw new InvalidOperationException("FirstDeviceAcquisitionWatcher already started");

        _targetPid = targetPid;
        _fired     = 0;
        _watchedNtPaths      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _watchedProcessNames = new HashSet<string>(brokerProcessNames ?? Array.Empty<string>(),
                                                   StringComparer.OrdinalIgnoreCase);

        // Resolve each Win32 device interface path (\\?\HID#xxx#{guid}) to its
        // NT object path (\Device\xxx) so the substring match below is exact.
        foreach (var win32Path in alwaysVisibleDevicePaths)
        {
            var nt = ResolveNtPath(win32Path);
            if (!string.IsNullOrEmpty(nt))
            {
                _watchedNtPaths.Add(nt);
                Logger.WriteVerbose($"[Acquisition] Watching: {nt}");
            }
            else
            {
                Logger.WriteVerbose($"[Acquisition] Could not resolve NT path for: {win32Path}");
            }
        }

        if (_watchedNtPaths.Count == 0)
        {
            Logger.Write("[Acquisition] No resolvable always-visible device paths — ETW watcher won't help.");
            return false;
        }

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

            _processTask = Task.Run(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Logger.WriteException("ETW Source.Process", ex); }
            });

            var brokers = _watchedProcessNames.Count == 0
                ? "(none)"
                : string.Join(",", _watchedProcessNames);
            Logger.WriteVerbose(
                $"[Acquisition] Watching PID {targetPid} (+ brokers: {brokers}) for {_watchedNtPaths.Count} path(s)");
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

        bool pathMatch = _watchedNtPaths.Contains(path);

        // Diagnostic: log every open of a watched path regardless of opener
        // so we can confirm broker theories (which PID/process is actually
        // opening the device file). Rare event — set hits are O(1) and the
        // watched set is small, so spam risk is low.
        if (pathMatch)
        {
            Logger.WriteVerbose(
                $"[Acquisition] Watched-path open: '{path}' by PID={data.ProcessID} Process='{data.ProcessName}'");
        }

        bool pidMatch = (_targetPid >= 0 && data.ProcessID == _targetPid)
                     || (_watchedProcessNames.Count > 0
                         && _watchedProcessNames.Contains(data.ProcessName));

        if (!pidMatch || !pathMatch) return;

        if (Interlocked.Exchange(ref _fired, 1) != 0) return;

        Logger.Write(
            $"[Acquisition] Signal fired: {path} (PID={data.ProcessID}, Process='{data.ProcessName}')");
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
