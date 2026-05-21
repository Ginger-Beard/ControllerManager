using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ControllerManager.Services;

/// <summary>
/// Calibration runner — measures which HID devices are actively read during a
/// game session, in what order, without trying to observe individual file
/// opens (which doesn't work for WGI titles like Forza, where the broker
/// pre-opens device handles at boot).
///
/// Mechanism: the Microsoft-Windows-Input-HIDCLASS ETW provider emits a
/// "Rundown" burst whenever it's freshly enabled — one event per HID device
/// containing the lifetime <c>NumReadReports</c> counter and last-read
/// timestamp. By capturing two rundowns separated in time and diffing the
/// counters, we identify which devices saw activity in the interval.
///
/// Compared to FileIOCreate-based observation, this works for both legacy
/// (DirectInput, XInput) and modern (WGI / GameInput) titles because we're
/// reading driver-side state, not user-mode CreateFile calls.
/// </summary>
public sealed class CalibrationRunner
{
    private static readonly Guid HidClassProviderGuid =
        new("6465da78-e7a0-4f39-b084-8f53c7c30dc6");

    public sealed record DeviceActivity(
        string   DeviceInstancePath,
        string   DeviceDescription,
        int      VendorId,
        int      ProductId,
        long     BaselineReads,
        long     FinalReads,
        long     ReadsDelta,
        DateTime? LastReadAtUtc);

    public sealed record Result(
        DateTime BaselineAt,
        DateTime FinalAt,
        IReadOnlyList<DeviceActivity> Devices);

    /// <summary>
    /// Capture a baseline rundown, await cancellation (user clicks "Done"),
    /// capture a final rundown, return the diff sorted by activity descending.
    /// </summary>
    public async Task<Result> RunAsync(CancellationToken ct)
    {
        Logger.Write("[Calibration] Capturing baseline rundown...");
        var baseline = await CaptureRundownAsync(TimeSpan.FromSeconds(5), ct);
        var baselineAt = DateTime.UtcNow;
        Logger.Write($"[Calibration] Baseline: {baseline.Count} device(s) snapshotted.");

        try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        catch (OperationCanceledException) { /* expected — user stopped the test */ }

        Logger.Write("[Calibration] Capturing final rundown...");
        var final = await CaptureRundownAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var finalAt = DateTime.UtcNow;
        Logger.Write($"[Calibration] Final: {final.Count} device(s) snapshotted.");

        var paths = new HashSet<string>(baseline.Keys, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(final.Keys);

        var activities = new List<DeviceActivity>();
        foreach (var p in paths)
        {
            baseline.TryGetValue(p, out var b);
            final.TryGetValue(p, out var f);
            var path  = b?.DeviceInstancePath ?? f?.DeviceInstancePath ?? p;
            var desc  = (f?.DeviceDescription ?? b?.DeviceDescription) ?? "";
            var vid   = f?.VendorId  ?? b?.VendorId  ?? 0;
            var pid   = f?.ProductId ?? b?.ProductId ?? 0;
            var bReads = b?.NumReadReports ?? 0;
            var fReads = f?.NumReadReports ?? 0;
            var delta  = Math.Max(0, fReads - bReads);
            var lastAt = f?.LastReadAtUtc;

            activities.Add(new DeviceActivity(path, desc, vid, pid, bReads, fReads, delta, lastAt));
        }

        activities.Sort((a, b) => b.ReadsDelta.CompareTo(a.ReadsDelta));
        return new Result(baselineAt, finalAt, activities);
    }

    // ── Rundown capture ──────────────────────────────────────────────────────

    private sealed record Snapshot(
        string   DeviceInstancePath,
        string   DeviceDescription,
        int      VendorId,
        int      ProductId,
        long     NumReadReports,
        DateTime? LastReadAtUtc);

    /// <summary>
    /// Open a fresh ETW session, enable HIDCLASS (which triggers Rundown
    /// emission), collect all per-device snapshot events until Rundown/Stop
    /// fires or the timeout elapses. Returns one snapshot per device.
    /// </summary>
    private static async Task<Dictionary<string, Snapshot>> CaptureRundownAsync(
        TimeSpan timeout, CancellationToken ct)
    {
        var sessionName = "ControllerManager_CalibrationRundown_" + Guid.NewGuid().ToString("N");
        var snapshots   = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        var stopSignal  = new TaskCompletionSource();

        TraceEventSession? session = null;
        Task? consumer = null;

        try
        {
            try { TraceEventSession.GetActiveSession(sessionName)?.Stop(); } catch { }

            session = new TraceEventSession(sessionName) { StopOnDispose = true };
            session.EnableProvider(HidClassProviderGuid, TraceEventLevel.Verbose);

            var dyn = new DynamicTraceEventParser(session.Source);
            dyn.All += data =>
            {
                if (data.ProviderGuid != HidClassProviderGuid) return;

                // Rundown/Stop = EventID(2). Once it fires, we have all
                // per-device events. Signal completion to break the wait.
                if ((int)data.ID == 2)
                {
                    stopSignal.TrySetResult();
                    return;
                }

                // EventID(3) = per-device snapshot. Extract by payload name.
                if ((int)data.ID != 3) return;

                try
                {
                    var path = data.PayloadByName("DeviceInstancePath") as string ?? "";
                    if (string.IsNullOrEmpty(path)) return;
                    var desc   = data.PayloadByName("DeviceDescription") as string ?? "";
                    var vid    = ToInt(data.PayloadByName("VendorID"));
                    var pid    = ToInt(data.PayloadByName("ProductID"));
                    var reads  = ToLong(data.PayloadByName("NumReadReports"));
                    var lastFt = ToLong(data.PayloadByName("LastReadReportSuccessTime"));
                    DateTime? lastAt = null;
                    // Provider uses 864000000000 (= 1 day in ticks) as the
                    // "never read" sentinel. Anything else is a real FILETIME.
                    if (lastFt > 864000000000L)
                    {
                        try { lastAt = DateTime.FromFileTimeUtc(lastFt); }
                        catch (ArgumentOutOfRangeException) { /* malformed */ }
                    }

                    snapshots[path] = new Snapshot(path, desc, vid, pid, reads, lastAt);
                }
                catch (Exception ex)
                {
                    Logger.WriteVerbose($"[Calibration] Snapshot parse failed: {ex.Message}");
                }
            };

            consumer = Task.Run(() =>
            {
                try { session.Source.Process(); }
                catch (Exception ex) { Logger.WriteException("Calibration ETW Source.Process", ex); }
            });

            // Wait for Rundown/Stop or timeout, whichever first.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await stopSignal.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.WriteVerbose($"[Calibration] Rundown timed out at {timeout.TotalSeconds:0.#}s with {snapshots.Count} device(s).");
            }
        }
        finally
        {
            try { session?.Stop(); } catch { }
            try { session?.Dispose(); } catch { }
            try { if (consumer != null) await consumer.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }

        return snapshots;
    }

    private static int  ToInt (object? v) => v is null ? 0 : Convert.ToInt32(v);
    private static long ToLong(object? v) => v is null ? 0 : Convert.ToInt64(v);
}
