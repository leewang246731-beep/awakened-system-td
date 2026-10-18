using EraCore.Llm;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

/// <summary>
/// C-26 BYOK 设置表单纯逻辑（T-077 / F-081）：四模板下拉对齐 LlmProviderPresets、
/// 字段→LlmConfig 映射 upsert、校验矩阵（坏 URL/空 key 警示不阻保存/协议-端点
/// 匹配/数值域）、脏检查与序列化往返。
/// </summary>
// [F-081] C-26 T-077：BYOK 设置界面
public class SettingsFormStateTests
{
    // ---- 四模板下拉 ---------------------------------------------------------

    [Fact]
    public void Templates_are_exactly_the_four_design_presets()
    {
        // legal: §11.6 模板下拉四项 = 智谱 GLM / 小米 MiMo / 自定义 / 本地 LM Studio
        var templates = SettingsFormState.Templates;

        Assert.Equal(4, templates.Count);
        Assert.Equal(LlmProviderPresets.ZhipuGlm(), templates[0]);
        Assert.Equal(LlmProviderPresets.XiaomiMiMo(), templates[1]);
        Assert.Equal(LlmProviderPresets.CustomRelay(), templates[2]);
        Assert.Equal(LlmProviderPresets.LocalLmStudio(), templates[3]);

        // 四个 base_url 逐字对齐设计文档 §11.6
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", templates[0].BaseUrl);
        Assert.Equal("https://api.xiaomimimo.com/v1", templates[1].BaseUrl);
        Assert.Equal(string.Empty, templates[2].BaseUrl);
        Assert.Equal("http://localhost:1234/v1", templates[3].BaseUrl);
    }

    [Fact]
    public void SelectTemplate_fills_fields_and_preserves_logging_switches()
    {
        // legal: 下拉选模板 → 表单字段全量来自预置行；日志开关保持当前位置
        var state = new SettingsFormState();
        state.Edit(f => f with { ShowCallLog = false, LogRetention = 20 });

        state.SelectTemplate("local-lmstudio");

        Assert.Equal("local-lmstudio", state.Fields.ProviderId);
        Assert.Equal(LlmProtocol.OpenAi, state.Fields.Protocol);
        Assert.Equal("http://localhost:1234/v1", state.Fields.BaseUrl);
        Assert.Equal(LlmProviderPresets.LocalLmStudio().Note, state.Fields.Note);
        Assert.False(state.Fields.ShowCallLog, "日志开关不应被模板选择重置");
        Assert.Equal(20, state.Fields.LogRetention);
        Assert.True(state.IsDirty, "切换模板即离开基线");
    }

    [Theory]
    [InlineData("no-such-template")]
    [InlineData("")]
    [InlineData(null)]
    public void SelectTemplate_rejects_unknown_ids(string? templateId)
    {
        // illegal: 未知模板 id 必须显式拒绝
        var state = new SettingsFormState();

        Assert.Throws<ArgumentException>(() => state.SelectTemplate(templateId!));
    }

    // ---- 加载与映射 ---------------------------------------------------------

    [Fact]
    public void Fresh_state_loads_the_default_active_row()
    {
        // legal: 出厂态 = DefaultConfig 的 active 行（zhipu-glm）
        var state = new SettingsFormState();

        Assert.Equal("zhipu-glm", state.Fields.ProviderId);
        Assert.Equal("glm-5.3", state.Fields.Model);
        Assert.True(state.Fields.Enable);
        Assert.False(state.IsDirty);
        Assert.Contains("https://open.bigmodel.cn/api/paas/v4", state.DeclarationText());
    }

    [Fact]
    public void Load_by_id_restores_every_field_of_that_row()
    {
        // legal: 指定行加载 → 全字段（含 note 透传与日志开关）恢复
        var row = LlmProviderPresets.XiaomiMiMo() with
        {
            Temperature = 0.2,
            MaxTokens = 2048,
            HistoryRounds = 6,
            UseStream = false,
        };
        var config = new LlmConfig
        {
            Version = 1,
            ActiveProvider = row.Id,
            Providers = [LlmProviderPresets.ZhipuGlm(), row],
            Logging = new LlmLoggingConfig { ShowCallLog = false, LogRetention = 30 },
        };
        var state = new SettingsFormState();

        state.Load(config, "xiaomi-mimo");

        Assert.Equal(0.2, state.Fields.Temperature);
        Assert.Equal(2048, state.Fields.MaxTokens);
        Assert.Equal(6, state.Fields.HistoryRounds);
        Assert.False(state.Fields.UseStream);
        Assert.Equal(row.Note, state.Fields.Note);
        Assert.False(state.Fields.ShowCallLog);
        Assert.Equal(30, state.Fields.LogRetention);
        Assert.False(state.IsDirty);
    }

