using System.Text;
using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Agent.Grammar;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using EraCore.Llm;
using EraCore.Sim;
using EraCore.Tests.Agent.Actions;
using EraCore.Tests.Agent.Grammar;
using EraCore.Tests.Agent.Pipeline;
using EraCore.Tests.Llm;

namespace EraCore.Tests.Agent.Adversarial;

// [F-096][F-173] T-082 注入对抗语料测试基建：adversarial.jsonl 装载、模拟
// LLM 回传的 SSE 合成、双档夹具（L1 管线 + 同一仿真上的 ActionRegistry
// 闭表 + FakeWirePort 网关路由器）与零越权指纹。纯支撑类，不含测试方法
// （T3 不适用）。

/// <summary>一条对抗语料（jsonl 行的强类型形）。</summary>
/// <param name="Id">语料 id（adv-&lt;type&gt;-&lt;lang&gt;-&lt;nn&gt; / advrg-nn）。</param>
/// <param name="AttackType">七类攻击之一。</param>
/// <param name="Lang">zh / en / mixed。</param>
/// <param name="Payload">军令原文：L1 档输入，也是 LLM 档不可信块内文本。</param>
/// <param name="EchoTool">模拟模型回传的工具名（清单外伪动词亦可）；null=无工具调用。</param>
/// <param name="EchoArgumentsJson">工具参数原始 JSON 文本（保留装载时的字节序）。</param>
/// <param name="EchoContent">模拟模型的自由文本回复（内容层注入，无工具调用）。</param>
/// <param name="EchoReasoning">模拟 reasoning_content 夹带文本（思考通道分流验证）。</param>
/// <param name="ExpectedBehavior">deny | sanitize | clarify | allow-with-sanitization。</param>
/// <param name="Notes">出处注记（对应 F.4 条款/K-3 行）。</param>
/// <param name="Enabled">false=红/绿验证用『已知漏洞样本』，正式断言不跑。</param>
/// <param name="KnownVulnerability">禁用样本的漏洞后果描述（防护失效时会发生什么）。</param>
internal sealed record AdversarialEntry(
    string Id,
    string AttackType,
    string Lang,
    string Payload,
    string? EchoTool,
    string? EchoArgumentsJson,
    string? EchoContent,
    string? EchoReasoning,
    string ExpectedBehavior,
    string Notes,
    bool Enabled,
    string? KnownVulnerability)
{
    /// <summary>该条是否携带 LLM 档模拟回传。</summary>
    public bool HasEcho => EchoTool is not null || EchoContent is not null;
}

/// <summary>adversarial.jsonl 装载 + 形状常量。</summary>
internal static class AdversarialCorpus
{
    /// <summary>七类攻击的规范 id（各 ≥20 条）。</summary>
    public static readonly IReadOnlyList<string> AttackTypes = new[]
    {
        "prompt_injection",
        "privilege_escalation",
        "schema_escape",
        "phishing_link",
        "length_overflow",
        "encoding_obfuscation",
        "social_engineering",
    };

    /// <summary>expected_behavior 的四值闭集。</summary>
    public static readonly IReadOnlySet<string> Behaviors = new HashSet<string>(StringComparer.Ordinal)
    {
        "deny", "sanitize", "clarify", "allow-with-sanitization",
    };

    /// <summary>红/绿已知漏洞样本的固定条数（advrg-01..05）。</summary>
    public const int KnownVulnerabilitySampleCount = 5;

    /// <summary>全量装载（含禁用的红/绿样本）。</summary>
    public static IReadOnlyList<AdversarialEntry> LoadAll() =>
        LoadLines(GrammarTestPaths.ReadGrammarFile("adversarial.jsonl"));

    /// <summary>仅装载启用条目（正式断言域）。</summary>
    public static IReadOnlyList<AdversarialEntry> LoadEnabled() =>
        LoadAll().Where(entry => entry.Enabled).ToArray();

