namespace ControllerManager.Services;

/// <summary>
/// Abstracts the pnputil and HidHide device-hiding backends so both can coexist.
/// HidHide is preferred when available; pnputil is the fallback.
/// </summary>
public interface IDeviceHider
{
    string Name        { get; }
    bool   IsAvailable { get; }

    // ── Game session ──────────────────────────────────────────────────────────────

    /// <summary>Hide all listed devices before game launch.</summary>
    void BeginSession(IEnumerable<string> instanceIds, string gameExePath);

    /// <summary>
    /// Update the set of hidden devices mid-session (DisableThenRestore phase).
    /// Only the supplied IDs remain hidden; others become visible again.
    /// </summary>
    void UpdateSession(IEnumerable<string> remainingInstanceIds);

    /// <summary>End the session and restore all devices.</summary>
    void EndSession();

    // ── Devices tab persistent toggle ────────────────────────────────────────────

    void SetPersistentHidden(string instanceId, bool hidden);
    bool IsPersistentlyHidden(string instanceId);

    // ── Global on/off (HidHide only; no-op for pnputil) ─────────────────────────

    bool GetActive();
    void SetActive(bool active);
}
