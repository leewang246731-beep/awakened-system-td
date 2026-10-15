using System.Text.Json;
using EraCore.Agent.Actions;
using EraCore.Agent.Grammar;
using EraCore.Combat;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Content;
using EraCore.Mods;
using EraCore.Tests.Agent.Grammar;
using Xunit;
using static EraCore.Tests.Agent.Actions.ModLinkTestPaths;

namespace EraCore.Tests.Agent.Actions;

// [F-095][C-03] 模组参数空间自动扩展单元面（T-104）：模组装载后
// BuildPlan.tower_id 进入「内置+注册表动态并集」、军令别名词典模组源打通、
// 升级律/锚点带对模组塔生效（越带=装载错误带行号，非运行时崩溃）、
// 无模组时零漂移（金样断言）。全链联测（建塔→开火→入波→开卷）见
// PlayerPackFullChainTests；本文件只测 Compose 与 Schema 门本身。

public class ModActionParamSpaceTests
{
    private static IReadOnlyList<TowerSpec> BuiltinEra1() =>
        TowerSpec.LoadCollection(GrammarTestPaths.Era1TowersJson());

    private static ModParamSpace ComposeWithSamples() => ComposeWith(SamplesDir());

    private static ModParamSpace ComposeWith(string? modsRoot)
    {
        (_, ContentRegistry registry) = Boot(modsRoot);
        return ModParamSpace.Compose(BuiltinEra1(), registry, "era1:");
    }

    private static string Serialized(IReadOnlyList<TowerSpec> towers) =>
        JsonSerializer.Serialize(
            new TowerSpecDocument("Building", towers), TowerSpecJsonContext.Default.TowerSpecDocument);

    // ------------------------------------------------ legal：装载扩展 --------

    [Fact] // [F-095] 三样板模组全过：零错误；塔清单=内置 4（文件序）+ more-arrows 1 + player-pack 2（id 序）
    public void Samples_ComposeClean_BuiltinOrderPreserved_ModTowersAppended()
    {
        ModParamSpace space = ComposeWithSamples();

        Assert.True(space.Ok);
        Assert.Equal(
            new[]
            {
                "era1:tower-wood-arrow", "era1:tower-rockfall", "era1:tower-spike-trap", "era1:tower-campfire",
                "more-arrows:tower-barbed", "player-pack:tower-cinder-mortar", "player-pack:tower-thorn-ballista",
            },
            space.Towers.Select(static t => t.Id).ToArray());
        Assert.Contains(space.Towers, t => t.Id == "player-pack:tower-thorn-ballista" && t.Dps == 18.0);
        Assert.Contains(space.Towers, t => t.Id == "player-pack:tower-cinder-mortar" && t.Traits?.BurnDps == 6);
    }

    [Fact] // [F-156] patch 叠加被 Combat 侧采纳：rebalance-patch 的木箭塔 dps 12 流入扩展塔清单
    public void PatchedBuiltinTower_RebuiltFromRegistryOverlay()
    {
        ModParamSpace space = ComposeWithSamples();

        TowerSpec wood = space.Towers.Single(t => t.Id == "era1:tower-wood-arrow");
        Assert.Equal(12.0, wood.Dps); // 本体 10 → 模组 12（rebalance-patch 全量替换）
    }

    [Fact] // [F-095] BuildPlan tower_id 并集：内置+模组全在；敌清单入 mod 敌；波组 roster 面可读
    public void UnionTowerIds_ContainModTowers_AndWaveRosterFaceReads()
    {
        ModParamSpace space = ComposeWithSamples();

        Assert.True(space.ContainsTower("era1:tower-wood-arrow"));
        Assert.True(space.ContainsTower("player-pack:tower-thorn-ballista"));
        Assert.False(space.ContainsTower("era1:tower-nonexistent"));
        Assert.Equal(7, space.BuildableTowerIds.Count);

        Assert.True(space.Enemies.TryGet("player-pack:enemy-shade", out EraCore.Units.EnemySpec shade));
        Assert.Equal(55, shade.Hp);
        Assert.False(space.Enemies.TryGet("era2:enemy-ram", out _)); // 他时代本体敌不进 E1 清单

        ModWaveRoster roster = Assert.Single(space.WaveRosters);
        Assert.Equal("player-pack:pack-night-patrol", roster.GroupId);
        Assert.Equal(new[] { "player-pack:enemy-shade" }, roster.Enemies);
        Assert.Equal(new[] { "player-pack:pack-night-patrol" }, roster.SpawnOpTargets);
    }

