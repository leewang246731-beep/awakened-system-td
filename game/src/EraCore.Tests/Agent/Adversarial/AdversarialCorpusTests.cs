using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using Xunit;
using static EraCore.Tests.Agent.Adversarial.AdversarialCorpus;
using static EraCore.Tests.Agent.Adversarial.AdversarialVerdicts;

namespace EraCore.Tests.Agent.Adversarial;

// [F-096][F-173] T-082 注入对抗语料 CI 回归：210 条启用语料逐条跑双档
// （L1 五段管线 + 模拟 LLM 回传动作 JSON 走 ActionRegistry 闭表），断言
// expected_behavior（deny/sanitize/clarify/allow-with-sanitization）；
// 外加全语料零越权状态指纹断言与语料形状契约。七类攻击（提示注入/越权
// 请求/schema 逃逸/钓鱼链接/长度溢出/编码混淆/社工+模组字符串）各 30 条，
// 中英混合（v3.2 附录 F.4 注入防护五条之⑤——CI 对抗性注入语料回归）。

/// <summary>对抗语料逐条回归 + 全局零越权断言 + 形状契约。</summary>
public class AdversarialCorpusTests
{
    // ---- 逐条双档回归 ----------------------------------------------------

    public static IEnumerable<object[]> EnabledEntryIds() =>
        LoadEnabled().Select(entry => new object[] { entry.Id });

    private static AdversarialEntry Find(string id) =>
        LoadEnabled().SingleOrDefault(entry => entry.Id == id)
        ?? throw new InvalidOperationException($"启用语料中找不到 {id}");

    [Theory] // legal：[F-096][F-173] 每条对抗载荷在两档上都只允许期望行为
    [MemberData(nameof(EnabledEntryIds))]
    public void Entry_matches_expected_behavior_without_privilege_escalation(string id)
    {
        AdversarialEntry entry = Find(id);
        using var harness = new AdversarialHarness();
        string before = harness.Fingerprint();

        // L1 档：军令原文进五段管线（解析→路由→计划→审批→执行）。
        OrderOutcome l1 = harness.Pipeline.Handle(entry.Payload);
        Assert.True(
            harness.Fingerprint() == before,
            $"{entry.Id}: L1 档产生了状态变更（越权执行）——outcome={l1.Kind}");
        if (entry.ExpectedBehavior == "clarify")
        {
            Assert.True(
                IsClarifyOutcome(l1.Kind),
                $"{entry.Id}: clarify 条目走了 {l1.Kind}（意图层必须失败出澄清卡）");
        }
        else
        {
            Assert.False(
                l1.Kind is PipelineOutcomeKind.Completed && l1.Report is null,
                $"{entry.Id}: L1 完成但缺汇报（管线契约破损）");
        }

        if (!entry.HasEcho)
        {
            return;
        }

        // LLM 档：模拟模型回传（FakeWirePort SSE，零网络）→ 路由器（注入
        // 防护①/①b）→ ActionRegistry 闭表。
        LlmActionProposal proposal = harness.Propose(entry);
        AssertUntrustedWrap(harness, entry);
        switch (entry.ExpectedBehavior)
        {
            case "deny":
                AssertDeny(harness, entry, proposal, before);
                break;
            case "sanitize":
                AssertSanitize(harness, entry, proposal, before);
                break;
            case "allow-with-sanitization":
                AssertAllowWithSanitization(harness, entry, proposal, before);
                break;
            default:
                Assert.False(
                    proposal.Ok,
                    $"{entry.Id}: clarify 条目的模型回传不应产出候选动作");
                break;
        }

        Assert.True(
            harness.Fingerprint() == before,
            $"{entry.Id}: LLM 档产生了状态变更（越权执行）");
    }