    [Fact]
    public void Load_falls_back_to_first_enabled_row_when_active_missing()
    {
        // boundary: active_provider 指向缺失行 → 取首个启用行（表单永不空载）
        var config = new LlmConfig
        {
            Version = 1,
            ActiveProvider = "gone",
            Providers = [LlmProviderPresets.CustomRelay(),
                LlmProviderPresets.XiaomiMiMo() with { Enable = true }],
            Logging = new LlmLoggingConfig(),
        };
        var state = new SettingsFormState();

        state.Load(config);

        Assert.Equal("xiaomi-mimo", state.Fields.ProviderId);
    }

    [Fact]
    public void BuildConfig_upserts_edited_row_and_appends_fourth_provider()
    {
        // legal: 编辑既有行=原位替换；选第四模板保存=追加 → providers=4
        var state = new SettingsFormState();
        state.Edit(f => f with { Temperature = 0.4, MaxTokens = 512 });

        var edited = state.BuildConfig(LlmProviderPresets.DefaultConfig());
        Assert.Equal(3, edited.Providers.Count);
        var zhipu = edited.FindProvider("zhipu-glm")!;
        Assert.Equal(0.4, zhipu.Temperature);
        Assert.Equal(512, zhipu.MaxTokens);
        Assert.Equal("zhipu-glm", edited.ActiveProvider);

        var state2 = new SettingsFormState();
        state2.SelectTemplate("local-lmstudio");
        state2.Edit(f => f with { Model = "local-model", Enable = true });

        var grown = state2.BuildConfig(edited);
        Assert.Equal(4, grown.Providers.Count); // smoke 判据 providers=4
        Assert.Equal("local-lmstudio", grown.ActiveProvider);
        var lmStudio = grown.FindProvider("local-lmstudio")!;
        Assert.Equal("http://localhost:1234/v1", lmStudio.BaseUrl);
        Assert.Equal("local-model", lmStudio.Model);
        Assert.True(lmStudio.Enable);
    }

    [Fact]
    public void BuildConfig_keeps_baseline_active_when_saving_a_disabled_row()
    {
        // boundary: 保存禁用行不改 active_provider（网关对禁用行零网络拒绝）
        var state = new SettingsFormState();
        state.SelectTemplate("xiaomi-mimo"); // 预置禁用行

        var saved = state.BuildConfig(LlmProviderPresets.DefaultConfig());

        Assert.Equal("zhipu-glm", saved.ActiveProvider);
        Assert.False(saved.FindProvider("xiaomi-mimo")!.Enable);
    }

    [Fact]
    public void BuildConfig_carries_the_reserved_tts_section_through_verbatim()
    {
        // [F-140][C-16] T-138 legal: tts 预留段非表单字段——保存逐字保留基线段，
        // 不因 BYOK 行编辑而丢失/重置
        var state = new SettingsFormState();
        state.Edit(f => f with { Temperature = 0.2 });
        var baseline = LlmProviderPresets.DefaultConfig() with
        {
            Tts = new LlmTtsConfig
            {
                Enable = false,
                Provider = "xiaomi-mimo",
                Model = "mimo-v2.5-tts",
                Voice = "narrator-a",
            },
        };

        var saved = state.BuildConfig(baseline);

        Assert.Equal(baseline.Tts, saved.Tts);
    }

    // ---- 校验矩阵 -----------------------------------------------------------

    public static TheoryData<string, bool, bool> BaseUrlRows => new()
    {
        // legal
        { "https://api.xiaomimimo.com/v1", true, false },
        { "http://localhost:1234/v1", true, false },
        { "https://open.bigmodel.cn/api/paas/v4", true, false },
        // boundary: 空白 base_url 仅在禁用行合法（预置模板即如此出厂）
        { "", false, false },
        // illegal
        { "", true, true },                                    // 启用但空
        { "   ", true, true },                                 // 启用但空白
        { "notaurl", true, true },                             // 非绝对地址
        { "api.example.com/v1", true, true },                  // 缺 scheme
        { "ftp://api.example.com/v1", true, true },            // 非 http(s)
        { "https://user:pass@api.example.com/v1", true, true }, // userinfo
        { "https://api.example.com/v1#frag", true, true },     // fragment
    };