    [Fact] // [F-157][F-095] 军令别名词典模组源打通：模组塔中文名可解析到命名空间 id；卷轴面同源
    public void AliasDictionaries_ExtendWithModSurfaces()
    {
        ModParamSpace space = ComposeWithSamples();

        Assert.True(space.TowerAliases.TryResolve(TextNormalizer.Normalize("荆棘弩塔"), out string thorn));
        Assert.Equal("player-pack:tower-thorn-ballista", thorn);
        Assert.True(space.TowerAliases.TryResolve(TextNormalizer.Normalize("焚石"), out string cinder));
        Assert.Equal("player-pack:tower-cinder-mortar", cinder);
        Assert.True(space.TowerAliases.TryResolve(TextNormalizer.Normalize("倒刺塔"), out string barbed));
        Assert.Equal("more-arrows:tower-barbed", barbed);

        Assert.True(space.ScrollAliases.TryResolve(TextNormalizer.Normalize("疾风令"), out string scroll));
        Assert.Equal("player-pack:scroll-quickstep", scroll);
        Assert.Single(space.Scrolls, d => d.Id == "player-pack:scroll-quickstep" && d.CooldownTicks == 1350);
    }

    [Fact] // [F-095] 伤害走模组弹种：装载期一致性已由 Compose 强制（player-pack 数值合规零错误）
    public void ProjectileLink_PlayerPackTowers_CarryTheirProjectileDamage()
    {
        (_, ContentRegistry registry) = Boot(SamplesDir());

        foreach (string towerId in new[] { "player-pack:tower-thorn-ballista", "player-pack:tower-cinder-mortar" })
        {
            JsonContentItem tower = (JsonContentItem)registry.Get(ContentType.Building, towerId);
            JsonContentItem projectile = (JsonContentItem)registry.Get(
                ContentType.Projectile,
                JsonDocument.Parse(tower.RawJson).RootElement.GetProperty("ammo_item").GetString()!);
            double towerDamage = JsonDocument.Parse(tower.RawJson).RootElement.GetProperty("damage_per_shot").GetDouble();
            double projectileDamage = JsonDocument.Parse(projectile.RawJson).RootElement.GetProperty("damage_per_shot").GetDouble();
            Assert.Equal(projectileDamage, towerDamage); // 塔伤害=弹种伤害（装载门已强制，此处独立复核）
        }
    }

    // ------------------------------------------------ 金样断言：无模组零漂移 --

    [Fact] // [F-095] 无模组时零漂移：塔清单=本体原序原值（序列化全等）；别名词典=T-090 本体装载面全等
    public void NoMods_ComposeEqualsBuiltinCatalogExactly_ZeroDrift()
    {
        IReadOnlyList<TowerSpec> builtin = BuiltinEra1();
        ModParamSpace space = ComposeWith(modsRoot: null);

        Assert.True(space.Ok);
        Assert.Empty(space.Errors);
        Assert.Equal(Serialized(builtin), Serialized(space.Towers)); // 逐字段全等（含序）
        Assert.Equal(
            AliasDictionary.FromTowerJson(GrammarTestPaths.Era1TowersJson()).Surfaces,
            space.TowerAliases.Surfaces); // L1 词典面与 T-090 本体管线输出全等
        Assert.Equal(
            AliasDictionary.FromContentRegistryJson(GrammarTestPaths.Era2ScrollsJson()).Surfaces,
            space.ScrollAliases.Surfaces);
        Assert.False(space.ContainsTower("player-pack:tower-thorn-ballista")); // 模组塔不在封闭集
    }

