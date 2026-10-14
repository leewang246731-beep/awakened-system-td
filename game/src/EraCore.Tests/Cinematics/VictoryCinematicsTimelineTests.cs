using EraCore.Cinematics;
using EraCore.Progression;
using Xunit;
using static EraCore.Tests.Cinematics.VictoryCinematicsFixture;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-002] [F-104] [RL12] T-087 结算演出时间轴纯逻辑：既定数据绑定
// （未绑定即拒）、事件序断言（判分→档位→转盘起→转盘落=既定落点→宝箱→
// 点评→完结）、长按加速（≤4×、倍速域外拒绝、加速不改效果序与落点）、
// 保护线（完成不早于既定拍数——加速不可全跳：Skip 拒绝）、段数不同源
// 拒绝、加速调度下的确定性双跑、零世界命令。
public class VictoryCinematicsTimelineTests
{
    private static DecidedVictoryPerformance DecidedGold() => Decided(ObjectiveTier.Gold);

    private static readonly string[] ExpectedVictoryOrder =
    {
        "screen", "sound", "verdict", "tier", "spinStart", "landed", "sound",
        "chest", "sound", "comment", "checkpoint", "screen", "completed",
    };

    private static string TraceOf(CinematicEffect effect) => effect switch
    {
        CinematicEffect.SettlementVerdict verdict => $"verdict:{verdict.Rows.Count}:{verdict.Rows.Count(r => r.Passed)}",
        CinematicEffect.SettlementTierRevealed tier => $"tier:{tier.Tier}:{tier.ChestTierId}:{tier.CompletedCount}",
        CinematicEffect.WheelSpinStarted spin => $"spinStart:{spin.Segments}:{spin.StopIndex}:{spin.DurationTicks}:{string.Join("/", spin.DecelTicks)}",
        CinematicEffect.WheelLanded landed => $"landed:{landed.StopIndex}:{landed.ItemId}:{landed.Rarity}",
        CinematicEffect.SettlementChestOpened chest => $"chest:{chest.ChestTierId}:{chest.LightLayers}:{chest.Items.Count}",
        CinematicEffect.SettlementCommentarySpoken comment => $"comment:{comment.Commentary.Persona}:{comment.Commentary.MainLine}",
        CinematicEffect.Screen screen => $"screen:{screen.Flavor}:{screen.DurationTicks}",
        CinematicEffect.SoundCue cue => $"sound:{cue.Cue}",
        CinematicEffect.SkipPointReached skipPoint => $"checkpoint:{skipPoint.Id}",
        CinematicEffect.Completed => "completed",
        _ => "other",
    };

    private static string KindOf(CinematicEffect effect) => TraceOf(effect).Split(':')[0];

    /// <summary>
    /// Plays the timeline to completion, collecting every effect; asserts the
    /// zero-world-command invariant along the way (入库先于演出，演出零污染).
    /// </summary>
    private static List<CinematicEffect> PlayThrough(CinematicTimeline timeline, Action<int>? atTick = null)
    {
        var all = new List<CinematicEffect>(timeline.Start());
        int tick = 0;
        while (timeline.Phase != CinematicPhase.Completed && tick < 2000)
        {
            atTick?.Invoke(tick);
            all.AddRange(timeline.Tick());
            Assert.Empty(timeline.TakePendingCommands());
            tick++;
        }

        Assert.Equal(CinematicPhase.Completed, timeline.Phase);
        return all;
    }

    // ---------------------------------------------------------------- legal

    [Fact] // legal — 全放演事件序断言（先定后演验收）：判分→档位→转盘起→转盘落→宝箱→点评→完结
    public void The_full_victory_play_emits_the_decided_events_in_order()
    {
        DecidedVictoryPerformance decided = DecidedGold();
        var timeline = new CinematicTimeline(CinematicContent.Victory(), decided);

        List<CinematicEffect> effects = PlayThrough(timeline);

        Assert.Equal(ExpectedVictoryOrder, effects.Select(KindOf).ToArray());

        // 落点=已定结果（演出倒推）：到停效果携带既定落点与赠品
        CinematicEffect.WheelLanded landed = effects.OfType<CinematicEffect.WheelLanded>().Single();
        Assert.Equal(decided.Wheel.StopIndex, landed.StopIndex);
        Assert.Equal(decided.Wheel.Granted.ItemId, landed.ItemId);
        Assert.Equal(decided.Wheel.Granted.Rarity, landed.Rarity);

        CinematicEffect.SettlementChestOpened chest =
            effects.OfType<CinematicEffect.SettlementChestOpened>().Single();
        Assert.Equal(decided.ChestItems, chest.Items);
        Assert.Equal(decided.ChestLightLayers, chest.LightLayers);
        Assert.Equal(decided.ChestTierId, chest.ChestTierId);

        CinematicEffect.SettlementVerdict verdict =
            effects.OfType<CinematicEffect.SettlementVerdict>().Single();
        Assert.Equal(decided.VerdictRows, verdict.Rows);

        CinematicEffect.WheelSpinStarted spin = effects.OfType<CinematicEffect.WheelSpinStarted>().Single();
        Assert.Equal(decided.Wheel.StopIndex, spin.StopIndex); // 起转即宣告既定落点（倒推依据）
        Assert.Equal(decided.Wheel.Segments.Count, spin.Segments);
        Assert.False(timeline.Skipped);
        Assert.Same(decided, timeline.Settlement); // 绑定只读引用（演出不改既定）
    }