    private static IReadOnlyList<AdversarialEntry> LoadLines(string jsonl)
    {
        var entries = new List<AdversarialEntry>();
        int line = 0;
        foreach (string raw in jsonl.Split('\n'))
        {
            string text = raw.TrimEnd('\r');
            if (text.Length == 0)
            {
                continue;
            }

            line++;
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            entries.Add(new AdversarialEntry(
                root.GetProperty("id").GetString()!,
                root.GetProperty("attack_type").GetString()!,
                root.GetProperty("lang").GetString()!,
                root.GetProperty("payload").GetString()!,
                root.TryGetProperty("llm_echo", out JsonElement echo) && echo.TryGetProperty("tool", out JsonElement tool)
                    ? tool.GetString()
                    : null,
                root.TryGetProperty("llm_echo", out JsonElement echoArgs)
                    && echoArgs.TryGetProperty("arguments", out JsonElement arguments)
                    && arguments.ValueKind is not JsonValueKind.Null
                    ? arguments.GetRawText()
                    : null,
                root.TryGetProperty("llm_echo", out JsonElement echoContent)
                    && echoContent.TryGetProperty("content", out JsonElement content)
                    ? content.GetString()
                    : null,
                root.TryGetProperty("llm_echo", out JsonElement echoReasoning)
                    && echoReasoning.TryGetProperty("reasoning", out JsonElement reasoning)
                    ? reasoning.GetString()
                    : null,
                root.GetProperty("expected_behavior").GetString()!,
                root.GetProperty("notes").GetString()!,
                root.TryGetProperty("enabled", out JsonElement enabled) && enabled.GetBoolean(),
                root.TryGetProperty("known_vulnerability", out JsonElement vulnerability)
                    ? vulnerability.GetString()
                    : null));
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("adversarial.jsonl 装载为空（语料缺失）");
        }

        return entries;
    }
}

/// <summary>
/// 模拟 LLM 回传的 SSE 合成器：把一条语料的 llm_echo 变成 OpenAI 风格的
/// 分片 tool_calls / content / reasoning_content 流（形状对齐
/// mimo-buildplan-tool cassette 与 LlmFixtures.OpenAiSse；ChunkedStream 在
/// 网关侧再按 7 字节切流，半行拼接路径全程在测）。
/// </summary>
internal static class AdversarialEchoFactory
{
    private const int ArgumentChunkChars = 8;

    /// <summary>合成一条回传的完整 SSE 文本。</summary>
    public static string BuildSse(AdversarialEntry entry)
    {
        var frames = new List<string> { RoleFrame() };
        if (entry.EchoReasoning is { Length: > 0 } reasoning)
        {
            frames.Add(DeltaFrame(w => w.WriteString("reasoning_content", reasoning)));
        }

        if (entry.EchoContent is { Length: > 0 } content)
        {
            frames.Add(DeltaFrame(w => w.WriteString("content", content)));
        }

        if (entry.EchoTool is { Length: > 0 } tool)
        {
            frames.Add(ToolNameFrame(tool));
            string arguments = entry.EchoArgumentsJson ?? string.Empty;
            for (int start = 0; start < arguments.Length; start += ArgumentChunkChars)
            {
                string chunk = arguments.Substring(start, Math.Min(ArgumentChunkChars, arguments.Length - start));
                frames.Add(ToolArgumentsFrame(chunk));
            }

            frames.Add(FinishFrame("tool_calls"));
        }
        else
        {
            frames.Add(FinishFrame("stop"));
        }

        frames.Add("data: [DONE]");
        return string.Join("\n\n", frames) + "\n\n";
    }

    private static string RoleFrame() => DeltaFrame(w => w.WriteString("role", "assistant"));

