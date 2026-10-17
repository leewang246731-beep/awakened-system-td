using Xunit;
using EraCore.Legacy;

namespace EraCore.Tests.Legacy;

// [C-20][F-056][F-058][F-060][F-003][F-011] 首版内容锚点回归（T-074）：
// game/content/legacy/ 五份 JSON 的装载器行为与文档锚点比对。期望值硬编码
// 自 spike3-report §6 推荐参数采纳值与附录 F.3 清单；解析失败即红。
public class LegacyContentTests
{
    private static string Load(string fileName) =>
        File.ReadAllText(Path.Combine(LegacyFixture.FindLegacyDir(), fileName));

    // ---- relic_mapping.json ------------------------------------------- legal

    [Fact] // legal — spike3 §6 推荐参数集完整落表
    public void relic_mapping_adopts_spike3_recommended_parameters()
    {
        RelicMappingParams parameters = LegacyDataLoader.ParseRelicMapping(Load("relic_mapping.json"));
        Assert.Equal(10, parameters.CoreRadiusCells); // A5 切比雪夫半径 10（07 #13）
        Assert.Equal(2, parameters.BuildMarginCells); // 已建区外扩 2 格
        Assert.Equal(2, parameters.ShiftRadiusCells); // F.7 移位档（spike3 §6 推荐参数 3）
        Assert.Equal(10, parameters.TerrainYieldRadiusCells); // 让位半径=核心区半径（推荐参数 1）
        Assert.True(parameters.ResourceAvoidYieldZone); // 资源点避开让位区（推荐参数 2）
        Assert.Equal(12, parameters.ResourceNodeCount);
        Assert.Equal(9, parameters.ThreatNodeCount);
        Assert.Equal(96, parameters.MapSizeCells);
        Assert.Equal(new[] { 0, 90, 180, 270 }, parameters.Orientations);
        Assert.Equal(0.15, parameters.ForestRatio, 9); // GridWorld.Default*Ratio 逐值一致
        Assert.Equal(0.10, parameters.RockRatio, 9);
        Assert.Equal(0.08, parameters.WaterRatio, 9);
        Assert.True(parameters.ReserveAreaCapFraction > 0 && parameters.ReserveAreaCapFraction <= 0.151,
            "核心区面积标记上限=15%（T-074 任务卡）");
    }

    [Fact] // illegal — 声明 content_type 不符即拒
    public void relic_mapping_rejects_wrong_content_type()
    {
        Assert.Throws<InvalidOperationException>(() => LegacyDataLoader.ParseRelicMapping(
            "{\"content_type\":\"Wonder\"}"));
    }

    [Theory] // illegal — 参数域违例逐一拒绝
    [InlineData("\"core_zone\":{\"core_radius_cells\":0,\"build_margin_cells\":2,\"reserve_area_cap_fraction\":0.15}")]
    [InlineData("\"core_zone\":{\"core_radius_cells\":10,\"build_margin_cells\":2,\"reserve_area_cap_fraction\":1.5}")]
    [InlineData("\"new_continent\":{\"forest_ratio\":0.9,\"rock_ratio\":0.9,\"water_ratio\":0.08}")]
    public void relic_mapping_rejects_out_of_domain_values(string patched)
    {
        string json = "{\"content_type\":\"DropTable\"," + patched + "}";
        Assert.Throws<InvalidOperationException>(() => LegacyDataLoader.ParseRelicMapping(json));
    }

    // ---- wonders.json -------------------------------------------------- legal

    [Fact] // legal — 附录 F.3 六奇观清单逐行锚点
    public void wonders_match_appendix_f3_catalog()
    {
        IReadOnlyList<WonderSpec> wonders = LegacyDataLoader.ParseWonders(Load("wonders.json"));
        Assert.Equal(6, wonders.Count); // 六奇观（E1..E5+终章）
        WonderSpec pyramid = wonders.Single(w => w.EraIndex == 1);
        Assert.Equal("campaign:wonder-pyramid", pyramid.Id);
        Assert.Equal("resource_cap", pyramid.BonusKind);
        Assert.Equal(0.2, pyramid.BonusValue, 9); // 资源上限 +20%（F.3）
        Assert.Equal("research_cost", wonders.Single(w => w.EraIndex == 2).BonusKind);
        Assert.Equal(0.15, wonders.Single(w => w.EraIndex == 2).BonusValue, 9); // 大图书馆 研究 −15%
        Assert.Equal("logistics_throughput", wonders.Single(w => w.EraIndex == 3).BonusKind);
        Assert.Equal(0.25, wonders.Single(w => w.EraIndex == 3).BonusValue, 9); // 世博会 吞吐 +25%
        Assert.Equal("radar_shared", wonders.Single(w => w.EraIndex == 4).BonusKind);
        Assert.Equal("energy_output", wonders.Single(w => w.EraIndex == 5).BonusKind);
        Assert.Equal(2.0, wonders.Single(w => w.EraIndex == 5).BonusValue, 9); // 戴森云 能源 ×2
        Assert.Equal("finale_gate", wonders.Single(w => w.EraIndex == 6).BonusKind);
        Assert.Equal(Enumerable.Range(1, 6), wonders.Select(w => w.EraIndex).OrderBy(e => e)); // 每时代恰一座
    }

