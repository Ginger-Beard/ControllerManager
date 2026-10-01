using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ControllerManager.Services;

/// <summary>
/// Downloads and silently installs the HidHide driver from the nefarius/HidHide
/// GitHub release. The app already runs elevated (see app.manifest), so the
/// installer runs under our token with no separate UAC step.
///
/// Deliberately PINNED, not "latest". HidHide is a kernel-mode filter driver we
/// install silently — the highest-blast-radius auto-install there is. We pin the
/// exact version, verify the download against a pinned SHA-256, AND verify the
/// Authenticode publisher before running it. See the spike in DEVELOPMENT.md
/// ("HidHide as a bundled dependency"). Bump all three constants together, after
/// validating a newer driver by hand.
///
/// The v1.5.230.0 setup is an Advanced Installer (Caphyon) .exe bootstrapper —
/// NOT an MSI. Its documented fully-silent invocation is "/exenoui /qn /norestart"
/// (suppress bootstrapper UI, no MSI UI, never auto-reboot — we surface the
/// reboot-required exit code instead).
/// </summary>
public static class HidHideInstaller
{
    // ── Pinned artifact (validated 2026-07; bump as a set) ─────────────────────────
    private const string PinnedVersion = "1.5.230.0";
    private const string InstallerUrl  =
        "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe";
    // sha256sum of HidHide_1.5.230_x64.exe (8,078,016 bytes).
    private const string ExpectedSha256 =
        "f4bbbcb82e6258641b887c74bc81c4c5f66e4aa811808dfc304347687b7605f6";
    // Authenticode signer subject (O=/CN=), chained to DigiCert G4 Code Signing.
    private const string ExpectedPublisher = "Nefarius Software Solutions e.U.";

    public const string ReleasesPage =
        "https://github.com/nefarius/HidHide/releases/latest";

    public sealed record Result(bool Success, string Message);

