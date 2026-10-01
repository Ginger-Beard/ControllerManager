using System.Diagnostics;
using ControllerManager.Services;

namespace ControllerManager.Cli;

/// <summary>
/// Handles the standalone <c>--usb-diag</c> CLI flag: runs the Phase-1 USB
/// diagnostics pass, writes the report to a text file, and opens it. A throwaway
/// harness for confirming the enumeration on real hardware before the Diagnostics
/// tab UI exists — see [[project_diagnostics_tab]].
/// </summary>
public static class UsbDiagInvocation
{
    public static void Handle(string appDataDir)
    {
        var report = new UsbDiagnostics().BuildReport();
        Logger.Write(report);

        var path = Path.Combine(appDataDir, "usb-diag.txt");
        try
        {
            File.WriteAllText(path, report);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.WriteException("UsbDiagInvocation", ex);
        }
    }
}
