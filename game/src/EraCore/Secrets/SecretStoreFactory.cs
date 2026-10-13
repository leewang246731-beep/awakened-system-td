using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;
using EraCore.IO;

namespace EraCore.Secrets;

/// <summary>
/// Backend selection for <see cref="ISecretStore"/> (C-17, T-054; 方案 §11.6
/// Key 安全分级 "优先 OS 凭据管理器…回落"): prefers
/// <see cref="DpapiSecretStore"/> when the real DPAPI probe passes, otherwise
/// falls back to <see cref="FileSecretStore"/> and records the reason through
/// <see cref="ILogSink"/> (Warn) so the degradation is observable. The probe
/// delegate parameter exists for tests; production callers omit it and get
/// the genuine <see cref="DpapiSecretStore.Probe"/> roundtrip.
/// </summary>
public static class SecretStoreFactory
{
    /// <summary>
    /// Creates the best available store over the injected file root.
    /// Selection order: DPAPI (current user) → file fallback.
    /// </summary>
    public static ISecretStore Create(
        IFileStore files, ILogSink log, Func<bool>? dpapiProbe = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(log);

        bool probeOk;
        string probeReason;
        if (dpapiProbe is null)
        {
            probeOk = DpapiSecretStore.Probe(out probeReason);
        }
        else
        {
            probeOk = dpapiProbe();
            probeReason = "dpapi probe returned false";
        }

        var candidate = new DpapiSecretStore(files, log);
        if (candidate.IsAvailable && probeOk)
        {
            log.Write(LogLevel.Info, -1, ActionLog.Scrub(
                $"secret-store factory=select backend={candidate.Backend} " +
                "reason=dpapi-available"));
            return candidate;
        }

        // Non-Windows with a passing injected probe is still no DPAPI — the
        // platform gate decides, and the log says which gate failed.
        var fallbackReason = probeOk
            ? "dpapi backend unavailable on this platform"
            : probeReason;
        log.Write(LogLevel.Warn, -1, ActionLog.Scrub(
            $"secret-store factory=select backend=file-fallback " +
            $"reason={fallbackReason}"));
        return new FileSecretStore(files, log);
    }
}