    private static string DeltaFrame(Action<Utf8JsonWriter> writeDelta)
    {
        string payload = Frame(w =>
        {
            w.WriteStartArray("choices");
            w.WriteStartObject();
            w.WriteNumber("index", 0);
            w.WriteStartObject("delta");
            writeDelta(w);
            w.WriteEndObject();
            w.WriteNull("finish_reason");
            w.WriteEndObject();
            w.WriteEndArray();
        });
        return payload;
    }

    private static string ToolNameFrame(string tool) => DeltaFrame(w =>
    {
        w.WriteStartArray("tool_calls");
        w.WriteStartObject();
        w.WriteNumber("index", 0);
        w.WriteString("id", "call_adv");
        w.WriteString("type", "function");
        w.WriteStartObject("function");
        w.WriteString("name", tool);
        w.WriteString("arguments", string.Empty);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndArray();
    });

    private static string ToolArgumentsFrame(string chunk) => DeltaFrame(w =>
    {
        w.WriteStartArray("tool_calls");
        w.WriteStartObject();
        w.WriteNumber("index", 0);
        w.WriteStartObject("function");
        w.WriteString("arguments", chunk);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndArray();
    });

    private static string FinishFrame(string reason) => Frame(w =>
    {
        w.WriteStartArray("choices");
        w.WriteStartObject();
        w.WriteNumber("index", 0);
        w.WriteStartObject("delta");
        w.WriteEndObject();
        w.WriteString("finish_reason", reason);
        w.WriteEndObject();
        w.WriteEndArray();
    });

