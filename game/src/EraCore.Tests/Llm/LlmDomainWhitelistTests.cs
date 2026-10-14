using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 域名白名单（BYOK 架构裁决 D）：只放行玩家配置的 base_url 域名；
/// 子域/换端口/相对 URI 一律拒绝。
/// </summary>
// [F-162] C-16 T-072：出网域名锁
public class LlmDomainWhitelistTests
{
    private static LlmDomainWhitelist Whitelist(params string[] baseUrls) =>
        LlmDomainWhitelist.FromBaseUrls(baseUrls);

    [Theory]
    [InlineData("https://api.xiaomimimo.com/v1", "https://api.xiaomimimo.com/v1/chat/completions")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4/chat/completions")]
    [InlineData("https://API.Test.example.com/v1", "https://api.test.example.com/chat/completions")]
    [InlineData("http://localhost:1234/v1", "http://localhost:1234/chat/completions")]
    public void Configured_domains_and_case_variants_are_allowed(string configured, string requested)
    {
        // legal: 配置域名本体（大小写不敏感、路径无关）
        Assert.True(Whitelist(configured).IsAllowed(new Uri(requested)));
    }

    [Theory]
    [InlineData("https://api.xiaomimimo.com/v1", "https://evil.example.com/v1/chat/completions")]
    [InlineData("https://api.test.example.com/v1", "https://sub.api.test.example.com/v1/chat/completions")]
    [InlineData("http://localhost:1234/v1", "http://localhost:8080/v1/chat/completions")]
    [InlineData("https://api.test.example.com/v1", "https://api.test.example.com.evil.net/v1/chat/completions")]
    public void Foreign_hosts_subdomains_and_port_changes_are_refused(string configured, string requested)
    {
        // illegal: 他域 / 子域 / 端口漂移 / 域名后缀伪装
        Assert.False(Whitelist(configured).IsAllowed(new Uri(requested)));
    }

    [Fact]
    public void Blank_and_non_http_entries_are_skipped_not_fatal()
    {
        // boundary: 模板空行/非 http(s) 不参与白名单也不崩溃
        var whitelist = Whitelist("", "ftp://x.example.com", "https://ok.example.com");

        Assert.Contains("ok.example.com", whitelist.Authorities);
        Assert.Single((System.Collections.IEnumerable)whitelist.Authorities);
        Assert.True(whitelist.IsAllowed(new Uri("https://ok.example.com/v1/chat/completions")));
    }

    [Fact]
    public void Relative_uri_is_never_allowed()
    {
        // illegal: 相对 URI（UriKind 相对无法出网，防御性拒绝）
        Assert.False(Whitelist("https://ok.example.com").IsAllowed(
            new Uri("/chat/completions", UriKind.Relative)));
    }

    [Fact]
    public void Providers_projection_contains_every_configured_authority()
    {
        // legal: 从 provider 行投影
        var whitelist = LlmDomainWhitelist.FromProviders(new[]
        {
            LlmFixtures.TestProvider(baseUrl: "https://a.example.com/v1"),
            LlmFixtures.TestProvider(id: "b", baseUrl: "https://b.example.com/v1"),
        });

        Assert.Equal(2, whitelist.Authorities.Count);
        Assert.True(whitelist.IsAllowed(new Uri("https://a.example.com/v1/messages")));
        Assert.True(whitelist.IsAllowed(new Uri("https://b.example.com/v1/chat/completions")));
    }
}