    [Theory]
    [MemberData(nameof(BaseUrlRows))]
    public void Base_url_matrix_blocks_only_broken_values(string baseUrl, bool enable, bool expectError)
    {
        var state = new SettingsFormState();
        state.Edit(f => f with
        {
            BaseUrl = baseUrl,
            Enable = enable,
            Model = "m",
        });

        var review = state.Review(keyAvailable: true);

        Assert.Equal(expectError, review.Errors.Count > 0);
        if (expectError)
        {
            Assert.Contains(review.Errors, e => e.Contains("base_url"));
        }
    }

    [Fact]
    public void Missing_key_warns_but_never_blocks_save()
    {
        // boundary: §11.6 空 key 警示不阻保存
        var state = new SettingsFormState();

        var withoutKey = state.Review(keyAvailable: false);
        Assert.True(withoutKey.CanSave);
        Assert.Contains(withoutKey.Warnings, w => w.Contains("API Key"));

        var withKey = state.Review(keyAvailable: true);
        Assert.True(withKey.CanSave);
        Assert.DoesNotContain(withKey.Warnings, w => w.Contains("API Key"));
    }

    public static TheoryData<string, LlmProtocol, bool> ProtocolEndpointRows => new()
    {
        // legal: OpenAI 形端点配 openai 协议；服务根地址配 anthropic
        { "https://api.xiaomimimimo.com/v1", LlmProtocol.OpenAi, false },
        { "https://api.anthropic.com", LlmProtocol.Anthropic, false },
        { "https://api.xiaomimimo.com/anthropic", LlmProtocol.Anthropic, false },
        // boundary: anthropic 协议 + 以 /v1 结尾的 OpenAI 形 base_url → 疑似不匹配警示
        { "https://api.example.com/v1", LlmProtocol.Anthropic, true },
        { "https://api.example.com/V1/", LlmProtocol.Anthropic, true },
        // illegal 输入本身（坏 URL）不触发协议警示——那是 error 的职责
        { "notaurl", LlmProtocol.Anthropic, false },
    };

    [Theory]
    [MemberData(nameof(ProtocolEndpointRows))]
    public void Protocol_endpoint_match_warns_on_anthropic_over_v1_style_base(
        string baseUrl, LlmProtocol protocol, bool expectWarning)
    {
        var state = new SettingsFormState();
        state.Edit(f => f with { BaseUrl = baseUrl, Protocol = protocol, Model = "m" });

        var review = state.Review(keyAvailable: true);

        Assert.Equal(expectWarning,
            review.Warnings.Any(w => w.Contains("协议与端点")));
    }

    public static TheoryData<double, int, int, int, bool> NumericRangeRows => new()
    {
        // legal
        { 0.0, 1, 0, 1, false },
        { 2.0, 32768, 5, 50, false },
        // boundary: 区间端点合法
        { 0.7, 1024, 2, 50, false },
        // illegal
        { -0.1, 1024, 2, 50, true },     // temperature 下越界
        { 2.1, 1024, 2, 50, true },      // temperature 上越界
        { 0.7, 0, 2, 50, true },         // max_tokens 下越界
        { 0.7, 32769, 2, 50, true },     // max_tokens 上越界
        { 0.7, 1024, 6, 50, true },      // retries 上越界
        { 0.7, 1024, 2, 0, true },       // log_retention 下越界
    };

    [Theory]
    [MemberData(nameof(NumericRangeRows))]
    public void Numeric_range_matrix_matches_the_loader_rules(
        double temperature, int maxTokens, int retries, int logRetention, bool expectError)
    {
        var state = new SettingsFormState();
        state.Edit(f => f with
        {
            Temperature = temperature,
            MaxTokens = maxTokens,
            Retries = retries,
            LogRetention = logRetention,
        });

        var review = state.Review(keyAvailable: true);

        Assert.Equal(expectError, review.Errors.Count > 0);
    }

    [Fact]
    public void Enabled_row_with_blank_model_is_an_error()
    {
        // illegal: 启用行必须有模型
        var state = new SettingsFormState();
        state.Edit(f => f with { Model = "" });

        var review = state.Review(keyAvailable: true);

        Assert.False(review.CanSave);
        Assert.Contains(review.Errors, e => e.Contains("model"));
    }

    // ---- 脏检查与持久化 -----------------------------------------------------

