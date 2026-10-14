using System.Text.Json;
using EraCore.Combat;
using EraCore.Scrolls;
using Xunit;

namespace EraCore.Tests.Scrolls;

// [F-038] T-071 JSON 卷轴装载锚点：game/content/scrolls/era2.json 七张全量
// 数据化（SP-5 L161~L167 逐项锚点）+ 装载器封闭词表拒绝矩阵（动词不可扩
// 展/条件与效果原语闭集/卡型纪律）。解析失败即红（严格 JSON）。
public class ScrollDataLoaderTests
{
    private static string FindEra2ScrollsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "scrolls", "era2.json");
            if (File.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/scrolls/era2.json（测试须在仓库内运行）");
    }

    private static IReadOnlyList<ScrollDefinition> Load() =>
        ScrollDataLoader.Parse(File.ReadAllText(FindEra2ScrollsPath()));

    private static ScrollDefinition BySp(string spId) =>
        Load().Single(s => s.SpId == spId);

    // ---- 1) SP-5 七张逐项锚点 ----------------------------------------------

    [Fact]
    public void Era2_file_holds_exactly_seven_scrolls_four_condition_three_action()
    {
        // SP-5 头部 L156：「E2 首发清单 7 张（条件卡 4+动作卡 3）」
        IReadOnlyList<ScrollDefinition> scrolls = Load();
        Assert.Equal(7, scrolls.Count);
        Assert.Equal(4, scrolls.Count(s => s.Kind == ScrollCardKind.ConditionCard));
        Assert.Equal(3, scrolls.Count(s => s.Kind == ScrollCardKind.ActionCard));
        Assert.All(scrolls, s => Assert.Equal(2, s.UnlockEra)); // E2 解锁
        Assert.Equal(7, scrolls.Select(s => s.Id).Distinct().Count()); // id 唯一
        Assert.All(scrolls, s => Assert.Matches("^era2:scroll-[a-z][a-z0-9-]*$", s.Id)); // K-2 命名空间
    }

    [Fact]
    public void SC001_alarm_string_matches_SP5_row()
    {
        // SP-5 L161：警弦｜条件卡｜某分区敌数 ≥5→该区塔集火最强敌（复用
        // FocusFire 语义），冷却 45 s（×30=1350 ticks）；获取=研究节点壹。
        ScrollDefinition scroll = BySp("SC-001");
        Assert.Equal(ScrollCardKind.ConditionCard, scroll.Kind);
        Assert.Equal(1350, scroll.CooldownTicks);
        Assert.Equal(ScrollConditionKind.ZoneEnemyCountAtLeast, scroll.Condition!.Kind);
        Assert.Equal("*", scroll.Condition.Text("zone"));
        Assert.Equal(5, (int)scroll.Condition.Number("count"));
        Assert.Equal("FocusFire", scroll.Action.Verb);
        Assert.Equal(ScrollVerbTarget.StrongestInTriggerZone, scroll.Action.VerbTarget);
        Assert.Equal(ScrollTargetScope.TriggerZone, scroll.Action.Scope);
        Assert.Equal("research", scroll.Acquisition.Kind);
        Assert.Equal("era2:tech-codex-i", scroll.Acquisition.Ref);
    }

    [Fact]
    public void SC002_barbican_retort_matches_SP5_row()
    {
        // SP-5 L162：瓮城反击｜敌入瓮城分区→伤害 +50% 10 s，冷却 45 s；
        // 获取=铁蹄 Boss 首杀掉落（era2:enemy-boss，不可研究）。
        ScrollDefinition scroll = BySp("SC-002");
        Assert.Equal(1350, scroll.CooldownTicks);
        Assert.Equal(ScrollConditionKind.EnemyInZone, scroll.Condition!.Kind);
        Assert.Equal("barbican", scroll.Condition.Text("zone"));
        Assert.Equal(ScrollEffectKind.ZoneDamageBonus, scroll.Action.Effect);
        Assert.Equal(0.5, scroll.Action.EffectMagnitude, 3);
        Assert.Equal(300, scroll.Action.EffectDurationTicks); // 10 s × 30
        Assert.Equal("boss_drop", scroll.Acquisition.Kind);
        Assert.Equal("era2:enemy-boss", scroll.Acquisition.Ref);
    }

    [Fact]
    public void SC003_spare_blades_matches_SP5_row()
    {
        // SP-5 L163：备刃｜动作卡｜穿刺塔攻速 +25% 15 s，冷却 60 s。
        ScrollDefinition scroll = BySp("SC-003");
        Assert.Equal(ScrollCardKind.ActionCard, scroll.Kind);
        Assert.Null(scroll.Condition);
        Assert.Equal(1800, scroll.CooldownTicks); // 60 s × 30
        Assert.Equal(ScrollEffectKind.TowerClassAttackSpeed, scroll.Action.Effect);
        Assert.Equal(TowerClass.Pierce, scroll.Action.TowerClass);
        Assert.Equal(0.25, scroll.Action.EffectMagnitude, 3);
        Assert.Equal(450, scroll.Action.EffectDurationTicks); // 15 s × 30
        Assert.Equal("research", scroll.Acquisition.Kind);
    }

    [Fact]
    public void SC004_rearguard_matches_SP5_row()
    {
        // SP-5 L164：断后｜任一塔 HP ≤50%→回复 20% 最大 HP（每塔每波 1 次）。
        ScrollDefinition scroll = BySp("SC-004");
        Assert.Equal(ScrollConditionKind.TowerHpBelowPercent, scroll.Condition!.Kind);
        Assert.Equal(0.5, scroll.Condition.Number("percent"), 3);
        Assert.Equal(ScrollEffectKind.TowerHealPercent, scroll.Action.Effect);
        Assert.Equal(ScrollTargetScope.TriggerTower, scroll.Action.Scope);
        Assert.Equal(0.2, scroll.Action.EffectMagnitude, 3);
        Assert.Equal(1, scroll.PerTowerPerWaveLimit);
        Assert.Equal(0, scroll.CooldownTicks); // 限频=每塔每波上限，无自身冷却
    }

    [Fact]
    public void SC005_beacon_matches_SP5_row()
    {
        // SP-5 L165：烽火｜动作卡｜支援光环 +2 格 20 s，冷却 90 s；跃迁赠送。
        ScrollDefinition scroll = BySp("SC-005");
        Assert.Equal(ScrollCardKind.ActionCard, scroll.Kind);
        Assert.Equal(2700, scroll.CooldownTicks); // 90 s × 30
        Assert.Equal(ScrollEffectKind.SupportAuraRangeBonus, scroll.Action.Effect);
        Assert.Equal(TowerClass.Support, scroll.Action.TowerClass);
        Assert.Equal(2.0, scroll.Action.EffectMagnitude, 3);
        Assert.Equal(600, scroll.Action.EffectDurationTicks); // 20 s × 30
        Assert.Equal("era_gift", scroll.Acquisition.Kind);
    }

    [Fact]
    public void SC006_press_gang_matches_SP5_row()
    {
        // SP-5 L166：民夫征调｜动作卡｜运力 ×1.5 30 s（不改档位上限），冷却 120 s。
        ScrollDefinition scroll = BySp("SC-006");
        Assert.Equal(3600, scroll.CooldownTicks); // 120 s × 30
        Assert.Equal(ScrollEffectKind.LogisticsThroughputMultiplier, scroll.Action.Effect);
        Assert.Equal(1.5, scroll.Action.EffectMagnitude, 3);
        Assert.Equal(900, scroll.Action.EffectDurationTicks); // 30 s × 30
        Assert.Equal(ScrollTargetScope.None, scroll.Action.Scope);
    }

    [Fact]
    public void SC007_silence_matches_SP5_row()
    {
        // SP-5 L167：静默｜波内剩敌 ≤3→全塔能耗 −50% 至波结束；踏群者掉落。
        ScrollDefinition scroll = BySp("SC-007");
        Assert.Equal(ScrollConditionKind.EnemiesRemainingAtMost, scroll.Condition!.Kind);
        Assert.Equal(3, (int)scroll.Condition.Number("count"));
        Assert.Equal(ScrollEffectKind.TowerEnergyCostMultiplier, scroll.Action.Effect);
        Assert.Equal(0.5, scroll.Action.EffectMagnitude, 3);
        Assert.True(scroll.Action.UntilWaveEnd);
        Assert.Equal("boss_drop", scroll.Acquisition.Kind);
        Assert.Equal("era1:enemy-boss", scroll.Acquisition.Ref); // 踏群者（E1 Boss，E2 期可用）
    }

    [Fact]
    public void Priorities_are_distinct_and_acquisitions_stay_store_free()
    {
        // 优先级互异（仲裁矩阵可判）+ 获取途径仅两类（SP-5 尾注：无商店，全免费定位）。
        IReadOnlyList<ScrollDefinition> scrolls = Load();
        Assert.Equal(7, scrolls.Select(s => s.Priority).ToHashSet().Count);
        Assert.All(scrolls, s => Assert.Contains(s.Acquisition.Kind, new[] { "research", "boss_drop", "era_gift" }));
    }

    // ---- 2) 装载器拒绝矩阵（非法/边界） -----------------------------------

    private static string MinimalItem(string id = "era2:scroll-min", string? condition = null, string actionVerb = null!, string actionEffect = null!, string kind = "condition_card", string scope = "none") =>
        "{\"content_type\":\"Scroll\",\"items\":[{" +
        "\"id\":\"" + id + "\",\"sp_id\":\"SC-T\",\"content_type\":\"Scroll\",\"kind\":\"" + kind + "\"," +
        "\"name_zh\":\"最小\",\"name_en\":\"Minimal\",\"aliases\":[\"最小\"],\"priority\":10," +
        "\"unlock_era\":2,\"cooldown_ticks\":0," +
        (condition is null ? "" : "\"condition\":" + condition + ",") +
        "\"action\":{" + actionVerb + actionEffect + "\"scope\":\"" + scope + "\"}," +
        "\"acquisition\":{\"kind\":\"research\",\"ref\":\"era2:tech-codex-i\"}}" +
        "]}";

    [Fact]
    public void Loader_rejects_unknown_condition_effect_verb_and_scope()
    {
        // illegal: 条件/效果/动词/作用域四词表全部闭集——未知即拒。
        Assert.Throws<InvalidOperationException>(() =>
            ScrollDataLoader.Parse(MinimalItem(condition: "{\"kind\":\"enemy_smells_bad\"}")));
        Assert.Throws<InvalidOperationException>(() =>
            ScrollDataLoader.Parse(MinimalItem(actionEffect: "\"effect\":\"instant_win\",", kind: "action_card")));
        Assert.Throws<InvalidOperationException>(() =>
            ScrollDataLoader.Parse(MinimalItem(actionVerb: "\"verb\":\"SellTower\",", kind: "action_card"))); // 动词不可扩展（F.1）
        Assert.Throws<InvalidOperationException>(() =>
            ScrollDataLoader.Parse(MinimalItem(
                actionEffect: "\"effect\":\"zone_damage_bonus\",\"magnitude\":1.0,",
                condition: "{\"kind\":\"wave_is\",\"wave\":1}",
                scope: "everywhere")));
    }

    [Fact]
    public void Loader_rejects_card_kind_drift_and_missing_halves()
    {
        // illegal: 条件卡无 condition / 动作卡带 condition / 动作既无动词也无效果。
        Assert.Throws<InvalidOperationException>(() =>
            ScrollDataLoader.Parse(MinimalItem(condition: null))); // condition_card without condition
        Assert.Throws<InvalidOperationException>(() =>
            ScrollDataLoader.Parse(MinimalItem(
                condition: "{\"kind\":\"wave_is\",\"wave\":1}",
                kind: "action_card"))); // action_card must not carry a condition
        Assert.Throws<InvalidOperationException>(() =>
            ScrollDataLoader.Parse(MinimalItem(condition: "{\"kind\":\"wave_is\",\"wave\":1}"))); // no verb, no effect
    }

    [Fact]
    public void Loader_rejects_duplicates_and_malformed_json_and_bad_numbers()
    {
        // illegal: 重复 id / 非法 JSON / 错误 content_type / 负冷却 / 小数优先级。
        string twoCopies = "{\"content_type\":\"Scroll\",\"items\":[" +
            "{\"id\":\"era2:scroll-a\",\"sp_id\":\"SC-A\",\"content_type\":\"Scroll\",\"kind\":\"action_card\",\"name_zh\":\"A\",\"name_en\":\"A\",\"priority\":1,\"unlock_era\":2,\"cooldown_ticks\":0,\"action\":{\"effect\":\"logistics_throughput_multiplier\",\"magnitude\":1.5,\"duration_ticks\":30,\"scope\":\"none\"},\"acquisition\":{\"kind\":\"research\",\"ref\":\"r\"}}," +
            "{\"id\":\"era2:scroll-a\",\"sp_id\":\"SC-B\",\"content_type\":\"Scroll\",\"kind\":\"action_card\",\"name_zh\":\"B\",\"name_en\":\"B\",\"priority\":1,\"unlock_era\":2,\"cooldown_ticks\":0,\"action\":{\"effect\":\"logistics_throughput_multiplier\",\"magnitude\":1.5,\"duration_ticks\":30,\"scope\":\"none\"},\"acquisition\":{\"kind\":\"research\",\"ref\":\"r\"}}]}";
        Assert.Throws<InvalidOperationException>(() => ScrollDataLoader.Parse(twoCopies));

        _ = Assert.ThrowsAny<JsonException>(() => ScrollDataLoader.Parse("{\"content_type\":\"Scroll\",\"items\":[{"));
        _ = Assert.Throws<InvalidOperationException>(() => ScrollDataLoader.Parse(
            "{\"content_type\":\"Enemy\",\"items\":[]}")); // wrong content_type

        string negativeCooldown = MinimalItem().Replace("\"cooldown_ticks\":0", "\"cooldown_ticks\":-1");
        Assert.Throws<InvalidOperationException>(() => ScrollDataLoader.Parse(negativeCooldown));

        string fractionalPriority = MinimalItem().Replace("\"priority\":10", "\"priority\":10.5");
        Assert.Throws<InvalidOperationException>(() => ScrollDataLoader.Parse(fractionalPriority));
    }

    [Fact]
    public void Loader_accepts_boundary_shapes_zero_cooldown_and_trigger_scopes()
    {
        // boundary: cooldown 0 合法（SC-004/SC-007 形态）；trigger 作用域合法（条件卡）。
        IReadOnlyList<ScrollDefinition> parsed = ScrollDataLoader.Parse(MinimalItem(
            condition: "{\"kind\":\"enemy_in_zone\",\"zone\":\"barbican\"}",
            actionEffect: "\"effect\":\"zone_damage_bonus\",\"magnitude\":0.5,\"duration_ticks\":0,",
            kind: "condition_card",
            scope: "trigger_zone"));
        Assert.Single(parsed);
        Assert.Equal(0, parsed[0].CooldownTicks);
        Assert.Equal(0, parsed[0].Action.EffectDurationTicks);
        Assert.Equal(ScrollTargetScope.TriggerZone, parsed[0].Action.Scope);
    }
}
