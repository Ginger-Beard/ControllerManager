using System.Runtime.InteropServices;
using HIDReorder.Models;
using HIDReorder.Native;

namespace HIDReorder.Services;

/// <summary>
/// Polls a target process's handle table every 100ms and emits an event
/// whenever a new \Device\HID* handle is opened.
///
/// For FH5/FH6 target gameinputsvc.exe (handles live there, not in the game).
/// For iRacing, ACC, etc. target the game process directly.
/// </summary>
public sealed class HandleWatcher : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task?                    _task;
    private readonly int             _pollMs;

    public event EventHandler<HidHandleEvent>? HidHandleOpened;

    public HandleWatcher(int pollMs = 100) => _pollMs = pollMs;

    public void Start(int pid)
    {
        Stop();
        _cts  = new CancellationTokenSource();
        _task = Task.Run(() => PollLoop(pid, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(2000); } catch { }
        _cts?.Dispose();
        _cts  = null;
        _task = null;
    }

    public void Dispose() => Stop();

    // ── Poll loop ────────────────────────────────────────────────────────────────

    // When true, emits ALL named handles — not just HID — useful for diagnosis
    public bool DiagnosticMode { get; set; }

    // Fires on every poll with (total handles scanned, named handles found, last NT status)
    public event EventHandler<(int total, int named, int ntStatus)>? PollStats;

    private void PollLoop(int pid, CancellationToken ct)
    {
        var hProcess = NtDll.OpenProcess(
            NtDll.PROCESS_DUP_HANDLE | NtDll.PROCESS_QUERY_INFORMATION,
            false, pid);

        if (hProcess == IntPtr.Zero)
        {
            PollStats?.Invoke(this, (-1, -1, 0)); // signal open failure
            return;
        }

        try
        {
            HashSet<IntPtr> prev = [];
            bool firstPoll = true;

            while (!ct.IsCancellationRequested)
            {
                var (handleList, ntStatus) = GetProcessHandles(hProcess);
                var current  = new Dictionary<IntPtr, string>();
                int named    = 0;

                foreach (var handleValue in handleList)
                {
                    var name = GetHandleName(hProcess, handleValue);
                    if (name is null) continue;
                    named++;

                    bool isHid     = name.StartsWith(@"\Device\HID",  StringComparison.OrdinalIgnoreCase);
                    bool isDiInput = name.Contains(@"\DirectInput\", StringComparison.OrdinalIgnoreCase);
                    bool isDevice  = name.StartsWith(@"\Device\",    StringComparison.OrdinalIgnoreCase);
                    if (isHid || isDiInput || (DiagnosticMode && isDevice))
                        current[handleValue] = name;
                }

                PollStats?.Invoke(this, (handleList.Count, named, ntStatus));

                if (!firstPoll)
                {
                    foreach (var (handle, path) in current)
                    {
                        if (!prev.Contains(handle))
                        {
                            HidHandleOpened?.Invoke(this, new HidHandleEvent
                            {
                                DevicePath = path,
                                ProcessId  = pid,
                                Timestamp  = DateTime.Now,
                            });
                        }
                    }
                }

                prev      = current.Keys.ToHashSet();
                firstPoll = false;

                try { Task.Delay(_pollMs, ct).Wait(ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            NtDll.CloseHandle(hProcess);
        }
    }

    // ── Handle enumeration ───────────────────────────────────────────────────────

    private static (List<IntPtr> handles, int ntStatus) GetProcessHandles(IntPtr hProcess)
    {
        int size = 4096;
        int lastStatus = 0;

        while (true)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                lastStatus = NtDll.NtQueryInformationProcess(
                    hProcess, NtDll.ProcessHandleInformation, buf, size, out int needed);

                if (lastStatus == NtDll.STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(needed + 1024, size * 2);
                    continue;
                }

                if (lastStatus != 0) return ([], lastStatus);

                long count = Marshal.ReadInt64(buf, 0);
                var handles = new List<IntPtr>((int)Math.Min(count, 8192));

                for (long i = 0; i < count; i++)
                {
                    int offset      = NtDll.SnapshotHeaderSize + (int)(i * NtDll.HandleEntrySize);
                    var handleValue = Marshal.ReadIntPtr(buf, offset);
                    handles.Add(handleValue);
                }

                return (handles, lastStatus);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    private static string? GetHandleName(IntPtr hProcess, IntPtr handleValue)
    {
        if (!NtDll.DuplicateHandle(
            hProcess, handleValue,
            NtDll.GetCurrentProcess(), out var dup,
            0, false, NtDll.DUPLICATE_SAME_ACCESS))
            return null;

        string? name    = null;
        var finished    = new ManualResetEventSlim(false);

        // NtQueryObject can hang indefinitely on certain handle types (pipes, sockets).
        // Run it on a thread pool thread with a hard timeout; close the dup handle
        // to unblock the thread if it times out.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                const int bufSize = 1024;
                var nameBuf = Marshal.AllocHGlobal(bufSize);
                try
                {
                    int st = NtDll.NtQueryObject(dup, NtDll.ObjectNameInformation,
                                                 nameBuf, bufSize, out int _retLen);
                    if (st == 0)
                    {
                        // UNICODE_STRING on x64: Length(2)+MaxLen(2)+pad(4)+Buffer(8)
                        short length  = Marshal.ReadInt16(nameBuf, 0);
                        IntPtr strPtr = Marshal.ReadIntPtr(nameBuf, 8);
                        if (length > 0 && strPtr != IntPtr.Zero)
                            name = Marshal.PtrToStringUni(strPtr, length / 2);
                    }
                }
                finally { Marshal.FreeHGlobal(nameBuf); }
            }
            catch { }
            finally { finished.Set(); }
        });

        bool completed = finished.Wait(80);

        // Closing the dup unblocks any stalled NtQueryObject call on the worker thread
        NtDll.CloseHandle(dup);

        return completed ? name : null;
    }
}