    [Fact] // legal — K-2 注册表包装按 id 排序且 kind=Wonder
    public void wonder_items_register_under_wonder_type_sorted()
    {
        var items = LegacyDataLoader.WonderItems(LegacyFixture.Wonders());
        Assert.Equal(6, items.Count);
        Assert.All(items, item => Assert.Equal(Contracts.Content.ContentType.Wonder, item.ContentType));
        Assert.Equal(items.Select(i => i.Id).Order(StringComparer.Ordinal), items.Select(i => i.Id));
        Assert.Contains(items.Single(i => i.Id == "campaign:wonder-pyramid").Aliases, a => a == "金字塔");
    }

    [Fact] // illegal — 同时代双奇观拒绝（F.3 每时代一座）
    public void wonders_reject_two_wonders_of_one_era()
    {
        string Row(string id) =>
            "{\"id\":\"" + id + "\",\"era_index\":1,\"tech_node\":\"t\",\"name_zh\":\"n\",\"name_en\":\"n\","
            + "\"bonus_kind\":\"k\",\"bonus_value\":0.1}";
        string json = "{\"content_type\":\"Wonder\",\"items\":[" + Row("w:a") + "," + Row("w:b") + "]}";
        Assert.Throws<InvalidOperationException>(() => LegacyDataLoader.ParseWonders(json));
    }

    // ---- archaeology.json --------------------------------------------- legal

    [Fact] // legal — F-058 三通道与考古经济锚点
    public void archaeology_table_matches_task_card_knobs()
    {
        ArchaeologyTable table = LegacyDataLoader.ParseArchaeology(Load("archaeology.json"));
        Assert.Equal(20, table.Dig.ManpowerCost); // 人力 20
        Assert.Equal(35, table.Dig.EnergyCost); // 能量 35
        Assert.Equal(30, table.Dig.MinDurationSeconds); // 30~90s
        Assert.Equal(90, table.Dig.MaxDurationSeconds);
        Assert.Equal(30, table.Dig.DrillTicksPerSecond); // K-1 30 Hz 对齐
        Assert.Equal(30, table.Dig.TierDurationSeconds[1]);
        Assert.Equal(90, table.Dig.TierDurationSeconds[4]);
        Assert.Equal(3, table.Channels.Count); // 三通道
        Assert.Equal(50, table.Channels.Single(c => c.Channel == "knowledge_scroll").Weight, 9);
        Assert.Equal(30, table.Channels.Single(c => c.Channel == "ancient_blueprint").Weight, 9);
        Assert.Equal(20, table.Channels.Single(c => c.Channel == "restoration").Weight, 9);
        Assert.Equal(4, table.Scrolls.Count); // 知识残卷 4 张（接 E2 节点）
        Assert.All(table.Scrolls, scroll => Assert.Equal(0.10, scroll.ReliefShare, 9));
        Assert.Equal(2, table.Blueprints.Count); // 远古图纸首批两张
        Assert.All(table.Blueprints, bp => Assert.Equal(2, bp.Mix.Count)); // 跨时代混合=2 构件
        Assert.Equal(0.3, table.Restoration.WreckRepairCostFraction, 9); // 07 #28 ×30%
        Assert.Equal(0.8, table.Restoration.NewEraConversionRate, 9); // ×0.8 叠加
        Assert.Equal(0.05, table.Restoration.MemorialAuras["repair_discount"], 9);
        Assert.Equal(0.04, table.Restoration.MemorialAuras["research_relief"], 9);
    }

    [Fact] // illegal — 未知通道名拒绝（封闭词汇）
    public void archaeology_rejects_unknown_channel()
    {
        Assert.Throws<InvalidOperationException>(() => LegacyDataLoader.ParseArchaeology(
            "{\"content_type\":\"DropTable\",\"dig\":{},\"channels\":{\"gold\":1}}"));
    }

    // ---- heritage_shop.json ------------------------------------------- legal

    [Fact] // legal — 传承商店恰 3 张装饰蓝图（30% 带走清单的兑换目录）
    public void heritage_shop_offers_exactly_three_decorative_blueprints()
    {
        HeritageShopTable shop = LegacyDataLoader.ParseHeritageShop(Load("heritage_shop.json"));
        Assert.Equal(3, shop.Items.Count);
        Assert.All(shop.Items, item => Assert.Equal("decorative_blueprint", item.Kind));
        Assert.All(shop.Items, item => Assert.True(item.PriceFund >= 1));
        Assert.Equal(shop.Items.Select(i => i.Id).Order(StringComparer.Ordinal), shop.Items.Select(i => i.Id));
    }

