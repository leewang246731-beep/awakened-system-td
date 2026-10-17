using System.Text.Json;
using EraCore.Agent.Pipeline;
using EraCore.Llm;
using EraCore.Llm.L2;
using EraCore.Secrets;
using Xunit;

namespace EraCore.Tests.Llm.L2;

// [F-078] [F-163] T-137 L2 provider 行：事实冻结 + 网关真实对接面（spike
// §2.5「零协议改动」的回归证明——合成行经 LlmGateway 走 OpenAI 协议，mock
// wire 端口回 47-t129 §2.3 同形 tool_call 应答，断言候选动作 JSON 与调用
// 日志照记）。端口 mock 合规（CS-3.4）；被测系统=合成行+网关+路由器。
public class L2ProviderRowTests
{
    // ------------------------------------------------ 行事实

    [Fact] // 合成行=OpenAI 协议指向 127.0.0.1 回环（spike §1.2 端口缺省 8017）
    public void Synthesized_row_points_at_the_local_openai_endpoint()
    {
        LlmProviderConfig row = L2ProviderRow.Build();

        Assert.Equal(L2Facts.ProviderRowId, row.Id);
        Assert.Equal(LlmProtocol.OpenAi, row.Protocol);
        Assert.Equal($"http://127.0.0.1:{L2Facts.DefaultPort}/v1", row.BaseUrl);
        Assert.True(row.Enable);
        Assert.Equal(0d, row.Temperature); // spike determinism 口径
        Assert.Equal(0, row.Retries);      // the wave-gap budget owns the deadline — no silent retry doubling

        LlmProviderConfig customPort = L2ProviderRow.Build(port: 8099);
        Assert.Equal("http://127.0.0.1:8099/v1", customPort.BaseUrl);
    }

    [Fact] // 端点构造复用既有 OpenAI 协议面（零协议改动证明：同 BuildEndpoint 管线）
    public void Endpoint_builds_through_the_existing_openai_protocol_lane()
    {
        LlmProviderConfig row = L2ProviderRow.Build(8017);
        Uri endpoint = ChatProtocols.Resolve(row.Protocol).BuildEndpoint(row.BaseUri);

        Assert.Equal("http://127.0.0.1:8017/v1/chat/completions", endpoint.ToString());
    }

    [Fact] // 启动参数=spike §1.2 + §3.2③④ 强制配套（单槽小上下文+--jinja）
    public void Server_arguments_carry_the_spike_mandates()
    {
        string[] arguments = L2ProviderRow.ServerArguments("/srv/llama/model.gguf", port: 8017);

        Assert.Equal(
            ["-m", "/srv/llama/model.gguf", "--port", "8017", "-c", "2048", "-np", "1", "--jinja", "--host", "127.0.0.1"],
            arguments);
        Assert.Contains("--jinja", arguments);              // §3.2④ 工具调用解析依赖
        Assert.Equal("2048", arguments[Array.IndexOf(arguments, "-c") + 1]); // §3.2③ 单槽小上下文
    }

    [Fact] // 本地通道占位凭据=常量标记（非密钥材料；llama-server 忽略 Authorization——spike §2.5）
    public void Lane_key_marker_is_a_constant_not_secret_material()
    {
        Assert.Equal("local-lane-no-key", L2ProviderRow.LaneKeyMarker);
        Assert.Equal(L2Facts.LocalLaneKeyMarker, L2ProviderRow.LaneKeyMarker);
    }

    // ------------------------------------------------ 网关真实对接面（spike 回归）

    [Fact] // [F-163] spike §2.3 Mode B 同形应答 → LlmActionRouter 产出候选动作 JSON（经网关全保障）
    public async Task Synthesized_row_routes_a_tool_call_through_the_real_gateway()
    {
        LlmProviderConfig row = L2ProviderRow.Build();
        var config = new LlmConfig
        {
            Version = 1,
            ActiveProvider = row.Id,
            Providers = [row],
        };
        var port = new FakeWirePort();
        port.EnqueueReply(_ => new HttpPortResponse(200,
            """
            {"id":"x","choices":[{"index":0,"message":{"role":"assistant","content":"","tool_calls":[{"id":"c","type":"function","function":{"name":"FocusFire","arguments":"{\"target_id\":3}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1781,"completion_tokens":21}}
            """,
            null));
        var secrets = new InMemorySecretStore();
        secrets.Store(row.Id, L2ProviderRow.LaneKeyMarker);

        var callLog = new LlmCallLogRing(capacity: 8, clock: new FixedClock(1234));
        var gateway = new LlmGateway(config, port, secrets, callLog: callLog);
        var router = new LlmActionRouter(gateway);

        LlmActionProposal proposal = await router.ProposeAsync(row.Id, "兽崽冲进东门了，全军集火 3号敌人");

        Assert.True(proposal.Ok);
        Assert.NotNull(proposal.ActionJson);
        using JsonDocument parsed = JsonDocument.Parse(proposal.ActionJson!);
        Assert.Equal("FocusFire", parsed.RootElement.GetProperty("action").GetString());
        Assert.Equal(3, parsed.RootElement.GetProperty("target_id").GetInt32());
        Assert.False(proposal.SawRewrite);

        // 军令原文只出现在 untrusted 包裹内（F-096 ① 注入防线——本地通道同罩；
        // System.Text.Json 默认转义非 ASCII，故对包裹串整体做同口径编码再比对）
        string order = "兽崽冲进东门了，全军集火 3号敌人";
        string wrappedEscaped = JsonSerializer.Serialize(LlmActionRouter.WrapUntrusted(order)).Trim('"');
        string body = port.Calls.Single().Body;
        Assert.Contains(wrappedEscaped, body);

        // 调用日志照记（F-091 玩家可见——本地通道不豁免）
        LlmCallLogEntry entry = Assert.Single(callLog.Snapshot());
        Assert.Equal(row.Id, entry.ProviderId);
        Assert.Equal("127.0.0.1", entry.Host);
        Assert.True(entry.Ok);
    }

    [Fact] // 进程死（端口拒连）→ 网关分类 network 错（降级矩阵的输入事实）
    public async Task Dead_local_server_surfaces_as_a_classified_network_failure()
    {
        LlmProviderConfig row = L2ProviderRow.Build();
        var config = new LlmConfig
        {
            Version = 1,
            ActiveProvider = row.Id,
            Providers = [row],
        };
        var port = new FakeWirePort { NextFailure = new IOException("connection refused 127.0.0.1:8017") };
        var secrets = new InMemorySecretStore();
        secrets.Store(row.Id, L2ProviderRow.LaneKeyMarker);

        var gateway = new LlmGateway(config, port, secrets);
        LlmGatewayCallResult call = await gateway.RouteAsync(
            row,
            [new EraCore.Contracts.Agent.LlmChatMessage("user", "x")],
            LlmActionRouter.BuildToolsJson());

        Assert.False(call.Ok);
        Assert.Equal(LlmErrorKeys.Network, call.ErrorKey);
        Assert.Equal(1, call.Attempts); // retries=0 on the L2 row — the queue's budget owns the deadline
    }

    private sealed class FixedClock(long nowMs) : EraCore.Contracts.Foundation.IClock
    {
        public long Tick => 0;

        public long NowMs => nowMs;
    }
}
