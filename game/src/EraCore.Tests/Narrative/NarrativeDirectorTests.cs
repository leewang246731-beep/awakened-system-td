using EraCore.Agent.Governance;
using EraCore.Narrative;
using Xunit;

namespace EraCore.Tests.Narrative;

// [C-24] [F-098] [F-099] [F-103] [F-109] T-089 章节状态机：幕推进触发器
// （波次/演出/胜利喂入→序贯推进）、文案选择器（信任三档 × 人格四套穷举）、
// 波间闲聊确定性（同拍同线，无随机）、胜利交底+卡点预告合流、Begin 前喂拍
// 拒绝、人格切换零能力变化（只改选线）。
public class NarrativeDirectorTests
{
    private static StoryChapter Chapter() => NarrativeContent.StoryE1();

    private static NarrativeDirector Director(
        TrustBadge badge = TrustBadge.Trustworthy,
        SystemPersona persona = SystemPersona.Advisor,
        MockArcRow? arc = null) => new(Chapter(), badge, persona, arc);

    // ------------------------------------------------------- 幕推进触发器

    [Fact] // legal — E1 节拍流：开场→波2 受辱→绑定演出→波5 打脸→胜利交底+预告
    public void The_E1_beat_stream_walks_all_three_acts_to_the_tease()
    {
        NarrativeDirector director = Director();

        Assert.Equal(2, director.Begin().Count); // 开场白两行
        Assert.Equal(NarrativePhase.InChapter, director.Phase);

        var act1 = director.OnWaveStart(2, 100);
        Assert.Contains(act1, line => line.Key == "e1.act1.taunt"); // 受辱幕含嘲讽
        Assert.Equal(1, director.ActsCompleted);

        IReadOnlyList<DialogueLine> act2 = director.OnCinematicFired("cinematic:e1-aha", 200);
        Assert.Equal(2, act2.Count); // 绑定幕两行
        Assert.Equal(2, director.ActsCompleted);

        IReadOnlyList<DialogueLine> act3 = director.OnWaveStart(5, 300);
        Assert.Contains(act3, line => line.Key == "e1.act3.system");
        Assert.Equal(3, director.ActsCompleted);

        IReadOnlyList<DialogueLine> ending = director.OnVictory(400);
        Assert.Contains(ending, line => line.Key == "e1.end.handover"); // 交底
        Assert.Contains(ending, line => line.Text.Contains("剪影")); // 卡点预告（F-102）
        Assert.Contains(ending, line => line.Text.Contains("城墙")); // 威胁台词（E2 撞木）
        Assert.Equal(NarrativePhase.Ended, director.Phase);
    }

    [Fact] // legal — 谓词矩阵：带外波/错误演出 id 不推进（幕序不变）
    public void Out_of_band_beats_do_not_advance_the_sequence()
    {
        NarrativeDirector director = Director();
        _ = director.Begin();

        Assert.Empty(director.OnWaveStart(1, 10)); // 第 1 波：受辱幕在波 2
        Assert.Empty(director.OnWaveStart(3, 20)); // 第 3 波：带外
        Assert.Empty(director.OnCinematicFired("cinematic:e2-awaken", 30)); // 错误演出 id
        Assert.Equal(0, director.ActsCompleted);

        Assert.NotEmpty(director.OnWaveStart(2, 40)); // 命中受辱
        Assert.Equal(1, director.ActsCompleted);
    }

    [Fact] // legal — 打脸行武装：波 2/波 4 嘲讽与幕台词合流下发
    public void Armed_mock_arc_taunts_ride_the_wave_beats()
    {
        NarrativeDirector director = Director(arc: Chapter().MockArcFor("campaign:level-e1-01"));
        _ = director.Begin();

        IReadOnlyList<DialogueLine> wave2 = director.OnWaveStart(2, 100);
        Assert.Contains(wave2, line => line.Speaker == "斥候" && line.Text.Contains("木栅栏"));
        IReadOnlyList<DialogueLine> wave4 = director.OnWaveStart(4, 200);
        Assert.Contains(wave4, line => line.Text.Contains("援军")); // 波 4 嘲讽拍
        Assert.DoesNotContain(wave4, line => line.Text.Contains("木栅栏")); // 波 2 嘲讽不复发
    }

    [Fact] // legal — 幕未齐的胜利：只发胜利谓词幕，交底不发（幕齐才收章）
    public void Victory_before_the_acts_complete_holds_the_handover()
    {
        NarrativeDirector director = Director();
        _ = director.Begin();

        Assert.Empty(director.OnVictory(100)); // 三幕未齐
        Assert.Equal(NarrativePhase.InChapter, director.Phase);

        _ = director.OnWaveStart(2, 110);
        _ = director.OnCinematicFired("cinematic:e1-aha", 120);
        _ = director.OnWaveStart(5, 130);
        Assert.NotEmpty(director.OnVictory(140)); // 幕齐→交底
        Assert.Equal(NarrativePhase.Ended, director.Phase);
    }

    // ------------------------------------------------- 文案选择器（档位×人格）