    // ---- era_transition.json ------------------------------------------ legal

    [Fact] // legal — F-003/F-011 跃迁参数锚点：30% 带走 + 3:1 折算 + 仓库+1 + 终局关 E1..E5 行 + 五目标时代前三关排期（T-117/T-125/T-134/T-135）
    public void era_transition_params_match_frozen_anchors()
    {
        EraTransitionSpec spec = LegacyDataLoader.ParseEraTransition(Load("era_transition.json"));
        Assert.True(spec.RequiresFinalLevel); // 硬前置=终局关
        Assert.True(spec.RequiresWonder); // +奇观（F.3 清单=跃迁硬条件）
        Assert.Equal(0.3, spec.CarriedShare, 9); // 30% 课税（附录 A 映射）
        Assert.Equal(3, spec.UselessFoldDivisor); // 文明基金 3:1
        Assert.Equal(1, spec.WarehouseLevelGain); // 仓库自动 +1（§5）
        Assert.True(spec.PyramidBonusCarriesOver); // 金字塔随遗迹城跨代
        Assert.Equal("campaign:level-e1-final", spec.FinalLevels[1]);
        Assert.Equal("campaign:level-e2-final", spec.FinalLevels[2]); // T-117 E2→E3 行（id 裁定=E1 行同款约定，见 _source）
        Assert.Equal("campaign:level-e3-final", spec.FinalLevels[3]); // T-125 E3→E4 行（e3-finale.json 演出同款命名约定）
        Assert.Equal("campaign:level-e4-final", spec.FinalLevels[4]); // T-124 E4 行（T-134 域清账：E5 目标域实体化后 E4→E5 跃迁真实可达）
        Assert.Equal("campaign:level-e5-final", spec.FinalLevels[5]); // T-135 E5 行（E5→终章=第五跳；实关行已随 T-134 交付 levels/e5.json slot 5——引用与实关两侧齐备，不悬空）
        Assert.Equal(5, spec.FinalLevels.Count); // 键域 1..5 封闭：终章=第六代无后继跃迁（e6-final 走纪元之门通关之门语义，不入本表）

        // 排期=五个目标时代各三行（target_era 2=E1→E2 冻结行；3=T-117；4=T-125；5=T-134；6=T-135）
        Assert.Equal(15, spec.FirstThreeSchedule.Count);
        IReadOnlyList<EraFirstThreeSlot> e2 = spec.ScheduleFor(2);
        Assert.Equal(3, e2.Count);
        Assert.Equal("new_energy", e2.Single(s => s.Slot == 1).Focus); // 关 1 新能源
        Assert.Equal("campaign:level-e2-01", e2.Single(s => s.Slot == 1).LevelId);
        Assert.Equal("new_logistics", e2.Single(s => s.Slot == 2).Focus); // 关 2 新物流
        Assert.Equal("archaeology_hero_system", e2.Single(s => s.Slot == 3).Focus); // 关 3 考古+英雄+系统新能力

        IReadOnlyList<EraFirstThreeSlot> e3 = spec.ScheduleFor(3);
        Assert.Equal(3, e3.Count);
        Assert.Equal("campaign:level-e3-01", e3.Single(s => s.Slot == 1).LevelId); // T-117：E2→E3 前三关=e3-01..03
        Assert.Equal("new_energy", e3.Single(s => s.Slot == 1).Focus);
        Assert.Equal("campaign:level-e3-02", e3.Single(s => s.Slot == 2).LevelId);
        Assert.Equal("new_logistics", e3.Single(s => s.Slot == 2).Focus);
        Assert.Equal("campaign:level-e3-03", e3.Single(s => s.Slot == 3).LevelId);
        Assert.Equal("archaeology_hero_system", e3.Single(s => s.Slot == 3).Focus);

        IReadOnlyList<EraFirstThreeSlot> e4 = spec.ScheduleFor(4); // T-125：E3→E4 前三关=e4-01..03（逐值对齐 era4/levels.json）
        Assert.Equal(3, e4.Count);
        Assert.Equal("campaign:level-e4-01", e4.Single(s => s.Slot == 1).LevelId);
        Assert.Equal("new_energy", e4.Single(s => s.Slot == 1).Focus);
        Assert.Equal("campaign:level-e4-02", e4.Single(s => s.Slot == 2).LevelId);
        Assert.Equal("new_logistics", e4.Single(s => s.Slot == 2).Focus);
        Assert.Equal("campaign:level-e4-03", e4.Single(s => s.Slot == 3).LevelId);
        Assert.Equal("archaeology_hero_system", e4.Single(s => s.Slot == 3).Focus);

        IReadOnlyList<EraFirstThreeSlot> e5 = spec.ScheduleFor(5); // T-134：E4→E5 前三关=e5-01..03（逐值对齐 era5/levels.json 冻结侧）
        Assert.Equal(3, e5.Count);
        Assert.Equal("campaign:level-e5-01", e5.Single(s => s.Slot == 1).LevelId);
        Assert.Equal("new_energy", e5.Single(s => s.Slot == 1).Focus);
        Assert.Equal("campaign:level-e5-02", e5.Single(s => s.Slot == 2).LevelId);
        Assert.Equal("new_logistics", e5.Single(s => s.Slot == 2).Focus);
        Assert.Equal("campaign:level-e5-03", e5.Single(s => s.Slot == 3).LevelId);
        Assert.Equal("archaeology_hero_system", e5.Single(s => s.Slot == 3).Focus);

        IReadOnlyList<EraFirstThreeSlot> e6 = spec.ScheduleFor(6); // T-135：E5→终章前三关=e6-01..03（逐值对齐 era6/levels.json 冻结侧）
        Assert.Equal(3, e6.Count);
        Assert.Equal("campaign:level-e6-01", e6.Single(s => s.Slot == 1).LevelId);
        Assert.Equal("new_energy", e6.Single(s => s.Slot == 1).Focus);
        Assert.Equal("campaign:level-e6-02", e6.Single(s => s.Slot == 2).LevelId);
        Assert.Equal("new_logistics", e6.Single(s => s.Slot == 2).Focus);
        Assert.Equal("campaign:level-e6-03", e6.Single(s => s.Slot == 3).LevelId);
        Assert.Equal("archaeology_hero_system", e6.Single(s => s.Slot == 3).Focus);
        Assert.Empty(spec.ScheduleFor(7)); // 未声明目标时代=空（终章=六时代终点，无第七代）
    }

