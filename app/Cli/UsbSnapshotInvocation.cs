using System.Diagnostics;
using ControllerManager.Services;

namespace ControllerManager.Cli;

/// <summary>
/// Throwaway CLI harness for the Snapshot → Compare flow (precursor to the
/// Diagnostics-tab buttons). Two commands:
/// <list type="bullet">
/// <item><c>--usb-snapshot</c> — fingerprint current USB identities to disk.</item>
/// <item><c>--usb-compare</c> — diff the saved fingerprint against a fresh capture,
/// write the report, and open it.</item>
/// </list>
/// Workflow for the channel-scramble bug: snapshot while the amp works, add the
/// device that triggers it, then compare to see which identity field moved.
/// See [[project_diagnostics_tab]].
/// </summary>
public static class UsbSnapshotInvocation
{
    private const string SnapshotFile = "usb-snapshot.json";

    public static void Snapshot(string appDataDir)
    {
        var diag = new UsbDiagnostics();
        var snap = diag.CaptureSnapshot();
        var path = Path.Combine(appDataDir, SnapshotFile);

        try
        {
            UsbDiagnostics.SaveSnapshot(snap, path);
            Logger.Write($"[UsbDiag] Snapshot saved: {snap.Devices.Count} device(s) → {path}");
            Open(WriteSidecar(appDataDir, "usb-snapshot.txt",
                $"Saved USB snapshot at {snap.CapturedAt:yyyy-MM-dd HH:mm:ss}\n" +
                $"{snap.Devices.Count} device(s) recorded.\n\n" +
                "Now change the hub population (add the device that triggers the issue),\n" +
                "then run:  ControllerManager.exe --usb-compare"));
        }
        catch (Exception ex) { Logger.WriteException("UsbSnapshotInvocation.Snapshot", ex); }
    }

    public static void Compare(string appDataDir)
    {
        var path   = Path.Combine(appDataDir, SnapshotFile);
        var before = UsbDiagnostics.LoadSnapshot(path);
        if (before is null)
        {
            Open(WriteSidecar(appDataDir, "usb-compare.txt",
                "No saved snapshot found. Run --usb-snapshot first."));
            return;
        }

        var after  = new UsbDiagnostics().CaptureSnapshot();
        var report = UsbDiagnostics.BuildComparisonReport(before, after);
        Logger.Write(report);
        Open(WriteSidecar(appDataDir, "usb-compare.txt", report));
    }

    private static string WriteSidecar(string dir, string name, string content)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.WriteException("UsbSnapshotInvocation.Open", ex); }
    }
}