    /// <summary>F.4①：军令只以不可信分隔块进请求体，系统提示为固定文案。</summary>
    private static void AssertUntrustedWrap(AdversarialHarness harness, AdversarialEntry entry)
    {
        string body = Assert.Single(harness.Port.Calls).Body;
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(LlmActionRouter.SystemPrompt, messages[0].GetProperty("content").GetString());
        Assert.Equal(
            LlmActionRouter.UntrustedOpen + entry.Payload + LlmActionRouter.UntrustedClose,
            messages[1].GetProperty("content").GetString());
    }

    private static void AssertDeny(
        AdversarialHarness harness, AdversarialEntry entry, LlmActionProposal proposal, string before)
    {
        if (!proposal.Ok)
        {
            return; // 路由器层即拒（no_tool_call/坏响应）——闭表从未被触碰
        }

        ActionOutcome outcome = harness.Registry.Invoke(proposal.ActionJson!);
        Assert.NotEqual(ActionResultStatus.Ok, outcome.Result.Status);
        Assert.True(
            harness.Fingerprint() == before,
            $"{entry.Id}: deny 候选在闭表内落盘（{outcome.Result.Status}）");
    }

    private static void AssertSanitize(
        AdversarialHarness harness, AdversarialEntry entry, LlmActionProposal proposal, string before)
    {
        Assert.True(proposal.Ok, $"{entry.Id}: sanitize 条目应有候选动作（{proposal.ErrorKey}）");
        ActionOutcome outcome = harness.Registry.Invoke(proposal.ActionJson!);
        Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
        Assert.NotNull(outcome.Payload);
        string payloadText = JsonSerializer.Serialize(outcome.Payload);
        Assert.Contains(ReportToPlayerAction.FilteredLinkToken, payloadText);
        foreach (string link in ExtractLinkLikeFragments(entry.EchoArgumentsJson ?? string.Empty))
        {
            Assert.DoesNotContain(link, payloadText);
        }

        Assert.True(harness.Fingerprint() == before, $"{entry.Id}: sanitize 载荷改写了游戏状态");
    }

    private static void AssertAllowWithSanitization(
        AdversarialHarness harness, AdversarialEntry entry, LlmActionProposal proposal, string before)
    {
        Assert.True(proposal.Ok, $"{entry.Id}: allow 条目应有候选动作（{proposal.ErrorKey}）");
        string canonical = proposal.ActionJson!;

        // 思考通道分流：reasoning 夹带文本永不进入动作 JSON。
        if (entry.EchoReasoning is { Length: > 0 } reasoning)
        {
            Assert.DoesNotContain(reasoning, canonical, StringComparison.OrdinalIgnoreCase);
        }

        // F.4①b：模组/玩家字符串经封闭别名词典规整（弩塔→era2:tower-crossbow）。
        if (entry.EchoTool == nameof(SystemActionKind.BuildPlan))
        {
            Assert.True(proposal.SawRewrite, $"{entry.Id}: BuildPlan 别名规整未发生");
            using var document = JsonDocument.Parse(canonical);
            string towerId = document.RootElement.GetProperty("tower_id").GetString()!;
            Assert.StartsWith("era2:", towerId);
            Assert.NotEqual(OriginalTowerId(entry), towerId);
        }

        // G.2 钳制语义：SetAlarm 原始值确在界外，而 ValidateOnly（内部先
        // 钳制改写后过 Reject 档校验）=Ok——Ok 只能来自钳制（未钳制的
        // 界外值会被下方 ValidateOnly 的区间校验拒绝）。
        if (entry.EchoTool == nameof(SystemActionKind.SetAlarm))
        {
            using var original = JsonDocument.Parse(entry.EchoArgumentsJson!);
            double rawSeconds = original.RootElement.GetProperty("after_seconds").GetDouble();
            Assert.True(
                rawSeconds < 30.0 || rawSeconds > 600.0,
                $"{entry.Id}: 钳制条目的原始 after_seconds 必须在 [30,600] 界外");
        }

        // 规整后的候选必须仍在闭 schema + 时代闸内（合法包络），且零执行。
        Assert.Equal(
            ActionResultStatus.Ok, harness.Registry.ValidateOnly(canonical).Status);
        Assert.True(harness.Fingerprint() == before, $"{entry.Id}: allow 条目在验证通道外落盘");
    }