    [Fact] // [F-095] 无模组时敌清单零漂移：era1 本体敌全量同值；重启即回封闭集
    public void NoMods_EnemyCatalogEqualsFirstPartyParse()
    {
        ModParamSpace space = ComposeWith(modsRoot: null);
        var firstParty = EraCore.Units.EnemySpecCatalog.Parse(GrammarTestPaths.Era1EnemiesJson());

        Assert.Equal(firstParty.Count, space.Enemies.Count);
        foreach (string id in new[] { "era1:enemy-beastling", "era1:enemy-hunter", "era1:enemy-boar", "era1:enemy-tribesman" })
        {
            Assert.True(firstParty.TryGet(id, out EraCore.Units.EnemySpec expected));
            Assert.True(space.Enemies.TryGet(id, out EraCore.Units.EnemySpec actual));
            Assert.Equal(expected.Hp, actual.Hp);
            Assert.Equal(expected.Armor, actual.Armor);
            Assert.Equal(expected.SpeedPxPerTick, actual.SpeedPxPerTick);
        }
    }

    // ------------------------------------------------ Schema 门（动态枚举） ----

    [Fact] // [F-095] 动态并集进 Schema：模组塔 id 过闸；并集外 id 拒；不接线=历史行为逐字节不变
    public void SchemaValidator_TowerEnum_WidenOnlyWhenWired()
    {
        ModParamSpace space = ComposeWithSamples();
        string goodMod = ActionJsonSamples.BuildPlan(towerId: "player-pack:tower-thorn-ballista");
        string goodBuiltin = ActionJsonSamples.BuildPlan(towerId: "era1:tower-wood-arrow");
        string garbage = ActionJsonSamples.BuildPlan(towerId: "era1:tower-nonsense");

        var wired = new JsonSchemaValidator(static () => 2, space.BuildableTowerIds);
        Assert.True(wired.Validate(goodMod).IsValid); // 模组塔过 Schema 门（F-095 验收）
        Assert.True(wired.Validate(goodBuiltin).IsValid);
        SchemaValidationResult denied = wired.Validate(garbage);
        Assert.False(denied.IsValid);
        Assert.Contains(denied.Errors, e => e.Pointer == "/tower_id" && e.Message.Contains("builtin+registry tower union", StringComparison.Ordinal));

        var unwired = new JsonSchemaValidator(static () => 2);
        Assert.True(unwired.Validate(goodMod).IsValid); // 未接线=旧口径：Schema 层不裁 tower_id（handler 兜底）
        Assert.True(unwired.Validate(garbage).IsValid);
        Assert.True(unwired.Validate(goodBuiltin).IsValid);
    }

    [Fact] // [F-095] 无模组并集=内置集：接线后对全部本体塔 id 的裁决与内置集完全一致（零漂移）
    public void SchemaValidator_NoModUnion_EqualsBuiltinSet_OnRealIds()
    {
        ModParamSpace space = ComposeWith(modsRoot: null);
        var wired = new JsonSchemaValidator(static () => 2, space.BuildableTowerIds);
        var builtin = new JsonSchemaValidator(
            static () => 2,
            new HashSet<string>(BuiltinEra1().Select(static t => t.Id), StringComparer.Ordinal));

        foreach (TowerSpec tower in BuiltinEra1())
        {
            string payload = ActionJsonSamples.BuildPlan(towerId: tower.Id);
            Assert.Equal(builtin.Validate(payload).IsValid, wired.Validate(payload).IsValid);
            Assert.True(wired.Validate(payload).IsValid);
        }
    }

    // ------------------------------------------------ 边界负测：越带拒载 ------

    private const string BadGrowthTower = """
        {
          "id": "tower-bad-growth",
          "content_type": "Building",
          "class": "pierce",
          "name_zh": "坏成长塔",
          "name_en": "Bad Growth Tower",
          "aliases": ["坏成长塔"],
          "tier": 1, "max_tier": 1,
          "cost_total": 50, "dps": 18.0, "damage_per_shot": 18,
          "reload_ticks": 30, "range_cells": 5.0, "hp": 130,
          "tier_growth": { "dps_multiplier_per_tier": 5.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
          "traits": {}
        }
        """;

    private const string OutOfBandTower = """
        {
          "id": "tower-overcurve",
          "content_type": "Building",
          "class": "pierce",
          "name_zh": "越带塔",
          "name_en": "Overcurve Tower",
          "aliases": ["越带塔"],
          "tier": 1, "max_tier": 1,
          "cost_total": 999, "dps": 999.0, "damage_per_shot": 999,
          "reload_ticks": 30, "range_cells": 5.0, "hp": 130,
          "tier_growth": { "dps_multiplier_per_tier": 2.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
          "traits": {}
        }
        """;

