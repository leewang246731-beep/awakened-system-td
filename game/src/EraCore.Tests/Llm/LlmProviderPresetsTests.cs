using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 预置模板（F-082）：智谱 GLM 默认+小米 MiMo 已实测+自定义+本地 LM Studio，
/// 与 §11.6 登记值逐字段对齐。
/// </summary>
// [F-082] C-16 T-072：双预置模板
public class LlmProviderPresetsTests
{
    [Fact]
    public void Zhipu_template_matches_design_schema()
    {
        // legal: §11.6 智谱行
        var zhipu = LlmProviderPresets.ZhipuGlm();

        Assert.Equal("zhipu-glm", zhipu.Id);
        Assert.Equal(LlmProtocol.OpenAi, zhipu.Protocol);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", zhipu.BaseUrl);
        Assert.Equal("glm-5.3", zhipu.Model);
        Assert.True(zhipu.Enable);
        Assert.Contains("1315", zhipu.Note);
    }

    [Fact]
    public void Mimo_template_matches_live_verified_endpoint()
    {
        // legal: §11.6 MiMo 行（docs/engineering/09-env-locked.md 实测口径）
        var mimo = LlmProviderPresets.XiaomiMiMo();

        Assert.Equal("xiaomi-mimo", mimo.Id);
        Assert.Equal(LlmProtocol.OpenAi, mimo.Protocol);
        Assert.Equal("https://api.xiaomimimo.com/v1", mimo.BaseUrl);
        Assert.Equal("mimo-v2.6-pro", mimo.Model);
        Assert.False(mimo.Enable); // 默认关（L3 可选，默认 off）
        Assert.Contains("reasoning_content", mimo.Note);
        Assert.Contains("小写", mimo.Note);
    }

    [Fact]
    public void Custom_and_lmstudio_templates_ship_disabled()
    {
        // boundary: 自定义/本地模板默认禁用，不参与出网
        Assert.False(LlmProviderPresets.CustomRelay().Enable);
        Assert.Equal(string.Empty, LlmProviderPresets.CustomRelay().BaseUrl);

        var lmStudio = LlmProviderPresets.LocalLmStudio();
        Assert.False(lmStudio.Enable);
        Assert.Equal("http://localhost:1234/v1", lmStudio.BaseUrl); // 仅回环
    }

    [Fact]
    public void Default_config_activates_zhipu_with_three_rows()
    {
        // legal: 出厂配置 = §11.6 样例文档
        var config = LlmProviderPresets.DefaultConfig();

        Assert.Equal(1, config.Version);
        Assert.Equal("zhipu-glm", config.ActiveProvider);
        Assert.Equal(3, config.Providers.Count);
        Assert.All(config.Providers, p => Assert.False(string.IsNullOrWhiteSpace(p.Note)));
    }

    [Fact]
    public void Every_preset_passes_full_config_validation()
    {
        // boundary: 模板行数值域全部合法（enabled 行尤其）
        Assert.Empty(LlmConfigLoader.ValidateConfig(new LlmConfig
        {
            Version = 1,
            ActiveProvider = "xiaomi-mimo",
            Providers =
            [
                LlmProviderPresets.XiaomiMiMo() with { Enable = true },
                LlmProviderPresets.LocalLmStudio() with { Model = "local-model", Enable = true },
            ],
            Logging = new LlmLoggingConfig(),
        }));
    }
}
