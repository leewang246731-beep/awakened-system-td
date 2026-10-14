using System.Text.Json;

namespace EraCore.Llm;

/// <summary>
/// Maps an HTTP reply (or transport failure) onto an
/// <see cref="LlmErrorKeys"/> entry (方案 §11.7 错误码分类; F-089 首版).
/// Provider error CODES win over status codes: GLM returns 1315 (Coding
/// Plan key misuse) and 1301 (content filter) inside the error body, and
/// MiMo reports unsupported models via 4xx + message text. Pure function;
/// never throws — an unclassifiable reply is <see cref="LlmErrorKeys.BadResponse"/>.
/// </summary>
public static class LlmErrorClassifier
{
    /// <summary>GLM error code: Coding Plan keys are restricted to official tools.</summary>
    public const string CodingPlanCode = "1315";

    /// <summary>GLM error code: request blocked by content safety policy.</summary>
    public const string ContentBlockedCode = "1301";

    /// <summary>
    /// Classifies an HTTP reply. <paramref name="bodySnippet"/> is the
    /// (already size-capped, key-free) error body; may be null/empty for
    /// bodiless replies.
    /// </summary>
    public static string ClassifyHttp(int statusCode, string? bodySnippet)
    {
        var providerCode = ExtractProviderErrorCode(bodySnippet);
        if (providerCode == CodingPlanCode)
        {
            return LlmErrorKeys.CodingPlan;
        }

        if (providerCode == ContentBlockedCode)
        {
            return LlmErrorKeys.ContentBlocked;
        }

        if (statusCode is >= 300 and <= 399)
        {
            return LlmErrorKeys.RedirectBlocked;
        }

        switch (statusCode)
        {
            case 401 or 403:
                return LlmErrorKeys.Auth;
            case 429:
                return LlmErrorKeys.RateLimit;
            case >= 500 and <= 599:
                return LlmErrorKeys.ServerError;
        }

        if (LooksLikeInvalidModel(statusCode, bodySnippet))
        {
            return LlmErrorKeys.InvalidModel;
        }

        return statusCode is >= 400 and <= 499
            ? LlmErrorKeys.BadRequest
            : LlmErrorKeys.BadResponse;
    }

    /// <summary>
    /// Extracts the provider error code from a reply body: prefers
    /// <c>error.code</c> (GLM/zhipu shape, also common on relays), then a
    /// bare root <c>code</c>, then a quoted <c>"code":"NNNN"</c> substring
    /// for bodies that are not plain JSON. Returns null when absent.
    /// </summary>
    public static string? ExtractProviderErrorCode(string? bodySnippet)
    {
        if (string.IsNullOrEmpty(bodySnippet))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bodySnippet);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("code", out var code))
                {
                    var text = CodeText(code);
                    if (text is not null)
                    {
                        return text;
                    }
                }

                if (root.TryGetProperty("code", out var rootCode))
                {
                    var text = CodeText(rootCode);
                    if (text is not null)
                    {
                        return text;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the substring probe.
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            bodySnippet, "\"code\"\\s*:\\s*\"?([0-9]{3,6})\"?");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? CodeText(JsonElement code) => code.ValueKind switch
    {
        JsonValueKind.String => code.GetString(),
        JsonValueKind.Number when code.TryGetInt32(out _) => code.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// MiMo (and relays) report wrong model ids as 400/404 with the model
    /// named in the message; the §11.7 note pins the MiMo lowercase-id pit.
    /// </summary>
    private static bool LooksLikeInvalidModel(int statusCode, string? bodySnippet)
    {
        const int BadRequest = 400;
        const int NotFound = 404;
        if (statusCode is not (BadRequest or NotFound) || string.IsNullOrEmpty(bodySnippet))
        {
            return false;
        }

        return bodySnippet.Contains("Unsupported model", StringComparison.OrdinalIgnoreCase)
            || bodySnippet.Contains("model not found", StringComparison.OrdinalIgnoreCase)
            || bodySnippet.Contains("invalid model", StringComparison.OrdinalIgnoreCase)
            || bodySnippet.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }
}
