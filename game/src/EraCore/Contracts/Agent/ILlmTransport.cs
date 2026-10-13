// T2: pure contract — K-6 agent pipeline ports, T-023, draft (signatures pending C-12/C-13 ratification, 07-decided-plan D-21).

namespace EraCore.Contracts.Agent;

/// <summary>
/// LLM translator port (K-6 draft): one round-trip "messages + tool schema
/// → candidate action JSON". Transport-neutral BY DESIGN — no HTTP, SSE,
/// provider or endpoint detail ever appears here; those belong to the C-16
/// protocol machine and its adapters (D-01/D-20: real networking exists
/// only outside EraCore). Keys never travel through this port and are never
/// logged (RL5, K-10).
/// </summary>
public interface ILlmTransport
{
    /// <summary>Sends one request and awaits the classified reply.</summary>
    Task<LlmTransportResult> SendAsync(
        LlmTransportRequest request,
        CancellationToken cancellationToken = default);
}