    /// <summary>
    /// Downloads, verifies (hash + Authenticode), and installs the pinned HidHide
    /// build. Returns success only when the installer exits 0 (or a reboot-required
    /// code). The caller should re-probe the driver afterwards; a reboot-required
    /// result means it won't be live until the machine restarts.
    /// </summary>
    public static async Task<Result> InstallAsync(
        IProgress<string> progress, CancellationToken ct = default)
    {
        string? exePath = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ControllerManager");

            progress.Report($"Downloading HidHide {PinnedVersion}…");
            exePath = Path.Combine(Path.GetTempPath(), $"HidHide_{PinnedVersion}_x64.exe");
            await DownloadAsync(http, InstallerUrl, exePath, ct);
            Logger.Write($"[HidHideInstaller] Downloaded to {exePath}");

            progress.Report("Verifying the download…");
            var actualSha = await ComputeSha256Async(exePath, ct);
            if (!actualSha.Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Write($"[HidHideInstaller] SHA-256 mismatch: got {actualSha}");
                return new Result(false,
                    "The downloaded installer failed its integrity check (SHA-256 mismatch) " +
                    "and was not run. Please install HidHide manually.");
            }

            // Authenticode: chain must be valid AND signer must be the expected
            // publisher. Belt-and-suspenders on top of the hash pin — we're about
            // to hand this file kernel-driver install privileges.
            if (!VerifyAuthenticode(exePath, out var why))
            {
                Logger.Write($"[HidHideInstaller] Authenticode verification failed: {why}");
                return new Result(false,
                    $"The installer's digital signature could not be verified ({why}) " +
                    "and was not run. Please install HidHide manually.");
            }
            Logger.Write("[HidHideInstaller] Hash + Authenticode verified");

            progress.Report("Installing HidHide (this can take a minute)…");
            var exit = await RunInstallerAsync(exePath, ct);
            Logger.Write($"[HidHideInstaller] Installer exit code: {exit}");

            // Success codes (0 = ok, 3010/1641 = ok-but-reboot) all report success;
            // the caller prompts for the reboot HidHide needs regardless of code.
            return exit switch
            {
                0 or 3010 or 1641 => new Result(true,  "HidHide installed successfully."),
                1602              => new Result(false, "Installation was cancelled."),
                1618              => new Result(false, "Another installation is already in progress. Close it and try again."),
                _                 => new Result(false, $"The HidHide installer failed (exit code {exit})."),
            };
        }
        catch (OperationCanceledException)
        {
            return new Result(false, "Installation was cancelled.");
        }
        catch (Exception ex)
        {
            Logger.WriteException("HidHideInstaller.InstallAsync", ex);
            return new Result(false, $"Could not install HidHide automatically: {ex.Message}");
        }
        finally
        {
            try { if (exePath is not null && File.Exists(exePath)) File.Delete(exePath); }
            catch { /* leave it in temp; Windows will reap it eventually */ }
        }
    }

    private static async Task DownloadAsync(
        HttpClient http, string url, string destPath, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash); // upper-case hex; compared case-insensitively
    }

    /// <summary>
    /// Verifies the file's Authenticode chain (WinVerifyTrust) and that the signer
    /// subject matches <see cref="ExpectedPublisher"/>. Revocation is not checked
    /// online — the pinned SHA-256 is the primary integrity guarantee, and requiring
    /// a live CRL fetch would make offline installs flaky.
    /// </summary>
    private static bool VerifyAuthenticode(string path, out string reason)
    {
        // 1. Chain trust via WinVerifyTrust.
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct       = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath  = Marshal.StringToCoTaskMemUni(path),
            hFile          = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };
        var pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Marshal.StructureToPtr(fileInfo, pFile, false);

        var data = new WINTRUST_DATA
        {
            cbStruct            = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice          = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice       = WTD_CHOICE_FILE,
            pFile               = pFile,
            dwStateAction       = WTD_STATEACTION_VERIFY,
            dwProvFlags         = WTD_SAFER_FLAG,
        };
        var pData = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_DATA>());
        Marshal.StructureToPtr(data, pData, false);

        try
        {
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, pData);
            if (result != 0)
            {
                reason = $"chain not trusted (0x{result:X8})";
                return false;
            }

            // 2. Signer publisher must match. CreateFromSignedFile is the only BCL
            // API that pulls the signer cert out of a PE's Authenticode blob;
            // X509CertificateLoader only loads raw cert bytes, so the obsoletion has
            // no drop-in replacement here.
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!cert.Subject.Contains(ExpectedPublisher, StringComparison.OrdinalIgnoreCase))
            {
                reason = "unexpected signer";
                return false;
            }

            reason = "ok";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
        finally
        {
            // Ask WinVerifyTrust to release its cached state, then free our buffers.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(data, pData, true);
            var closeAction = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            WinVerifyTrust(IntPtr.Zero, ref closeAction, pData);

            Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            Marshal.FreeCoTaskMem(pFile);
            Marshal.FreeCoTaskMem(pData);
        }
    }

    private static async Task<int> RunInstallerAsync(string exePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            // Advanced Installer bootstrapper: /exenoui = no bootstrapper UI,
            // /qn = no MSI UI, /norestart = never auto-reboot (we surface 3010/1641).
            Arguments       = "/exenoui /qn /norestart",
            UseShellExecute = false,
            CreateNoWindow  = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the HidHide installer.");

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    // ── WinVerifyTrust interop ──────────────────────────────────────────────────────

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE            = 2;
    private const uint WTD_REVOKE_NONE        = 0;
    private const uint WTD_CHOICE_FILE        = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE  = 2;
    private const uint WTD_SAFER_FLAG         = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint   cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint   cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint   dwUIChoice;
        public uint   fdwRevocationChecks;
        public uint   dwUnionChoice;
        public IntPtr pFile;           // union member (we only use the file choice)
        public uint   dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint   dwProvFlags;
        public uint   dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
