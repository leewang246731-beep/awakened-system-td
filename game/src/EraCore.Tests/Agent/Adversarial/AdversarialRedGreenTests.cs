using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Actions;
using Xunit;
using static EraCore.Tests.Agent.Adversarial.AdversarialCorpus;
using static EraCore.Tests.Agent.Adversarial.AdversarialVerdicts;

namespace EraCore.Tests.Agent.Adversarial;

// [F-096][F-173] T-082 红/绿可判验证（CI 判别力证明）：语料内置 5 条
// disabled『已知漏洞样本』（advrg-01..05）。本套件做两件事：
//   绿侧——临时启用每条样本（只在测试内翻转 enabled 语义，文件不动），
//         跑与正式语料完全相同的双档通道：防护在场 ⇒ 全部按期望拒绝/净化
//         ⇒ 套件保持绿；
//   红侧——把每条样本 known_vulnerability 描述的『漏洞观测』具体化
//         （防护失效时会出现的 Ok/裸链接结果），喂给正式套件所用的同一批
//         判据谓词：判据必须拒收 ⇒ 若任何防护回归，CI 立即转红。
// 另附零越权指纹灵敏度探针：一次真实『玩家确认后的合法建造』必然改变
// 指纹 ⇒ 全局状态哈希断言非空洞（有真突变必翻红）。验证记录见
// docs/engineering/reviews/REV-20261014-T082-adversarial.md。

/// <summary>已知漏洞样本的红/绿判别力验证。</summary>
public class AdversarialRedGreenTests
{
    public static IEnumerable<object[]> SampleIds() =>
        LoadAll().Where(entry => !entry.Enabled).Select(entry => new object[] { entry.Id });

    private static AdversarialEntry FindSample(string id) =>
        LoadAll().SingleOrDefault(entry => entry.Id == id && !entry.Enabled)
        ?? throw new InvalidOperationException($"红绿样本 {id} 不存在");

    [Theory] // legal：[F-173] 绿侧——临时启用样本仍全绿（防护在场）
    [MemberData(nameof(SampleIds))]
    public void Temporarily_enabled_sample_stays_green_because_guards_hold(string id)
    {
        AdversarialEntry sample = FindSample(id);
        using var harness = new AdversarialHarness();
        string before = harness.Fingerprint();

        OrderOutcome l1 = harness.Pipeline.Handle(sample.Payload);
        Assert.True(harness.Fingerprint() == before, $"{id}: 样本 L1 档越权落盘");

        LlmActionProposal proposal = harness.Propose(sample);
        Assert.True(proposal.Ok, $"{id}: 样本回传未产出候选（{proposal.ErrorKey}）");
        if (sample.ExpectedBehavior == "sanitize")
        {
            ActionOutcome outcome = harness.Registry.Invoke(proposal.ActionJson!);
            Assert.Equal(ActionResultStatus.Ok, outcome.Result.Status);
            string payloadText = JsonSerializer.Serialize(outcome.Payload);
            Assert.Contains(ReportToPlayerAction.FilteredLinkToken, payloadText);
            foreach (string link in ExtractLinkLikeFragments(sample.EchoArgumentsJson ?? string.Empty))
            {
                Assert.DoesNotContain(link, payloadText);
            }
        }
        else
        {
            ActionOutcome outcome = harness.Registry.Invoke(proposal.ActionJson!);
            Assert.NotEqual(ActionResultStatus.Ok, outcome.Result.Status);
        }

        Assert.True(harness.Fingerprint() == before, $"{id}: 样本 LLM 档越权落盘");
    }

    [Fact] // legal：[F-173] 红侧——漏洞观测被正式判据拒收（防护回归即红）
    public void Known_vulnerability_observations_are_rejected_by_the_suite_predicates()
    {
        foreach (string id in LoadAll().Where(entry => !entry.Enabled).Select(entry => entry.Id))
        {
            AdversarialEntry sample = FindSample(id);
            // 漏洞观测=防护失效时的结果：闭表/钳制/过滤全部放行（Ok），或
            // 汇报渠道裸链接原样抵达玩家。用与正式套件完全相同的谓词判收。
            var hijackedProposal = new LlmActionProposal(true, sample.EchoArgumentsJson, null, SawRewrite: false);
            var vulnerableOutcome = new ActionOutcome(new ActionResult(ActionResultStatus.Ok), null);

            switch (sample.ExpectedBehavior)
            {
                case "deny":
                    // deny 谓词必须拒收『闭表放行』的观测（IsDenyOutcome=false ⇒ 断言红）。
                    Assert.False(
                        IsDenyOutcome(hijackedProposal, vulnerableOutcome),
                        $"{id}: deny 判据接受了漏洞观测（防护回归时 CI 不会红）");
                    break;
                case "sanitize":
                    // 漏洞观测=裸链接原样在场且无过滤标记；正式判据要求
                    // [link-filtered] 在场且裸链接绝迹——两条断言都会翻红。
                    string rawPayloadText = sample.EchoArgumentsJson ?? string.Empty;
                    Assert.NotEmpty(ExtractLinkLikeFragments(rawPayloadText)); // 观测确实携带钓鱼链接
                    Assert.DoesNotContain(ReportToPlayerAction.FilteredLinkToken, rawPayloadText);
                    break;
                default:
                    throw new InvalidOperationException($"{id}: 红绿样本只允许 deny/sanitize 期望");
            }
        }
    }

    [Fact] // legal：[F-173] 零越权指纹灵敏度——真实合法落子必被察觉（非空洞断言）
    public void Zero_privilege_fingerprint_detects_a_real_confirmed_build()
    {
        using var harness = new AdversarialHarness();
        string before = harness.Fingerprint();

        OrderOutcome awaiting = harness.Pipeline.Handle("在东门建两座木箭塔");
        Assert.Equal(PipelineOutcomeKind.AwaitingApproval, awaiting.Kind);
        Assert.True(harness.Fingerprint() == before, "挂起中的方案卡不得产生任何状态变更");

        OrderOutcome built = harness.Pipeline.Confirm(awaiting.PendingId);
        Assert.Equal(PipelineOutcomeKind.Completed, built.Kind);
        Assert.NotEqual(before, harness.Fingerprint()); // 玩家确认后合法落子 ⇒ 指纹必变
    }
}