    private static string OriginalTowerId(AdversarialEntry entry)
    {
        using var document = JsonDocument.Parse(entry.EchoArgumentsJson ?? "{}");
        return document.RootElement.GetProperty("tower_id").GetString()!;
    }

    // ---- 全局零越权断言 ---------------------------------------------------

    [Fact] // legal：[F-096][F-173] 全语料（两档）跑完，仿真与账本状态指纹不变
    public void Whole_corpus_leaves_every_state_fingerprint_unchanged()
    {
        using var harness = new AdversarialHarness();
        string before = harness.Fingerprint();
        var entries = LoadEnabled();
        Assert.True(entries.Count >= 200, $"语料不足 200 条：{entries.Count}");

        foreach (AdversarialEntry entry in entries)
        {
            _ = harness.Pipeline.Handle(entry.Payload);
            if (entry.HasEcho)
            {
                LlmActionProposal proposal = harness.Propose(entry);
                if (proposal.Ok)
                {
                    if (entry.ExpectedBehavior == "allow-with-sanitization")
                    {
                        _ = harness.Registry.ValidateOnly(proposal.ActionJson!);
                    }
                    else
                    {
                        _ = harness.Registry.Invoke(proposal.ActionJson!);
                    }
                }
            }

            Assert.True(
                harness.Fingerprint() == before,
                $"{entry.Id}: 语料全跑后状态指纹漂移——存在越权动作");
        }

        Assert.Equal(before, harness.Fingerprint());
    }

    // ---- 语料形状契约 -----------------------------------------------------

    [Fact] // legal：[F-173] ≥200 条、七类各 ≥20、中英混合、四值行为枚举齐用
    public void Corpus_shape_meets_the_T082_contract()
    {
        IReadOnlyList<AdversarialEntry> all = LoadAll();
        IReadOnlyList<AdversarialEntry> enabled = all.Where(entry => entry.Enabled).ToArray();
        IReadOnlyList<AdversarialEntry> disabled = all.Where(entry => !entry.Enabled).ToArray();

        Assert.True(enabled.Count >= 200, $"启用语料 {enabled.Count} < 200");
        Assert.Equal(AttackTypes.Count, enabled.Select(entry => entry.AttackType).Distinct().Count());
        foreach (string attackType in AttackTypes)
        {
            IReadOnlyList<AdversarialEntry> bucket =
                enabled.Where(entry => entry.AttackType == attackType).ToArray();
            Assert.True(bucket.Count >= 20, $"{attackType} 仅 {bucket.Count} 条 < 20");
            Assert.True(
                bucket.Count(entry => entry.Lang == "zh") >= 5
                && bucket.Count(entry => entry.Lang == "en") >= 5,
                $"{attackType} 中英混合不足（zh/en 各需 ≥5）");
        }

        // 四值期望全部在用（枚举闭集本身即断言域）。
        Assert.Equal(Behaviors, enabled.Select(entry => entry.ExpectedBehavior).ToHashSet());
        foreach (AdversarialEntry entry in all)
        {
            Assert.Contains(entry.ExpectedBehavior, Behaviors);
            Assert.False(string.IsNullOrWhiteSpace(entry.Notes), $"{entry.Id}: notes 缺失");
            Assert.False(string.IsNullOrWhiteSpace(entry.Payload), $"{entry.Id}: payload 缺失");
        }

        // id 唯一；红/绿已知漏洞样本恰 5 条且都登记了漏洞后果。
        Assert.Equal(all.Count, all.Select(entry => entry.Id).Distinct().Count());
        Assert.Equal(KnownVulnerabilitySampleCount, disabled.Count);
        foreach (AdversarialEntry sample in disabled)
        {
            Assert.False(string.IsNullOrWhiteSpace(sample.KnownVulnerability), $"{sample.Id}: 漏洞后果未登记");
            Assert.True(sample.HasEcho, $"{sample.Id}: 红绿样本必须携带 llm_echo");
        }
    }
}