    [Fact] // illegal — 前三关槽位重复拒绝（同 target_era 内 (era, slot) 重复）
    public void era_transition_rejects_duplicate_schedule_slots()
    {
        string row = "{\"slot\":1,\"target_era\":3,\"level_id\":\"l\",\"focus\":\"f\"}";
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            LegacyDataLoader.ParseEraTransition(ScheduleDoc("[" + row + "," + row + "]")));
        Assert.Contains("distinct", ex.Message, StringComparison.Ordinal); // 真因=槽位重复（非缺字段）
    }

    [Theory] // illegal — 排期行缺 target_era / 同时代槽 1..3 不完整 → 点名拒绝（T-117 结构守卫）
    [InlineData(
        "[{\"slot\":1,\"level_id\":\"l\",\"focus\":\"f\"}]",
        "target_era")]
    [InlineData(
        "[{\"slot\":1,\"target_era\":3,\"level_id\":\"l\",\"focus\":\"f\"},{\"slot\":2,\"target_era\":3,\"level_id\":\"m\",\"focus\":\"g\"},{\"slot\":1,\"target_era\":2,\"level_id\":\"n\",\"focus\":\"h\"}]",
        "slots 1..3")]
    public void era_transition_rejects_missing_target_era_and_incomplete_slots(string rows, string needle)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            LegacyDataLoader.ParseEraTransition(ScheduleDoc(rows)));
        Assert.Contains(needle, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A minimal but shape-complete era-transition document whose schedule
    /// array is <paramref name="scheduleJson"/> — the negative rows must fail
    /// on their OWN rule, not on a missing sibling field (CS-3.3).
    /// </summary>
    private static string ScheduleDoc(string scheduleJson) => """
        {"content_type":"Era",
         "preconditions":{"requires_final_level":true,"requires_wonder":true},
         "levy":{"carried_share":0.3,"useless_fold_divisor":3},
         "warehouse":{"level_gain_per_transition":1},
         "pyramid_bonus_carries_over":true,
         "final_levels":{"_source":"n"},
         "first_three_schedule":
        """ + scheduleJson + "}";

    // ---- boundary ------------------------------------------------------

    [Fact] // boundary — 注解键（'_' 前缀）全文档忽略，不进任何参数
    public void annotation_keys_are_ignored_everywhere()
    {
        RelicMappingParams parameters = LegacyDataLoader.ParseRelicMapping(Load("relic_mapping.json"));
        Assert.Equal(10, parameters.CoreRadiusCells); // _source/_units 注解不影响数值装载
        ArchaeologyTable table = LegacyDataLoader.ParseArchaeology(Load("archaeology.json"));
        Assert.Equal(3, table.Channels.Count); // channels._source 不算通道
    }
}
