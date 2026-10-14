namespace EraCore.Llm;

/// <summary>
/// One outbound HTTP request over the wire port. Built exclusively by
/// <see cref="LlmGateway"/> after the domain-whitelist and key checks pass;
/// implementations receive it pre-validated and must send it EXACTLY as
/// given (no redirect following, no URL rewriting).
/// </summary>
/// <param name="Method">HTTP method, always "POST" for chat calls.</param>
/// <param name="Url">Absolute http(s) endpoint URL already whitelist-checked.</param>
/// <param name="Headers">
/// Flat header map including auth material (Bearer / x-api-key). Header
/// VALUES carry key material: implementations must never log them (RL5).
/// </param>
/// <param name="BodyJson">Serialized JSON request body.</param>
/// <param name="ConnectTimeout">
/// Connect-phase budget from the provider config; response-body deadlines
/// (first byte / chunk idle) are enforced by the gateway above this port.
/// </param>
public sealed record HttpPortRequest(
    string Method,
    Uri Url,
    IReadOnlyDictionary<string, string> Headers,
    string BodyJson,
    TimeSpan ConnectTimeout);

/// <summary>
/// One HTTP reply. Success replies to streaming calls carry a live
/// <see cref="BodyStream"/> the gateway reads incrementally (SSE); failure
/// replies carry the (small) error <see cref="Body"/> for classification.
/// </summary>
/// <param name="StatusCode">Raw HTTP status code.</param>
/// <param name="Body">Error body text on non-2xx; null on streaming success.</param>
/// <param name="BodyStream">Response body stream on 2xx; null on failure.</param>
public sealed record HttpPortResponse(
    int StatusCode,
    string? Body,
    Stream? BodyStream)
{
    /// <summary>True for 2xx replies (the only replies that may carry a body stream).</summary>
    public bool IsSuccess => StatusCode is >= 200 and <= 299;
}

/// <summary>
/// Wire-level HTTP port for the C-16 LlmGateway (T-072). BYOK 架构裁决 D:
/// every LLM call in this game goes through the gateway, and the gateway
/// only ever speaks through this port — EraCore itself NEVER touches
/// System.Net.Http (CS-1.3 / D-01 / D-20; enforced by
/// GodotSharpGuardTests). Real implementations live OUTSIDE EraCore: the
/// test assembly ships an HttpClient-based adapter for online verification
/// and cassette recording, and the presentation layer provides the Godot
/// HTTPClient adapter in production (方案 §11.7).
/// Implementations must honor three hard rules:
/// <list type="bullet">
/// <item>NO redirect following — a 3xx surfaces as-is so the domain
/// whitelist cannot be bypassed by a Location hop.</item>
/// <item>NO logging of header values or body (RL5: keys ride the
/// headers).</item>
/// <item>Failures surface as exceptions (network unreachable / DNS /
/// TLS); the gateway classifies them.</item>
/// </list>
/// </summary>
public interface IHttpPort
{
    /// <summary>Sends one request and returns status plus body/stream.</summary>
    /// <exception cref="IOException">Transport failure (DNS, TLS, refused, unreachable).</exception>
    Task<HttpPortResponse> SendAsync(
        HttpPortRequest request,
        CancellationToken cancellationToken = default);
}