    [Theory] // legal — 档位×人格穷举：三档×四套=12 格每格都能选出该档该人格的线
    [InlineData(TrustBadge.Skeptical, SystemPersona.Advisor, "e1.idle.skept.advisor")]
    [InlineData(TrustBadge.Skeptical, SystemPersona.Snarky, "e1.idle.skept.snarky")]
    [InlineData(TrustBadge.Skeptical, SystemPersona.Goofy, "e1.idle.skept.goofy")]
    [InlineData(TrustBadge.Skeptical, SystemPersona.Tsundere, "e1.idle.skept.tsundere")]
    [InlineData(TrustBadge.Trustworthy, SystemPersona.Advisor, "e1.idle.trust.advisor")]
    [InlineData(TrustBadge.Trustworthy, SystemPersona.Snarky, "e1.idle.trust.snarky")]
    [InlineData(TrustBadge.Trustworthy, SystemPersona.Goofy, "e1.idle.trust.goofy")]
    [InlineData(TrustBadge.Trustworthy, SystemPersona.Tsundere, "e1.idle.trust.tsundere")]
    [InlineData(TrustBadge.Devoted, SystemPersona.Advisor, "e1.idle.devot.advisor")]
    [InlineData(TrustBadge.Devoted, SystemPersona.Snarky, "e1.idle.devot.snarky")]
    [InlineData(TrustBadge.Devoted, SystemPersona.Goofy, "e1.idle.devot.goofy")]
    [InlineData(TrustBadge.Devoted, SystemPersona.Tsundere, "e1.idle.devot.tsundere")]
    public void Intermission_selection_covers_every_tier_persona_cell(
        TrustBadge badge, SystemPersona persona, string expectedKey)
    {
        NarrativeDirector director = Director(badge: badge, persona: persona);
        _ = director.Begin();

        DialogueLine line = Assert.IsType<DialogueLine>(director.OnWaveEnd());

        Assert.Equal(expectedKey, line.Key); // 档位×人格加权选中
        Assert.Equal(persona, line.Persona);
    }

    [Fact] // legal — 档位切换即时生效（F.4 三档徽记只调制措辞/频率）
    public void Trust_tier_switch_changes_the_pool_immediately()
    {
        NarrativeDirector director = Director(badge: TrustBadge.Skeptical);
        _ = director.Begin();

        Assert.Equal("e1.idle.skept.advisor", director.OnWaveEnd()!.Key);
        director.SetTrust(TrustBadge.Devoted);
        Assert.Equal("e1.idle.devot.advisor", director.OnWaveEnd()!.Key);
    }

    [Fact] // legal — 确定性：同节拍流两次跑选线序列全等（零随机——CS-1.4/回放口径）
    public void The_same_beat_schedule_yields_the_identical_line_trace()
    {
        string TraceOne()
        {
            NarrativeDirector director = Director();
            var trace = new List<string>();
            trace.AddRange(director.Begin().Select(line => line.BubbleText));
            for (int wave = 1; wave <= 8; wave++)
            {
                trace.AddRange(director.OnWaveStart(wave, 10 * wave).Select(line => line.BubbleText));
                if (director.OnWaveEnd() is { } idle)
                {
                    trace.Add(idle.BubbleText);
                }
            }

            return string.Join("|", trace);
        }

        Assert.Equal(TraceOne(), TraceOne());
    }

    [Fact] // legal — 人格轮换：同一档池内同人逐步轮换（确定性 round-robin，非重复同线）
    public void Persona_draws_rotate_within_one_tier()
    {
        NarrativeDirector director = Director(badge: TrustBadge.Devoted, persona: SystemPersona.Advisor);
        _ = director.Begin();

        DialogueLine first = director.OnWaveEnd()!;
        director.SetPersona(SystemPersona.Snarky);
        DialogueLine second = director.OnWaveEnd()!;
        director.SetPersona(SystemPersona.Advisor);

        Assert.Equal("e1.idle.devot.advisor", first.Key);
        Assert.Equal("e1.idle.devot.snarky", second.Key);
        Assert.Equal("e1.idle.devot.advisor", director.OnWaveEnd()!.Key); // 回到 advisor=下一条 advisor 线
    }

    [Fact] // legal — 人格切换零能力差：三套人格下的幕推进结果完全一致（F-109 只改表达）
    public void Persona_switching_never_changes_act_progression()
    {
        int ProgressWith(SystemPersona persona)
        {
            NarrativeDirector director = Director(persona: persona);
            _ = director.Begin();
            _ = director.OnWaveStart(2, 10);
            _ = director.OnCinematicFired("cinematic:e1-aha", 20);
            _ = director.OnWaveStart(5, 30);
            return director.ActsCompleted;
        }

        Assert.Equal(3, ProgressWith(SystemPersona.Snarky));
        Assert.Equal(3, ProgressWith(SystemPersona.Goofy));
        Assert.Equal(3, ProgressWith(SystemPersona.Tsundere));
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — Begin 前喂拍：拒绝（状态机未开）；Begin 双调用拒绝
    public void Beats_before_begin_are_rejected()
    {
        NarrativeDirector director = Director();

        Assert.Throws<InvalidOperationException>(() => director.OnWaveStart(2, 1));
        _ = director.Begin();
        Assert.Throws<InvalidOperationException>(() => director.Begin());
    }

    [Fact] // illegal — 空演出 id：拒绝
    public void A_blank_cinematic_id_is_rejected()
    {
        NarrativeDirector director = Director();
        _ = director.Begin();

        Assert.Throws<ArgumentException>(() => director.OnCinematicFired(" ", 1));
    }
}