    [Fact] // legal — 长按加速 4×：效果序与落点不变、完成仍 ≥ 保护线 45 拍、总拍数缩短
    public void Boost_four_times_keeps_the_event_order_and_lands_the_same_stop()
    {
        DecidedVictoryPerformance decided = DecidedGold();
        var boosted = new CinematicTimeline(CinematicContent.Victory(), decided);
        boosted.SetBoost(CinematicTimeline.MaxBoost);

        List<CinematicEffect> effects = PlayThrough(boosted);

        Assert.Equal(ExpectedVictoryOrder, effects.Select(KindOf).ToArray());
        Assert.True(boosted.ElapsedTicks >= CinematicContent.Victory().MinimumPlayTicks!.Value);
        Assert.True(boosted.ElapsedTicks < 406, $"4× 加速应明显短于自然全长（得到 {boosted.ElapsedTicks}）");
        CinematicEffect.WheelLanded landed = effects.OfType<CinematicEffect.WheelLanded>().Single();
        Assert.Equal(decided.Wheel.StopIndex, landed.StopIndex);
    }

    [Fact] // legal — 加速调度下的确定性双跑：同脚本+同既定+同倍速日程 → 效果迹全等
    public void Two_boosted_runs_with_the_same_schedule_produce_identical_traces()
    {
        string RunOnce()
        {
            var timeline = new CinematicTimeline(CinematicContent.Victory(), DecidedGold());
            var trace = new List<string> { string.Join("|", timeline.Start().Select(TraceOf)) };
            int tick = 0;
            while (timeline.Phase != CinematicPhase.Completed && tick < 2000)
            {
                if (tick == 30)
                {
                    timeline.SetBoost(4);
                }

                if (tick == 120)
                {
                    timeline.SetBoost(1);
                }

                trace.Add(string.Join("|", timeline.Tick().Select(TraceOf)));
                tick++;
            }

            return string.Join(";", trace);
        }

        Assert.Equal(RunOnce(), RunOnce());
    }

    [Fact] // legal — 保护线钳制：短保护脚本 4× 加速也压不过保护线（完成恰在线上）
    public void Boost_cannot_compress_a_protected_performance_below_the_floor()
    {
        var script = new CinematicScript(
            "cinematic:test-protected", null, "短保护脚本",
            new CinematicStep[]
            {
                new CinematicStep.Sound("ding_reward"),
                new CinematicStep.VerdictReveal(10),
                new CinematicStep.WheelSpin(20, 6, new[] { 2, 3, 5 }),
            },
            Array.Empty<CinematicWorldCommand>())
        {
            MinimumPlayTicks = 45,
        };
        var timeline = new CinematicTimeline(script, DecidedGold());
        timeline.SetBoost(CinematicTimeline.MaxBoost);

        List<CinematicEffect> effects = PlayThrough(timeline);

        Assert.Equal(45, timeline.ElapsedTicks); // 至少播放 1.5s@30Hz——恰在线上完成
        Assert.NotNull(effects.OfType<CinematicEffect.WheelLanded>().Single()); // 持线期间轮盘照常到停
    }

