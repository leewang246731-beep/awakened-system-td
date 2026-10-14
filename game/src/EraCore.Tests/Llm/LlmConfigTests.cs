using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// LlmConfig 记录行为：provider 查找、BaseUri 解析、预置模板与 §11.6 对齐
/// （F-082 双预置模板 + 四模板下拉行）。
/// </summary>
// [F-080][F-082] C-16 T-072：配置记录与预置模板
public class LlmConfigTests
{
    [Fact]
    public void FindProvider_matches_exact_id_only()
    {
        // legal: 精确匹配
        var config = LlmFixtures.TestConfig();

        Assert.Equal("test-provider", config.FindProvider("test-provider")!.Id);
        Assert.Null(config.FindProvider("TEST-PROVIDER"));
        Assert.Null(config.FindProvider("missing"));
    }

    [Fact]
    public void BaseUri_parses_for_valid_rows_and_throws_for_blank_ones()
    {
        // legal/boundary: 启用行可解析；模板空行抛 UriFormatException（由校验前置拦截）
        var provider = LlmFixtures.TestProvider();

        Assert.Equal("https://api.test.example.com/v1", provider.BaseUri.AbsoluteUri);

        var blank = LlmFixtures.TestProvider(baseUrl: "");
        Assert.Throws<UriFormatException>(() => _ = blank.BaseUri);
    }

    [Fact]
    public void Preset_roundtrip_through_sourcegen_context_keeps_wire_names()
    {
        // legal: snake_case wire 名稳定（llm.config.json 由 T-077 读写）
        var json = System.Text.Json.JsonSerializer.Serialize(
            LlmProviderPresets.XiaomiMiMo(), LlmJsonContext.Default.LlmProviderConfig);

        Assert.Contains("\"base_url\":", json);
        Assert.Contains("\"max_tokens\":", json);
        Assert.Contains("\"use_stream\":", json);
        Assert.Contains("\"protocol\":\"openai\"", json);
    }
}
