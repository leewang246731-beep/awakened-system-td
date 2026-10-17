using System.Text.Json;
using EraCore.Llm;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// TTS 语音通道预留契约（T-138, F-140）：状态位门序（Disabled→
/// NotConfigured→MissingKey→NotImplemented，终点永不谎报成功）、每个拒绝
/// 态零 provider 调用（无 Key 零请求）、配置面默认 off 与校验规则。
/// 不做真实 TTS 调用——mimo TTS 端点未开通，预留=接口与配置面。
/// </summary>
// [F-140][C-16] T-138：TTS 通道预留状态位与零请求
public class TtsChannelTests
{
    /// <summary>
    /// Fake <see cref="ITtsProvider"/> counting every call — the zero-request
    /// oracle (mocking the PORT is sanctioned, CS-3.4; the channel itself is
    /// never mocked). Synthetic audio bytes only, never real material.
    /// </summary>
    private sealed class CountingTtsProvider : ITtsProvider
    {
        public int CallCount { get; private set; }

        public TtsProviderEndpoint Endpoint => TtsProviderPresets.MimoV25Tts();

        public Task<TtsSpeechResponse> SpeakAsync(
            TtsSpeechRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new TtsSpeechResponse(TtsAudioFormat.Wav, [1, 2, 3]));
        }
    }

    private static LlmConfig ConfigWith(
        bool enable,
        string provider = "xiaomi-mimo",
        string model = "mimo-v2.5-tts",
        string voice = "narrator-a") => new()
    {
        Version = LlmConfigLoader.SupportedVersion,
        ActiveProvider = "xiaomi-mimo",
        Providers = [LlmProviderPresets.XiaomiMiMo()],
        Tts = new LlmTtsConfig
        {
            Enable = enable,
            Provider = provider,
            Model = model,
            Voice = voice,
        },
    };

    private static TtsSpeechRequest SampleRequest() => new()
    {
        Model = TtsProviderPresets.MimoTtsModelId,
        Input = "生产链已接通。",
        Voice = "narrator-a",
    };

    // ------------------------------------------------ 状态位门序（零请求）

    [Fact]
    public async Task Default_off_config_refuses_with_disabled_and_zero_provider_calls()
    {
        // legal: 出厂默认（tts 缺省 off）→ Disabled，文案明说零网络
        var provider = new CountingTtsProvider();
        var channel = new TtsChannel(LlmProviderPresets.DefaultConfig(), new InMemorySecretStore(), provider);

        var result = await channel.SpeakAsync(SampleRequest());

        Assert.Equal(TtsChannelStatus.Disabled, result.Status);
        Assert.False(result.IsOk);
        Assert.True(result.IsRefusal);
        Assert.Null(result.Audio);
        Assert.Equal(0, provider.CallCount);
        Assert.Contains("未发出任何网络请求", result.MessageZh);
        Assert.NotEmpty(result.MessageEn);
    }

    [Fact]
    public async Task Enabled_but_incomplete_section_refuses_with_not_configured()
    {
        // illegal: 三种不全形态（provider 空/model 空/provider 行不存在）都归 NotConfigured
        foreach (var config in new[]
                 {
                     ConfigWith(enable: true, provider: ""),
                     ConfigWith(enable: true, model: ""),
                     ConfigWith(enable: true, provider: "no-such-provider"),
                 })
        {
            var provider = new CountingTtsProvider();
            var channel = new TtsChannel(config, new InMemorySecretStore(), provider);

            var result = await channel.SpeakAsync(SampleRequest());

            Assert.Equal(TtsChannelStatus.NotConfigured, result.Status);
            Assert.Null(result.Audio);
            Assert.Equal(0, provider.CallCount);
        }
    }

    [Fact]
    public async Task Keyless_but_fully_configured_refuses_with_missing_key_and_zero_provider_calls()
    {
        // illegal: 无 Key 零请求（BYOK 同构——无玩家 Key 绝不出网）
        var provider = new CountingTtsProvider();
        var secrets = new InMemorySecretStore(); // 无 xiaomi-mimo 条目
        var channel = new TtsChannel(ConfigWith(enable: true), secrets, provider);

        var result = await channel.SpeakAsync(SampleRequest());

        Assert.Equal(TtsChannelStatus.MissingKey, result.Status);
        Assert.Null(result.Audio);
        Assert.Equal(0, provider.CallCount);
        Assert.Contains("API Key", result.MessageZh);
    }

    [Fact]
    public async Task Fully_enabled_with_key_stops_at_not_implemented_and_never_fakes_success()
    {
        // boundary: 配置+Key 全齐 → 预留终点站 NotImplemented——端点未开通，
        // 绝不半实现、绝不谎报成功；provider 端口同样零调用
        var provider = new CountingTtsProvider();
        var secrets = new InMemorySecretStore();
        secrets.Store("xiaomi-mimo", "synthetic-tts-key-000111"); // 测试合成值（RL5）
        var channel = new TtsChannel(ConfigWith(enable: true), secrets, provider);

        var result = await channel.SpeakAsync(SampleRequest());

        Assert.Equal(TtsChannelStatus.NotImplemented, result.Status);
        Assert.False(result.IsOk);
        Assert.Null(result.Audio);
        Assert.Equal(0, provider.CallCount);
        Assert.Contains("预留", result.MessageZh);
        Assert.Contains("later release", result.MessageEn);

        // Key 值不进任何文案（RL5）
        Assert.DoesNotContain("synthetic-tts-key-000111", result.MessageZh);
        Assert.DoesNotContain("synthetic-tts-key-000111", result.MessageEn);
    }

    [Fact]
    public async Task Ok_status_is_unreachable_in_this_build()
    {
        // boundary: 四门全过也到不了 Ok——本版本不存在成功路径（预留声明）
        var secrets = new InMemorySecretStore();
        secrets.Store("xiaomi-mimo", "synthetic-tts-key-000111");
        var channel = new TtsChannel(ConfigWith(enable: true), secrets);

        var result = await channel.SpeakAsync(SampleRequest());

        Assert.NotEqual(TtsChannelStatus.Ok, result.Status);
        Assert.False(result.IsOk);
    }

    [Fact]
    public async Task Null_request_is_rejected()
    {
        // illegal: 空请求抛 ArgumentNullException（契约同 LlmGateway）
        var channel = new TtsChannel(LlmProviderPresets.DefaultConfig(), new InMemorySecretStore());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => channel.SpeakAsync(null!));
    }

    [Fact]
    public void Null_config_or_secrets_throw_at_construction()
    {
        // illegal: 构造参数防御
        Assert.Throws<ArgumentNullException>(
            () => new TtsChannel(null!, new InMemorySecretStore()));
        Assert.Throws<ArgumentNullException>(
            () => new TtsChannel(LlmProviderPresets.DefaultConfig(), null!));
    }

    // ------------------------------------------------ 配置面（默认 off/装载/校验）

    [Fact]
    public void Absent_tts_section_parses_to_default_off()
    {
        // legal: 旧版配置文件（无 tts 段）装载后 → 默认 off 段（Normalize 补全）
        const string legacyJson =
            """
            {
              "version": 1, "active_provider": "zhipu-glm",
              "providers": [
                { "id": "zhipu-glm", "name": "智谱 GLM", "protocol": "openai",
                  "base_url": "https://open.bigmodel.cn/api/paas/v4", "model": "glm-5.3", "enable": true,
                  "temperature": 0.7, "max_tokens": 1024, "request_timeout_s": 30, "connect_timeout_s": 10,
                  "retries": 2, "history_rounds": 10, "context_budget_tokens": 16000, "use_stream": true,
                  "note": "n" }
              ],
              "logging": { "show_call_log": true, "log_conversation": false, "log_retention": 50 }
            }
            """;
        Assert.DoesNotContain("\"tts\"", legacyJson);

        var loaded = LlmConfigLoader.Load(legacyJson);

        Assert.True(loaded.Ok, loaded.ToString());
        Assert.NotNull(loaded.Config!.Tts);
        Assert.False(loaded.Config.Tts.Enable);
        Assert.Equal(TtsProviderPresets.MimoTtsModelId, loaded.Config.Tts.Model);
    }

    [Fact]
    public void Tts_section_roundtrips_through_the_config_schema()
    {
        // legal: tts 段字段经 llm.config.json 往返全等（含预留模型默认值）
        var config = ConfigWith(enable: true);

        string json = JsonSerializer.Serialize(config, LlmJsonContext.Default.LlmConfig);
        var loaded = LlmConfigLoader.Load(json);

        Assert.True(loaded.Ok, loaded.ToString());
        Assert.Equal(config.Tts, loaded.Config!.Tts);
    }

    [Fact]
    public void Enabled_tts_with_incomplete_references_is_a_config_error()
    {
        // illegal: 启用态 tts 引用不全 → 装载报错（配置面诚实）
        var config = ConfigWith(enable: true, provider: "");

        var errors = LlmConfigLoader.ValidateConfig(config);

        Assert.Contains(errors, e => e.Contains("tts 已启用但 provider/model/voice 不完整"));
    }

    [Fact]
    public void Enabled_tts_referencing_an_unknown_provider_is_a_config_error()
    {
        // illegal: tts.provider 指向不存在的 provider 行 → 装载报错
        var config = ConfigWith(enable: true, provider: "ghost-provider");

        var errors = LlmConfigLoader.ValidateConfig(config);

        Assert.Contains(errors, e => e.Contains("tts.provider 'ghost-provider' 不在 providers 列表中"));
    }

    [Fact]
    public void Disabled_tts_may_stay_blank()
    {
        // legal: 默认 off 的 tts 段（字段全空）零约束通过——与禁用模板行同律
        var config = LlmProviderPresets.DefaultConfig() with
        {
            Tts = new LlmTtsConfig(),
        };

        Assert.Empty(LlmConfigLoader.ValidateConfig(config));
    }

    // ------------------------------------------------ 设置页文案面

    [Fact]
    public void Reserved_settings_copy_states_the_reservation_verbatim()
    {
        // legal: 设置页文案=任务卡 verbatim 口径（渲染层原样上屏）
        Assert.Equal("语音（预留）", SettingsFormState.TtsReservedCaptionZh);
        Assert.Equal("语音通道已预留，功能随后续版本开放", SettingsFormState.TtsReservedNoticeZh);
    }
}
