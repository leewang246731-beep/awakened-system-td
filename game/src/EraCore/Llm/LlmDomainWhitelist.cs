namespace EraCore.Llm;

/// <summary>
/// Outbound-domain lock of the BYOK gateway (BYOK 架构裁决 D, T-072):
/// the ONLY hosts the LLM tier may ever open a connection to are the
/// domains of the base URLs the player themselves configured (plus,
/// transitively, the connection probe — it targets those same rows).
/// The gateway snapshots this set at construction and re-checks the
/// built endpoint before EVERY wire call; a violation is rejected with
/// zero network. Matching is on host + non-default port (authority),
/// case-insensitive; schemes and paths are not part of identity, but a
/// port mismatch IS a violation (localhost:1234 ≠ localhost:8080).
/// </summary>
public sealed class LlmDomainWhitelist
{
    private readonly HashSet<string> _authorities;

    private LlmDomainWhitelist(HashSet<string> authorities) =>
        _authorities = authorities;

    /// <summary>
    /// Whitelist from raw base URLs; unparseable or non-http(s) entries
    /// are skipped (config validation reports them separately — a
    /// disabled blank template row must not crash whitelist building).
    /// </summary>
    public static LlmDomainWhitelist FromBaseUrls(IEnumerable<string> baseUrls)
    {
        ArgumentNullException.ThrowIfNull(baseUrls);

        var authorities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in baseUrls)
        {
            if (TryNormalizeAuthority(url, out var authority))
            {
                authorities.Add(authority);
            }
        }

        return new LlmDomainWhitelist(authorities);
    }

    /// <summary>Whitelist from provider rows (skips blank base URLs).</summary>
    public static LlmDomainWhitelist FromProviders(IEnumerable<LlmProviderConfig> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        return FromBaseUrls(providers
            .Select(p => p.BaseUrl)
            .Where(b => !string.IsNullOrWhiteSpace(b)));
    }

    /// <summary>The frozen authority set (host, plus ":port" when non-default).</summary>
    public IReadOnlyCollection<string> Authorities => _authorities;

    /// <summary>True when the URL's authority is one of the configured domains.</summary>
    public bool IsAllowed(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        return url.IsAbsoluteUri
            && _authorities.Contains(NormalizeAuthority(url));
    }

    /// <summary>
    /// Lower-cased host + explicit non-default port, e.g.
    /// "api.xiaomimimo.com", "localhost:1234". Uri already collapses
    /// default ports (80/443) for http(s), so both spellings agree.
    /// </summary>
    public static string NormalizeAuthority(Uri url) =>
        url.Authority.ToLowerInvariant();

    private static bool TryNormalizeAuthority(string url, out string authority)
    {
        authority = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        authority = NormalizeAuthority(uri);
        return authority.Length > 0;
    }
}
