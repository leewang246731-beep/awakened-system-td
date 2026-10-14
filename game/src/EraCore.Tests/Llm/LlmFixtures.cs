using EraCore.Llm;

namespace EraCore.Tests.Llm;

// [C-16] T-072 共享夹具：双协议 SSE/非流式样例、探针工具表、测试 provider 行。
// 形状对齐真实供应商 wire 格式（OpenAI choices/delta、Anthropic content_block_*）；
// 全部离线数据。纯夹具类，不含测试方法（T3 不适用）。
public static class LlmFixtures
{
    /// <summary>K-3 投影形状的 BuildPlan 工具表（OpenAI 形，Anthropic 协议内转换）。</summary>
    public const string BuildPlanToolsJson =
        """
        [{"type":"function","function":{"name":"BuildPlan","description":"按坐标批量建造塔","parameters":{"type":"object","properties":{"tower_id":{"type":"string"},"cells":{"type":"array","items":{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"}}}}},"required":["tower_id","cells"]}}}]
        """;

    /// <summary>OpenAI SSE 全量样例：reasoning_content 双帧 + content + 分片 tool_calls + [DONE]。</summary>
    public const string OpenAiSse =
        """
        data: {"id":"x","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"分析军令：东门，两座弩塔。"},"finish_reason":null}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"reasoning_content":"查看塔表。"},"finish_reason":null}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"content":"收到，正在规划建造。"},"finish_reason":null}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"BuildPlan","arguments":""}}]},"finish_reason":null}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"tower_id\":"}}]},"finish_reason":null}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"crossbow_t2\",\"cells\":[{\"x\":3,\"y\":7},{\"x\":3,\"y\":9}]}"}}]},"finish_reason":null}]}

        data: {"id":"x","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

        data: [DONE]

        """;

    /// <summary>Anthropic SSE 全量样例：text + tool_use(input_json_delta 分片) + message_stop。</summary>
    public const string AnthropicSse =
        """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","model":"m"}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"收到。"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"BuildPlan","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"tower_id\":\"cross"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"bow_t2\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"}}

        event: message_stop
        data: {"type":"message_stop"}
        """;

    /// <summary>OpenAI 非流式回复（含 reasoning_content 与完整 tool_calls）。</summary>
    public const string OpenAiNonStream =
        """
        {"id":"x","choices":[{"index":0,"message":{"role":"assistant","content":"","reasoning_content":"思考中","tool_calls":[{"id":"c1","type":"function","function":{"name":"BuildPlan","arguments":"{\"tower_id\":\"arrow_t1\",\"cells\":[{\"x\":1,\"y\":2}]}"}}]},"finish_reason":"tool_calls"}]}
        """;

    /// <summary>Anthropic 非流式回复（text + tool_use block）。</summary>
    public const string AnthropicNonStream =
        """
        {"id":"msg","content":[{"type":"text","text":"好的"},{"type":"tool_use","id":"t","name":"BuildPlan","input":{"tower_id":"arrow_t1","cells":[{"x":1,"y":2}]}}],"stop_reason":"tool_use"}
        """;

    /// <summary>GLM 1315 错误体（Coding Plan 密钥误用）。</summary>
    public const string ZhipuCodingPlanBody =
        """{"error":{"code":"1315","message":"该 API Key 仅支持官方编程工具白名单使用"}}""";

    /// <summary>GLM 1301 错误体（内容安全拦截）。</summary>
    public const string ZhipuContentBlockedBody =
        """{"error":{"code":"1301","message":"content filtered by safety policy"}}""";

    /// <summary>MiMo “Unsupported model”错误体（模型 ID 大写坑，§11.7）。</summary>
    public const string MimoUnsupportedModelBody =
        """{"error":{"message":"Unsupported model: MIMO-V2.6-PRO","type":"invalid_request_error"}}""";

    /// <summary>OpenAI SSE 多工具并发交错样例（T-091 矩阵）：index 1 先到、index 0/1 参数交错、空串参数片、name 重复下发、usage 尾帧。</summary>
    public const string OpenAiSseMultiToolInterleaved =
        """
        data: {"id":"x","choices":[{"index":0,"delta":{"reasoning_content":"两步：先建塔。"}}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"content":"收到。"}}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"reasoning_content":"再出售旧塔。"}}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"content":"正在执行。"}}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"SellTower","arguments":""}}]}},{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"BuildPlan","arguments":""}}]}}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"tower_id\":"}}]}},{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"tower_id\":\"cross"}}]}}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"BuildPlan","arguments":"bow_t2\",\"cells\":[{\"x\":3,\"y\":7}]}"}}]}},{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"\"arrow_t1\"}"}}]}}]}

        data: {"id":"x","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

        data: {"id":"x","choices":[{"index":0,"delta":{}}],"usage":{"prompt_tokens":128,"completion_tokens":64,"total_tokens":192}}

        data: [DONE]

        """;

    /// <summary>OpenAI SSE 乱序 index 样例（T-091 矩阵）：index=1 片段先于 index=0 到达——归约仍按最小 index 取首工具。</summary>
    public const string OpenAiSseIndexOutOfOrder =
        """
        data: {"id":"x","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"name":"SellTower","arguments":"{\"tower_id\":\"arrow_t1\"}"}}]}}]}

        data: {"id":"x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"BuildPlan","arguments":"{\"tower_id\":\"crossbow_t2\",\"cells\":[{\"x\":1,\"y\":1}]}"}}]}}]}

        data: {"id":"x","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

        data: [DONE]

        """;

    /// <summary>
    /// 完整可调用的测试 provider 行（默认 openai 协议、启用、虚拟域名——
    /// 非真实端点，绝不真连）。
    /// </summary>
    public static LlmProviderConfig TestProvider(
        string id = "test-provider",
        bool enable = true,
        string baseUrl = "https://api.test.example.com/v1",
        LlmProtocol protocol = LlmProtocol.OpenAi,
        bool useStream = true,
        int retries = 0,
        int historyRounds = 10,
        int contextBudgetTokens = 16000) => new()
    {
        Id = id,
        Name = "测试服务",
        Protocol = protocol,
        BaseUrl = baseUrl,
        Model = "test-model",
        Enable = enable,
        Temperature = 0.7,
        MaxTokens = 1024,
        RequestTimeoutS = 30,
        ConnectTimeoutS = 10,
        Retries = retries,
        HistoryRounds = historyRounds,
        ContextBudgetTokens = contextBudgetTokens,
        UseStream = useStream,
        Note = "测试行",
    };

    /// <summary>两 provider 配置（各自锁域）：test-provider 激活，second 备用。</summary>
    public static LlmConfig TestConfig(
        LlmProviderConfig? active = null,
        LlmProviderConfig? second = null,
        string? activeId = null)
    {
        active ??= TestProvider();
        second ??= TestProvider(id: "second-provider", baseUrl: "https://relay.test.example.com/v1");
        return new LlmConfig
        {
            Version = 1,
            ActiveProvider = activeId ?? active.Id,
            Providers = [active, second],
            Logging = new LlmLoggingConfig(),
        };
    }
}