    [Fact]
    public void Dirty_check_tracks_field_and_key_edits()
    {
        var state = new SettingsFormState();
        Assert.False(state.IsDirty);

        state.Edit(f => f with { Temperature = 0.9 });
        Assert.True(state.IsDirty);

        state.MarkPersisted();
        Assert.False(state.IsDirty);

        state.NotifyKeyEdited(); // 只改 Key（值不入纯逻辑）也算脏
        Assert.True(state.IsDirty);

        state.MarkPersisted();
        state.NotifyKeyCleared();
        Assert.True(state.IsDirty);
    }

    [Fact]
    public void Save_roundtrip_through_the_loader_restores_all_fields()
    {
        // legal: 编辑→序列化→LlmConfigLoader.Load→重新加载表单，全字段等值
        var state = new SettingsFormState();
        state.SelectTemplate("local-lmstudio");
        state.Edit(f => f with
        {
            Model = "local-roundtrip-model",
            Enable = true,
            Temperature = 0.3,
            MaxTokens = 2048,
            RequestTimeoutS = 45,
            ConnectTimeoutS = 15,
            Retries = 1,
            HistoryRounds = 6,
            ContextBudgetTokens = 8000,
            UseStream = false,
            ShowCallLog = false,
            LogConversation = true,
            LogRetention = 25,
        });

        var saved = state.BuildConfig(LlmProviderPresets.DefaultConfig());
        var json = SettingsFormState.Serialize(saved);

        var loaded = LlmConfigLoader.Load(json);
        Assert.True(loaded.Ok, loaded.ToString());

        var reloaded = new SettingsFormState();
        reloaded.Load(loaded.Config!, "local-lmstudio");
        Assert.Equal(state.Fields, reloaded.Fields);
        Assert.Equal(4, loaded.Config!.Providers.Count);
    }

    [Fact]
    public void Serialized_document_carries_no_key_surface()
    {
        // illegal 防线: 序列化产物永不携带 api_key 属性名（Key 只进 SecretStore）
        var state = new SettingsFormState();
        var json = SettingsFormState.Serialize(
            state.BuildConfig(LlmProviderPresets.DefaultConfig()));

        Assert.DoesNotContain("api_key", json);
    }

    // ---- 常驻声明 -----------------------------------------------------------

    [Fact]
    public void Declaration_inserts_the_current_base_url_and_fixed_wording()
    {
        // legal: F-081 常驻声明 = 固定文案 + 动态 base_url
        var withUrl = SettingsFormState.DeclarationFor("https://api.xiaomimimo.com/v1");
        Assert.Contains("https://api.xiaomimimo.com/v1", withUrl);
        Assert.Contains("密钥仅存本机", withUrl);
        Assert.Contains("本游戏不收取任何费用", withUrl);

        // boundary: 空 base_url → 明确占缺文案而非空括号
        var blank = SettingsFormState.DeclarationFor("  ");
        Assert.Contains(SettingsFormState.DeclarationBlankZh, blank);
        Assert.DoesNotContain("https://", blank);

        // 动态刷新：表单改 base_url 后声明跟随
        var state = new SettingsFormState();
        state.Edit(f => f with { BaseUrl = "http://localhost:1234/v1" });
        Assert.Contains("http://localhost:1234/v1", state.DeclarationText());
    }

    [Fact]
    public void Coding_plan_notice_keeps_the_whitelist_and_error_code_wording()
    {
        // boundary: §11.6 Coding Plan 白名单限制提示（错误码 1315 原文保留）
        Assert.Contains("Coding Plan", SettingsFormState.CodingPlanNoticeZh);
        Assert.Contains("白名单", SettingsFormState.CodingPlanNoticeZh);
        Assert.Contains("1315", SettingsFormState.CodingPlanNoticeZh);
    }

    [Fact] // [F-006] T-155 时长遥测披露行文案（数据流向声明三要素：本地/不上传/可删）
    public void Telemetry_notice_keeps_the_local_only_wording()
    {
        // legal: 三要素逐项在位——本地文件、零上传零联网、删除即清除
        Assert.Contains("本地", SettingsFormState.TelemetryNoticeCaptionZh);
        Assert.Contains("telemetry/durations.jsonl", SettingsFormState.TelemetryNoticeZh);
        Assert.Contains("不上传", SettingsFormState.TelemetryNoticeZh);
        Assert.Contains("不联网", SettingsFormState.TelemetryNoticeZh);
        Assert.Contains("删除", SettingsFormState.TelemetryNoticeZh);

        // boundary: 文案不述任何盘符/绝对路径（发布件任意注入盘——只述相对段）
        Assert.DoesNotContain(@":\", SettingsFormState.TelemetryNoticeZh);
        Assert.DoesNotContain("user://", SettingsFormState.TelemetryNoticeZh);
    }
}
