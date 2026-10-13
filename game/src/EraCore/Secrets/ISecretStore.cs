namespace EraCore.Secrets;

/// <summary>
/// Provider-keyed secret storage port (C-17 Key 安全存储, T-054 / F-084;
/// 方案 §11.6 Key 安全分级). Implementations persist per-provider API keys:
/// primary = OS credential store (Windows DPAPI, current-user scope),
/// fallback = a dedicated file under the D-03 injected <c>IFileStore</c> root
/// with tightened permissions (0600 best effort).
/// Contract rules every implementation must honor:
/// <list type="bullet">
/// <item><see cref="Retrieve"/> returns <see cref="string.Empty"/> for a
/// missing provider (and after <see cref="Clear"/>); absence is not an
/// error.</item>
/// <item>Secret values never appear in any log line, error message or
/// exception text emitted by an implementation (RL5 / F-084).</item>
/// <item>Storing an empty value removes the entry (idempotent with
/// <see cref="Clear"/>).</item>
/// </list>
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Capability probe (§11.6 Key 安全分级): true when this backend's OS
    /// facility actually works on the current machine (e.g. DPAPI exists).
    /// A store whose <see cref="IsAvailable"/> is false must throw with a
    /// clear message on use instead of failing silently.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Backend designation for diagnostics (e.g. "dpapi-currentuser",
    /// "file-fallback"). Never contains key material.
    /// </summary>
    string Backend { get; }

    /// <summary>
    /// Stores (replaces) the secret for one provider id (§11.6 provider
    /// <c>id</c> key, e.g. "zhipu-glm"). An empty or null value removes the
    /// entry.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="providerId"/> is
    /// null, empty or whitespace.</exception>
    void Store(string providerId, string? secretValue);

    /// <summary>
    /// Returns the stored secret for one provider id, or
    /// <see cref="string.Empty"/> when no entry exists.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="providerId"/> is
    /// null, empty or whitespace.</exception>
    string Retrieve(string providerId);

    /// <summary>
    /// Removes one provider's secret. Idempotent: clearing an absent entry
    /// succeeds.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="providerId"/> is
    /// null, empty or whitespace.</exception>
    void Clear(string providerId);

    /// <summary>
    /// Removes every provider's secret — the §11.6 "一键清除" operation
    /// behind the UI button.
    /// </summary>
    void ClearAll();
}
