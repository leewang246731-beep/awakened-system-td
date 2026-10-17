namespace EraCore.Llm.L2;

/// <summary>
/// Synthesizes the L2 provider row (T-137): an OpenAI-protocol
/// <see cref="LlmProviderConfig"/> pointing at the local llama.cpp server
/// (<c>http://127.0.0.1:{port}/v1</c>). The 47-t129 spike (§2.5) proved this
/// endpoint is wire-compatible with the existing OpenAI protocol lane —
/// ZERO protocol changes: the same <see cref="LlmGateway"/> route serves L2
/// and L3, so every gateway guarantee (whitelist, dual timeout, retry
/// matrix, call log) applies to the local lane unchanged.
/// </summary>
public static class L2ProviderRow
{
    /// <summary>
    /// Builds the L2 row for one port. Facts frozen: temperature 0 (spike
    /// determinism口径), max_tokens 200 (spike §2.1), stream off (whole-JSON
    /// reduction — the wave-gap lane does not need SSE), single history
    /// round, retries 0 (the queue's budget owns the deadline — a gateway
    /// retry would silently double it), context budget 4096 tokens.
    /// </summary>
    public static LlmProviderConfig Build(int port = L2Facts.DefaultPort) => new()
    {
        Id = L2Facts.ProviderRowId,
        Name = "L2 本地军师（llama.cpp）",
        Protocol = LlmProtocol.OpenAi,
        BaseUrl = $"http://127.0.0.1:{port}/v1",
        Model = L2Facts.ProviderModelId,
        Enable = true,
        Temperature = 0d,
        MaxTokens = 200, // ci-allow: R7 T-137 47-t129 §2.1 spike 实测口径（工程域常量）
        RequestTimeoutS = 60, // ci-allow: R7 T-137 波间整答预算上限（47-t129 §2.4 热答上界 22.7s 的工程余量）
        ConnectTimeoutS = 10, // ci-allow: R7 T-137 回环连接超时（本机通道工程域常量）
        Retries = 0,
        HistoryRounds = 1,
        ContextBudgetTokens = 4096, // ci-allow: R7 T-137 47-t129 §2.4 前缀缓存实测 prompt ~1781 tok（工程域常量）
        UseStream = false,
        Note = "本地推理通道：数据不出本机（127.0.0.1 回环）。",
    };

    /// <summary>
    /// The constant marker credential the L2 lane stores in
    /// <c>ISecretStore</c> so the BYOK key gate passes. Constant marker, not
    /// key material — llama-server ignores Authorization (spike §2.5 零改动
    /// 对接的配套事实). Storing it keeps the gateway's key-presence gate
    /// single-sourced instead of an L2 bypass branch (RL6 同源纪律).
    /// </summary>
    public static string LaneKeyMarker => L2Facts.LocalLaneKeyMarker;

    /// <summary>
    /// llama-server launch arguments for the local lane (spike §1.2 参数 +
    /// §3.2③ 强制配套: 单槽小上下文压内存; §3.2④ <c>--jinja</c> 必开——工具
    /// 调用解析依赖). The presentation process host is the ONLY consumer —
    /// EraCore never spawns processes (零进程依赖).
    /// </summary>
    /// <param name="modelAbsolutePath">Absolute path of the GGUF file (host-resolved — data dir or override).</param>
    /// <param name="port">Local port.</param>
    public static string[] ServerArguments(string modelAbsolutePath, int port = L2Facts.DefaultPort) =>
    [
        "-m", modelAbsolutePath,
        "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-c", L2Facts.ServerContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-np", L2Facts.ServerParallelSlots.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--jinja",
        "--host", "127.0.0.1",
    ];
}