    private const string AliasClashTower = """
        {
          "id": "tower-clash",
          "content_type": "Building",
          "class": "pierce",
          "name_zh": "撞名塔",
          "name_en": "Clash Tower",
          "aliases": ["木箭塔"],
          "tier": 1, "max_tier": 1,
          "cost_total": 50, "dps": 18.0, "damage_per_shot": 18,
          "reload_ticks": 30, "range_cells": 5.0, "hp": 130,
          "tier_growth": { "dps_multiplier_per_tier": 2.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
          "traits": {}
        }
        """;

    private const string MismatchTowerDoc = """
        {
          "mod_id": "bad-link",
          "name_zh": "坏弹链",
          "name_en": "Bad Link",
          "version": "1.0.0",
          "content_type": "Building",
          "items": [
            {
              "id": "tower-bad-link",
              "content_type": "Building",
              "class": "pierce",
              "name_zh": "坏弹链塔",
              "name_en": "Bad Link Tower",
              "aliases": ["坏弹链塔"],
              "tier": 1, "max_tier": 1,
              "cost_total": 50, "dps": 18.0, "damage_per_shot": 20,
              "reload_ticks": 30, "range_cells": 5.0, "hp": 130,
              "ammo_item": "bad-link-proj:proj-x",
              "tier_growth": { "dps_multiplier_per_tier": 2.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
              "traits": { "slow_percent": 15, "slow_ticks": 45 }
            }
          ]
        }
        """;

    private const string MismatchProjectileDoc = """
        {
          "mod_id": "bad-link-proj",
          "name_zh": "坏弹链弹种",
          "name_en": "Bad Link Projectiles",
          "version": "1.0.0",
          "content_type": "Projectile",
          "items": [
            {
              "id": "proj-x",
              "content_type": "Projectile",
              "name_zh": "坏弹",
              "name_en": "Bad Bolt",
              "damage_per_shot": 18,
              "speed_px_per_tick": 6.0,
              "pierce_count": 1,
              "on_hit": [ { "op": "slow_percent", "percent": 20, "ticks": 45 } ]
            }
          ]
        }
        """;

    private const string ChainTowerDoc = """
        {
          "mod_id": "bad-chain",
          "name_zh": "坏阶梯",
          "name_en": "Bad Chain",
          "version": "1.0.0",
          "content_type": "Building",
          "items": [
            {
              "id": "tower-chain-1",
              "content_type": "Building",
              "class": "pierce",
              "name_zh": "坏阶梯一级",
              "name_en": "Chain One",
              "aliases": ["坏阶梯一级"],
              "tier": 1, "max_tier": 2,
              "cost_total": 50, "dps": 18.0, "damage_per_shot": 18,
              "reload_ticks": 30, "range_cells": 5.0, "hp": 130,
              "upgrades_to": "bad-chain:tower-chain-2",
              "tier_growth": { "dps_multiplier_per_tier": 2.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
              "traits": {}
            },
            {
              "id": "tower-chain-2",
              "content_type": "Building",
              "class": "pierce",
              "name_zh": "坏阶梯二级",
              "name_en": "Chain Two",
              "aliases": ["坏阶梯二级"],
              "tier": 2, "max_tier": 2,
              "cost_total": 100, "dps": 36.0, "damage_per_shot": 36,
              "reload_ticks": 30, "range_cells": 6.5, "hp": 130,
              "tier_growth": { "dps_multiplier_per_tier": 2.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
              "traits": {}
            }
          ]
        }
        """;

