using System.Runtime.InteropServices;

namespace ControllerManager.Native;

internal static class NtDll
{
    // ── NTDLL ────────────────────────────────────────────────────────────────────

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(
        IntPtr ProcessHandle, int ProcessInformationClass,
        IntPtr ProcessInformation, int ProcessInformationLength,
        out int ReturnLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryObject(
        IntPtr Handle, int ObjectInformationClass,
        IntPtr ObjectInformation, int ObjectInformationLength,
        out int ReturnLength);

    internal const int ProcessHandleInformation  = 51;
    internal const int ObjectNameInformation     = 1;
    internal const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    // ── Kernel32 ─────────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle, IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle,
        uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    internal const uint PROCESS_DUP_HANDLE          = 0x0040;
    internal const uint PROCESS_QUERY_INFORMATION   = 0x0400;
    internal const uint DUPLICATE_SAME_ACCESS       = 0x0002;

    // ── Handle table layout (x64 only) ───────────────────────────────────────────

    // PROCESS_HANDLE_SNAPSHOT_INFORMATION header: NumberOfHandles(8) + Reserved(8) = 16 bytes
    internal const int SnapshotHeaderSize = 16;

    // PROCESS_HANDLE_TABLE_ENTRY_INFO: Handle(8)+HandleCount(8)+PointerCount(8)+4×uint(16) = 40 bytes
    internal const int HandleEntrySize = 40;
}
