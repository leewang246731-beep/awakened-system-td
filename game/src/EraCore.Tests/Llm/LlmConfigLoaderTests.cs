using System.Text.Json;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// llm.config.json 装载与校验（F-080）：schema 字段、api_key 拒收、枚举严格、
/// 版本/激活行/数值域与“禁用模板可空行”规则。
/// </summary>
// [F-080] C-16 T-072：BYOK 配置装载
public class LlmConfigLoaderTests
{
    private const string ValidJson =
        """
        {
          "version": 1, "active_provider": "zhipu-glm",
          "providers": [
            { "id": "zhipu-glm", "name": "智谱 GLM", "protocol": "openai",
              "base_url": "https://open.bigmodel.cn/api/paas/v4", "model": "glm-5.3", "enable": true,
              "temperature": 0.7, "max_tokens": 1024, "request_timeout_s": 30, "connect_timeout_s": 10,
              "retries": 2, "history_rounds": 10, "context_budget_tokens": 16000, "use_stream": true,
              "note": "n" },
            { "id": "custom-relay", "name": "自定义", "protocol": "anthropic",
              "base_url": "", "model": "", "enable": false, "temperature": 0.7, "max_tokens": 1024,
              "request_timeout_s": 30, "connect_timeout_s": 10, "retries": 2, "history_rounds": 10,
              "context_budget_tokens": 16000, "use_stream": true, "note": "" }
          ],
          "logging": { "show_call_log": true, "log_conversation": false, "log_retention": 50 }
        }
        """;

    [Fact]
    public void Valid_document_loads_with_all_fields()
    {
        // legal: §11.6 样例全文（key 字段缺席即正确形态）
        var result = LlmConfigLoader.Load(ValidJson);

        Assert.True(result.Ok, result.ToString());
        var config = result.Config!;
        Assert.Equal(1, config.Version);
        Assert.Equal("zhipu-glm", config.ActiveProvider);
        Assert.Equal(2, config.Providers.Count);
        var zhipu = config.Providers[0];
        Assert.Equal(LlmProtocol.OpenAi, zhipu.Protocol);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", zhipu.BaseUrl);
        Assert.Equal("glm-5.3", zhipu.Model);
        Assert.True(zhipu.Enable);
        Assert.Equal(0.7, zhipu.Temperature);
        Assert.Equal(1024, zhipu.MaxTokens);
        Assert.True(zhipu.UseStream);
        Assert.Equal(LlmProtocol.Anthropic, config.Providers[1].Protocol);
        Assert.True(config.Logging.ShowCallLog);
        Assert.False(config.Logging.LogConversation);
        Assert.Equal(50, config.Logging.LogRetention);
    }

    [Fact]
    public void Non_empty_api_key_anywhere_is_rejected_before_any_parsing()
    {
        // illegal: 配置文件永不存 key（RL5/F-084）——带值即拒
        var json = ValidJson.Replace(
            "\"model\": \"glm-5.3\"", "\"model\": \"glm-5.3\", \"api_key\": \"sk-should-not-be-here-123456\"");

        var result = LlmConfigLoader.Load(json);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("api_key"));
        Assert.Null(result.Config);
    }

    [Fact]
    public void Empty_api_key_field_is_tolerated()
    {
        // boundary: §11.6 样例自带空 "api_key": "" ——空值不触发拒收
        var json = ValidJson.Replace(
            "\"model\": \"glm-5.3\"", "\"model\": \"glm-5.3\", \"api_key\": \"\"");

        var result = LlmConfigLoader.Load(json);

        Assert.True(result.Ok, result.ToString());
    }

    [Theory]
    [InlineData("\"protocol\": \"openai\"", "\"protocol\": \"oai-x\"", "protocol")]
    [InlineData("\"version\": 1", "\"version\": 2", "version")]
    [InlineData("\"active_provider\": \"zhipu-glm\"", "\"active_provider\": \"ghost\"", "active_provider")]
    [InlineData("\"id\": \"custom-relay\"", "\"id\": \"zhipu-glm\"", "重复")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "ftp://x.example.com", "http")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "https://u:p@a.example.com", "userinfo")]
    [InlineData("\"temperature\": 0.7", "\"temperature\": 9.9", "temperature")]
    [InlineData("\"max_tokens\": 1024", "\"max_tokens\": 0", "max_tokens")]
    [InlineData("\"model\": \"glm-5.3\"", "\"model\": \"\"", "model")]
    public void Schema_violations_are_reported_as_errors(
        string original, string mutated, string expectedFragment)
    {
        // illegal: 逐项违例（首处命中即测该规则）
        var json = ValidJson.Replace(original, mutated);

        var result = LlmConfigLoader.Load(json);

        Assert.False(result.Ok, $"突变 {mutated} 应当被判为非法");
        Assert.Contains(result.Errors, e => e.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Enabled_provider_with_blank_base_url_is_rejected()
    {
        // illegal: 启用行缺 base_url（模板空行被强行启用）
        var json = ValidJson
            .Replace("\"base_url\": \"\", \"model\": \"\", \"enable\": false",
                     "\"base_url\": \"\", \"model\": \"\", \"enable\": true")
            .Replace("\"active_provider\": \"zhipu-glm\"", "\"active_provider\": \"custom-relay\"");

        var result = LlmConfigLoader.Load(json);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("base_url"));
    }

    [Fact]
    public void Disabled_template_rows_may_stay_blank()
    {
        // boundary: 预置模板 disabled 空 base_url/model 合法（装载层）
        var result = LlmConfigLoader.Load(ValidJson);

        Assert.True(result.Ok, result.ToString());
        var custom = result.Config!.Providers[1];
        Assert.Equal(string.Empty, custom.BaseUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"version\": 1}")]
    public void Unusable_documents_fail_with_explicit_errors(string json)
    {
        // illegal: 空/非 JSON/缺字段
        var result = LlmConfigLoader.Load(json);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Errors);
        Assert.Null(result.Config);
    }

    [Fact]
    public void Preset_default_config_passes_validation_and_roundtrips()
    {
        // legal: 预置默认配置可序列化→重载（T-077 持久化路径）
        var preset = LlmProviderPresets.DefaultConfig();
        Assert.Empty(LlmConfigLoader.ValidateConfig(preset));

        var json = JsonSerializer.Serialize(preset, LlmJsonContext.Default.LlmConfig);
        var result = LlmConfigLoader.Load(json);

        Assert.True(result.Ok, result.ToString());
        Assert.Equal("zhipu-glm", result.Config!.ActiveProvider);
        Assert.Equal(3, result.Config.Providers.Count);
    }

    [Fact]
    public void Loaded_provider_rows_carry_no_key_surface_at_all()
    {
        // illegal: 反序列化产物没有任何 key 属性可被误读（schema 层面不存在）
        var result = LlmConfigLoader.Load(ValidJson);

        Assert.True(result.Ok);
        using var roundTrip = JsonDocument.Parse(
            JsonSerializer.Serialize(result.Config!, LlmJsonContext.Default.LlmConfig));
        Assert.False(HasPropertyNamed(roundTrip.RootElement, "api_key"));
    }

    private static bool HasPropertyNamed(JsonElement node, string name)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.NameEquals(name) || HasPropertyNamed(property.Value, name))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    if (HasPropertyNamed(item, name))
                    {
                        return true;
                    }
                }

                break;
        }

        return false;
    }
}
