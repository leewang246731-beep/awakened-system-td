using EraCore.Agent.Governance;
using EraCore.Cinematics;
using EraCore.Presentation;
using EraCore.Progression;
using Xunit;
using static EraCore.Tests.Cinematics.VictoryCinematicsFixture;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-104] T-087 系统一句话点评（按信任档位+人格）：6+4 条模板穷举——
// 4 条结算向模板 × 3 档徽记 = 12 句成品（四人格四句互不相同、句句带档位
// 徽记措辞）；6 条跨档回声=复用 T-075 TrustBadgeLines 单一来源（含两格
// 结构不可达极值逐对穷举）；Decide 面同档无回声/跨档有回声。
public class SettlementCommentaryTests
{
    // ---------------------------------------------------------------- legal

    [Fact] // legal — 4 模板 × 3 档穷举：12 句成品两两不同（四人格四句点评，F-104）
    public void All_persona_times_badge_compositions_are_distinct()
    {
        var lines = new HashSet<string>();

        foreach (SystemPersona persona in SystemPersonas.All)
        {
            foreach (TrustBadge badge in new[] { TrustBadge.Skeptical, TrustBadge.Trustworthy, TrustBadge.Devoted })
            {
                string line = SettlementCommentary.Compose(persona, badge);
                Assert.NotEmpty(line);
                Assert.Contains(TrustBadgeLabels.Label(badge), line); // 按信任档位措辞
                lines.Add(line);
            }
        }

        Assert.Equal(SystemPersonas.All.Count * 3, lines.Count);
    }

    [Fact] // legal — 人格标签：四套齐备（毒舌/参谋/沙雕/傲娇，§10.7）
    public void The_four_persona_labels_are_available()
    {
        Assert.Equal(
            new[] { "毒舌", "参谋", "沙雕", "傲娇" },
            SystemPersonas.All.Select(SystemPersonas.LabelZh));
    }

    [Fact] // legal — 6 条跨档回声穷举：结算缝=T-075 台词库原文（单一来源不漂移）
    public void The_trust_shift_echo_exhausts_the_t075_library_unchanged()
    {
        TrustBadge[] tiers = { TrustBadge.Skeptical, TrustBadge.Trustworthy, TrustBadge.Devoted };
        int echoes = 0;
        foreach (TrustBadge from in tiers)
        {
            foreach (TrustBadge to in tiers)
            {
                if (from == to)
                {
                    continue;
                }

                Assert.Equal(TrustBadgeLines.Line(from, to), SettlementCommentary.TrustShiftEcho(from, to));
                echoes++;
            }
        }

        Assert.Equal(6, echoes); // 3 档 × 2 向全穷举（含 Devoted↑/Skeptical↓ 两格极值）
    }

    [Fact] // legal — Decide 面：同档无回声，跨档带 T-075 回声（RL12：回声也先定）
    public void Decide_carries_the_echo_only_when_the_badge_moved()
    {
        DecidedVictoryPerformance steady = Decided(
            ObjectiveTier.Gold, badgeAtVictory: TrustBadge.Trustworthy, badgeAtLevelStart: TrustBadge.Trustworthy);
        DecidedVictoryPerformance moved = Decided(
            ObjectiveTier.Gold, badgeAtVictory: TrustBadge.Devoted, badgeAtLevelStart: TrustBadge.Skeptical);

        Assert.Null(steady.Commentary.TrustShiftEcho);
        Assert.Equal(TrustBadge.Trustworthy, steady.Commentary.Badge);
        Assert.NotNull(moved.Commentary.TrustShiftEcho);
        Assert.Equal(
            TrustBadgeLines.Line(TrustBadge.Skeptical, TrustBadge.Devoted),
            moved.Commentary.TrustShiftEcho);
        Assert.Contains(TrustBadgeLabels.Label(TrustBadge.Devoted), moved.Commentary.MainLine);
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 同档跨档判据的边界：恰在阈值上的两档（40/70 邻档）与同档
    public void Neighbor_tier_pairs_echo_and_equal_tiers_do_not()
    {
        foreach ((TrustBadge from, TrustBadge to) in new[]
        {
            (TrustBadge.Skeptical, TrustBadge.Trustworthy),
            (TrustBadge.Trustworthy, TrustBadge.Devoted),
        })
        {
            Assert.NotEqual(string.Empty, SettlementCommentary.TrustShiftEcho(from, to));
            Assert.Throws<ArgumentException>(() => SettlementCommentary.TrustShiftEcho(from, from));
        }
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 未知人格值：Compose/LabelZh 拒绝（穷举 switch 无默认吸收）
    public void Unknown_persona_values_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SettlementCommentary.Compose((SystemPersona)99, TrustBadge.Trustworthy));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SystemPersonas.LabelZh((SystemPersona)(-1)));
    }

    [Fact] // illegal — 未知徽记值：Compose 拒绝
    public void Unknown_badge_values_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SettlementCommentary.Compose(SystemPersona.Acid, (TrustBadge)42));
    }
}