    [Fact] // legal — 自然速度回退：松开长按=1×，倍速域内自由切换
    public void Boost_releases_back_to_natural_speed()
    {
        var timeline = new CinematicTimeline(CinematicContent.Victory(), DecidedGold());

        timeline.SetBoost(4);
        Assert.Equal(4, timeline.BoostFactor);
        timeline.SetBoost(1);
        Assert.Equal(1, timeline.BoostFactor);
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 保护线恰可被自然放演越过：不额外持拍（自然完成拍 > 保护线）
    public void A_natural_run_longer_than_the_floor_completes_at_its_own_length()
    {
        // 单步 45 拍+两瞬发步：自然完成 ~48 拍 > 保护线 45；4× 则被钳到 45。
        var script = new CinematicScript(
            "cinematic:test-natural", null, "自然保护脚本",
            new CinematicStep[]
            {
                new CinematicStep.Sound("ding_reward"),
                new CinematicStep.PersonaComment(45),
                new CinematicStep.Sound("ding_reward"),
            },
            Array.Empty<CinematicWorldCommand>())
        {
            MinimumPlayTicks = 45,
        };
        var natural = new CinematicTimeline(script, DecidedGold());
        PlayThrough(natural);
        var boosted = new CinematicTimeline(script, DecidedGold());
        boosted.SetBoost(4);
        PlayThrough(boosted);

        Assert.Equal(45, boosted.ElapsedTicks);
        Assert.True(natural.ElapsedTicks > 45);
        Assert.Equal(natural.TerminalCommands, boosted.TerminalCommands); // 终态口径同一（空集）
    }

    [Fact] // boundary — 完成后的保护演出：Skip 空转（已完结无害），Tick 空转
    public void A_completed_protected_performance_is_inert_to_skip()
    {
        var script = new CinematicScript(
            "cinematic:test-inert", null, "完结保护脚本",
            new CinematicStep[] { new CinematicStep.Sound("ding_reward") },
            Array.Empty<CinematicWorldCommand>())
        {
            MinimumPlayTicks = 1,
        };
        var timeline = new CinematicTimeline(script, DecidedGold());
        PlayThrough(timeline);

        Assert.Empty(timeline.Skip());
        Assert.Empty(timeline.Tick());
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 不可全跳：保护演出任意时刻 Skip 拒绝（无"全跳"路径，F-002）
    public void Skip_is_refused_for_a_protected_settlement_performance()
    {
        var timeline = new CinematicTimeline(CinematicContent.Victory(), DecidedGold());
        timeline.Start();

        Assert.False(timeline.CanSkip);
        Assert.Throws<InvalidOperationException>(() => timeline.Skip());

        for (int i = 0; i < 60; i++)
        {
            timeline.Tick();
        }

        Assert.Throws<InvalidOperationException>(() => timeline.Skip()); // 中途同样拒绝
        Assert.Equal(CinematicPhase.Playing, timeline.Phase); // 拒绝非静默：演出继续
    }

    [Fact] // illegal — 普通演出跳过口径不受影响（T-086 回归锚）
    public void Ordinary_scripts_remain_skippable()
    {
        var timeline = new CinematicTimeline(CinematicContent.E1Aha());

        Assert.True(timeline.CanSkip);
        timeline.Start();
        Assert.Contains(timeline.Skip(), effect => effect is CinematicEffect.Skipped);
    }

    [Fact] // illegal — 倍速域外：0 与 MaxBoost+1 拒绝
    public void Out_of_range_boost_factors_are_rejected()
    {
        var timeline = new CinematicTimeline(CinematicContent.Victory(), DecidedGold());

        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.SetBoost(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.SetBoost(CinematicTimeline.MaxBoost + 1));
    }

    [Fact] // illegal — 未绑定既定结算的结算步：激活即拒（演出不得自行编造结果）
    public void Settlement_steps_without_a_binding_are_rejected()
    {
        var timeline = new CinematicTimeline(CinematicContent.Victory());

        Assert.Null(timeline.Settlement);
        _ = timeline.Start();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
        {
            int guard = 0;
            while (guard++ < 500)
            {
                timeline.Tick(); // 推进到首个结算步激活处抛出
            }
        });
        Assert.Contains("先定后演", ex.Message);
    }

    [Fact] // illegal — 转盘段数不同源：脚本段数≠既定布局段数即拒（演出倒推前提）
    public void A_wheel_segment_mismatch_between_script_and_decision_is_rejected()
    {
        DecidedVictoryPerformance decided = Decided(ObjectiveTier.Gold, wheelSegments: 5);
        var timeline = new CinematicTimeline(CinematicContent.Victory(), decided); // 脚本 6 段

        _ = timeline.Start();
        InvalidOperationException? fired = null;
        int guard = 0;
        while (fired is null && guard++ < 400)
        {
            try
            {
                timeline.Tick();
            }
            catch (InvalidOperationException ex)
            {
                fired = ex;
            }
        }

        Assert.NotNull(fired);
        Assert.Contains("段数不一致", fired!.Message);
    }
}