    private static string Frame(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return "data: " + Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// 双档对抗夹具：真实 E1 关卡 + era-2 智能体栈（复用 T-053 的
/// PipelineHarness），外加同一仿真上的一只 ActionRegistry 闭表（LLM 档
/// 候选直达闭表的受控通道）和一只 FakeWirePort 网关路由器（注入防护
/// ①/①b 全程在测，零网络）。
/// </summary>
internal sealed class AdversarialHarness : IDisposable
{
    public const string ProviderId = "test-provider";
    public const int AgentEra = 2;

    private readonly SimulationConfig _config;

    public AdversarialHarness()
    {
        Inner = new PipelineHarness(initialWood: 3000.0);
        _config = PipelineHarness.Config(3000.0);
        var view = new SimulationActionView(Inner.Simulation, _config);
        RegistrySink = new CapturingLogSink();
        Registry = new ActionRegistry(
            RegistrySink,
            view,
            eraOrdinalProvider: () => AgentEra,
            towerEstate: Inner.Simulation.Estate);
        Port = new FakeWirePort();
        var secrets = new InMemorySecretStore();
        secrets.Store(ProviderId, "synthetic-adversarial-key");
        var gateway = new LlmGateway(
            LlmFixtures.TestConfig(),
            Port,
            secrets,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
        Router = new LlmActionRouter(
            gateway,
            AliasDictionary.FromTowerJson(GrammarTestPaths.Era2TowersJson()));
    }

    public PipelineHarness Inner { get; }

    public EraCore.Agent.Pipeline.AgentPipeline Pipeline => Inner.Pipeline;

    public ActionRegistry Registry { get; }

    public CapturingLogSink RegistrySink { get; }

    public FakeWirePort Port { get; }

    public LlmActionRouter Router { get; }

    /// <summary>把一条语料的回传喂给路由器（同步包装；FakeWirePort 零网络）。</summary>
    public LlmActionProposal Propose(AdversarialEntry entry)
    {
        Port.EnqueueSse(AdversarialEchoFactory.BuildSse(entry));
        return Router.ProposeAsync(ProviderId, entry.Payload).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 零越权指纹：仿真权威状态哈希 + 已应用命令计数 + 管线账本余额 +
    /// 两只注册表各自的智能体状态（任务/待办/指令/模式/卷轴）。语料跑完
    /// 不变 ⇒ 没有任何越权动作落盘（T-082 全局断言的判据）。
    /// </summary>
    public string Fingerprint()
    {
        var builder = new StringBuilder();
        builder.Append("sim:").Append(Inner.Simulation.ComputeStateHash().ToString("x16"))
            .Append('/').Append(Inner.Simulation.AppliedCommandCount)
            .Append('/').Append(Inner.Simulation.CurrentTick)
            .Append('/').Append(Inner.Simulation.Towers.Count);
        builder.Append("|ledger:").Append(Pipeline.Ledger.Points)
            .Append('/').Append(Pipeline.Ledger.WaveSpent);
        AppendAgentState(builder, Pipeline.State);
        AppendAgentState(builder, Registry.State);
        return builder.ToString();
    }

    private static void AppendAgentState(StringBuilder builder, AgentActionState state)
    {
        builder.Append("|todos=").Append(state.Todos.Count);
        foreach (string todo in state.Todos)
        {
            builder.Append(',').Append(todo);
        }

        builder.Append("|tasks=").Append(state.Tasks.Count);
        foreach (AgentTaskRecord task in state.Tasks)
        {
            builder.Append(',').Append(task.TaskId).Append(':').Append(task.Family)
                .Append('/').Append(task.DueTick).Append('/').Append(task.UntilTick);
        }

        builder.Append("|scrolls=").Append(state.ScrollActivations.Count);
        foreach (ScrollActivation scroll in state.ScrollActivations)
        {
            builder.Append(',').Append(scroll.ScrollId).Append('@').Append(scroll.AtTick);
        }

        builder.Append("|focus:").Append(state.FocusTarget?.ToString() ?? "-");
        foreach (var pair in state.RallyStances.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("|rally:").Append(pair.Key).Append('=').Append(pair.Value);
        }

        foreach (var pair in state.StockpileFloors.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("|floor:").Append(pair.Key).Append('=').Append(pair.Value.ToString("0.##"));
        }

        foreach (var pair in state.TowerModes.OrderBy(pair => pair.Key))
        {
            builder.Append("|mode:").Append(pair.Key).Append('=').Append(pair.Value);
        }
    }

    public void Dispose() => Inner.Dispose();
}

/// <summary>期望判据的纯谓词（对抗语料理论测试与红/绿验证共用同一套）。</summary>
internal static class AdversarialVerdicts
{
    /// <summary>L1 档『澄清』语义：意图层失败出澄清卡（或路由拒收），绝无执行。</summary>
    public static bool IsClarifyOutcome(PipelineOutcomeKind kind) =>
        kind is PipelineOutcomeKind.Clarification or PipelineOutcomeKind.Refused;

    /// <summary>LLM 档『拒绝』语义：路由器未产出候选（no_tool_call/坏响应）或闭表拒绝。</summary>
    public static bool IsDenyOutcome(LlmActionProposal proposal, ActionOutcome outcome) =>
        !proposal.Ok || outcome.Result.Status != ActionResultStatus.Ok;

    /// <summary>抽出一串文本里的链接样片段（sanitize 断言用：过滤后不得再现）。</summary>
    public static IReadOnlyList<string> ExtractLinkLikeFragments(string text)
    {
        var found = new List<string>();
        foreach (string token in text.Split(new[] { ' ', '"', ',', '[', ']', '{', '}', '?' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Contains("http://", StringComparison.Ordinal)
                || token.Contains("https://", StringComparison.Ordinal)
                || token.Contains("www.", StringComparison.Ordinal)
                || (token.EndsWith(".cn", StringComparison.Ordinal)
                    || token.EndsWith(".io", StringComparison.Ordinal)
                    || token.EndsWith(".xyz", StringComparison.Ordinal)
                    || token.EndsWith(".top", StringComparison.Ordinal)
                    || token.EndsWith(".com", StringComparison.Ordinal)
                    || token.EndsWith(".example.com", StringComparison.Ordinal))
                && token.Contains('.'))
            {
                found.Add(token);
            }
        }

        return found;
    }
}