    private static ModParamSpace ComposeSingleFile(string json)
    {
        string root = TempModRoot(("the-mod.json", json));
        try
        {
            return ComposeWith(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string SingleFileMod(string itemJson) => $$"""
        {
          "mod_id": "the-mod",
          "name_zh": "负测模组",
          "name_en": "Negative Mod",
          "version": "1.0.0",
          "content_type": "Building",
          "items": [ {{itemJson}} ]
        }
        """;

    [Fact] // [F-095] 模组塔越升级律带→装载错误带行号（dps 乘子 5.0 出 2.0±15% 带）；塔被拒不入清单
    public void ModTower_OffUpgradeLawBand_IsRefusedAtLoadWithLine()
    {
        ModLoadError error = Assert.Single(ComposeSingleFile(SingleFileMod(BadGrowthTower)).Errors);

        Assert.Equal(ModErrorKind.Schema, error.Kind);
        Assert.Equal("the-mod", error.ModId);
        Assert.Equal("the-mod.json", error.File);
        Assert.True(error.Line > 0, "错误必须带 1 起行号");
        Assert.Contains("dps_multiplier", error.Message, StringComparison.Ordinal);
        Assert.False(ComposeSingleFile(SingleFileMod(BadGrowthTower)).ContainsTower("the-mod:tower-bad-growth"));
    }

    [Fact] // [F-095] 模组塔数值越锚点带（cost/dps 999 出 [30,60]×[10,20]）→装载错误；R7 锚点延伸到模组空间
    public void ModTower_OutOfAnchorBand_IsRefusedAtLoad()
    {
        ModParamSpace space = ComposeSingleFile(SingleFileMod(OutOfBandTower));

        Assert.False(space.Ok);
        Assert.Contains(space.Errors, e =>
            e.Kind == ModErrorKind.Schema
            && e.Path == "/dps"
            && e.Message.Contains("[10, 20]", StringComparison.Ordinal)
            && e.Line > 0);
        Assert.DoesNotContain(space.Towers, t => t.Id == "the-mod:tower-overcurve");
    }

    [Fact] // [F-095] 模组塔别名撞内置表面（木箭塔）→冲突报错整塔拒载；词典保持本体面
    public void ModTower_AliasClashesWithBuiltin_IsRefusedWithConflictError()
    {
        ModParamSpace space = ComposeSingleFile(SingleFileMod(AliasClashTower));

        ModLoadError error = Assert.Single(space.Errors);
        Assert.Equal(ModErrorKind.Duplicate, error.Kind);
        Assert.Contains("collides", error.Message, StringComparison.Ordinal);
        Assert.True(error.Line > 0);
        Assert.False(space.ContainsTower("the-mod:tower-clash"));
        Assert.Equal("era1:tower-wood-arrow", space.TowerAliases.TryResolve(TextNormalizer.Normalize("木箭塔"), out string owner) ? owner : null);
    }

    [Fact] // [F-095] 塔伤害与弹种伤害不一致（20 vs 18）且 on_hit 与 traits 两面不一致（20 vs 15）→弹种联动拒载
    public void ModTower_ProjectileDamageMismatch_IsRefused()
    {
        string root = TempModRoot(("building.json", MismatchTowerDoc), ("projectile.json", MismatchProjectileDoc));
        try
        {
            (_, ContentRegistry registry) = Boot(root);
            ModParamSpace space = ModParamSpace.Compose(BuiltinEra1(), registry, "era1:");

            Assert.Contains(space.Errors, e =>
                e.Kind == ModErrorKind.Schema
                && e.Message.Contains("projectile link mismatch", StringComparison.Ordinal)
                && e.Message.Contains("damage_per_shot", StringComparison.Ordinal)
                && e.Line > 0);
            Assert.Contains(space.Errors, e =>
                e.Message.Contains("slow_percent", StringComparison.Ordinal)
                && e.Message.Contains("disagrees", StringComparison.Ordinal));
            Assert.False(space.ContainsTower("bad-link:tower-bad-link"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] // [F-095] 模组升级链越冻结阶梯（cost 100 ≠ 50×3）→装载错误带行号；链上两塔均按律校验
    public void ModTower_ChainOffCostLadder_IsRefusedAtLoad()
    {
        ModParamSpace space = ComposeSingleFile(ChainTowerDoc);

        ModLoadError error = Assert.Single(space.Errors);
        Assert.Equal(ModErrorKind.Schema, error.Kind);
        Assert.Equal("/upgrades_to", error.Path);
        Assert.Contains("cost_step", error.Message, StringComparison.Ordinal);
        Assert.True(error.Line > 0);
        Assert.False(space.ContainsTower("bad-chain:tower-chain-1"));
    }
}
