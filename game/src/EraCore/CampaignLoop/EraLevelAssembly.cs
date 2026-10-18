using System.Text.Json;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Energy;
using EraCore.Sim;
using EraCore.Units;

namespace EraCore.CampaignLoop;

// [C-12][F-001][F-055] T-160 战役宿主面最小引擎扩（REV-20261018-T160 依据）：
// 手工关→引擎级 SimulationConfig/SimCommand 的组装律从 EraCore.Tests 侧
// （HandcraftedLevelAssemblyTests.BuildConfig/BuildCommands + 各时代
// PlaythroughTests 的能源表挂载 + FinalBossLevelTests 的践踏调参）提升为
// EraCore 生产面。表现层战役宿主与测试两侧共用同一份组装代码——测试侧
// 改为薄委托（既有全部关卡测试随之变成生产组装路径的回归网）。
//
// 组装律（与既有测试逐字段同律，冻结面）：
//   基础=T-085 BuildConfig（towers/enemies/仓库等级全出自 era 内容表；
//         hero 行读 era2/heroes.json 跨代遗产位——全仓唯一 heroes.json）；
//   践踏=E1~E4 终局 Boss 踩踏调参族（FinalBossLevelTests.Finals/V10 链
//         TramplePerEra 同锚：60/100/100/0、周期 45；E5/E6 无践踏——
//         Era5/Era6LevelPlaythroughTests 同口径）；
//   能源=E3 过热 / E4 过热+熔毁+辐射 / E5 过热+湮灭+力场塔反湮灭位
//         （Era3/Era4/Era5LevelPlaythroughTests 同律，数值全出自
//         era{N}/era.json，CS-2.3）；E1/E2/E6 无风险通道挂载。

/// <summary>
/// Production assembly of one handcrafted level row into engine-level
/// <see cref="SimulationConfig"/> + the wave-anchored command stream
/// (T-160 生产化；T-085 组装律). Every field comes from the level row and
/// the era content tables — the assembly itself embeds no balance numbers
/// beyond the frozen trample calibration family (test-calibration anchors,
/// ci-allow 登记于 <see cref="TrampleFor"/>).
/// </summary>
public static class EraLevelAssembly
{
    /// <summary>
    /// 践踏调参族（冻结校准锚，[NEW-原型校准] 先例=FinalBossLevelTests.Finals
    /// 与 V10FullClearChain.TramplePerEra——测试侧校准的同一数值族）：E1 踏群者
    /// 60 / E2 铁蹄 100 / E3 堡垒号 100 / E4 夜枭 0（无践踏）；周期恒 45 tick。
    /// E5 主宰/E6 堕落守望者无践踏通道（Era5/Era6LevelPlaythroughTests 同口径，
    /// 返回 null=不挂 TrampleProfile）。非 Boss 关不消费（Boss 批次专属通道）。
    /// </summary>
    public static TrampleProfile? TrampleFor(int eraIndex) => eraIndex switch
    {
        1 => new TrampleProfile(Damage: 60, PeriodTicks: 45),  // ci-allow: R7 T-160 校准族=E1 踏群者（FinalBossLevelTests 同锚）
        2 => new TrampleProfile(Damage: 100, PeriodTicks: 45), // ci-allow: R7 T-160 校准族=E2 铁蹄（同锚）
        3 => new TrampleProfile(Damage: 100, PeriodTicks: 45), // ci-allow: R7 T-160 校准族=E3 堡垒号（同锚）
        4 => new TrampleProfile(Damage: 0, PeriodTicks: 45),   // ci-allow: R7 T-160 校准族=E4 夜枭无践踏（同锚）
        _ => null,
    };

    /// <summary>The content era directory segment of a level row ("era1".."era6" from its era id).</summary>
    public static string EraDirOf(HandcraftedLevel level) => level.Era switch
    {
        string era when era.StartsWith("era6:", StringComparison.Ordinal) => "era6",
        string era when era.StartsWith("era5:", StringComparison.Ordinal) => "era5",
        string era when era.StartsWith("era4:", StringComparison.Ordinal) => "era4",
        string era when era.StartsWith("era3:", StringComparison.Ordinal) => "era3",
        string era when era.StartsWith("era2:", StringComparison.Ordinal) => "era2",
        _ => "era1",
    };

    /// <summary>The era ordinal (1..6) of a level row's era id.</summary>
    public static int EraIndexOf(HandcraftedLevel level)
    {
        string dir = EraDirOf(level);
        return int.TryParse(dir.AsSpan("era".Length), out int era) && era is >= 1 and <= 6
            ? era
            : throw new HandcraftedLevelSchemaException($"{level.Id}: 未登记时代 id {level.Era}");
    }

    /// <summary>
    /// Assembles one level row into the engine-level
    /// <see cref="SimulationConfig"/> (T-085 组装律 + 各时代面). All content
    /// tables are read from <paramref name="contentRoot"/> (the
    /// <c>game/content</c> directory — the caller resolves it repo-relative,
    /// RL9).
    /// </summary>
    /// <exception cref="InvalidOperationException">A content file the level references is missing.</exception>
    /// <exception cref="HandcraftedLevelSchemaException">A referenced id does not resolve in the era tables.</exception>
    public static SimulationConfig BuildConfig(HandcraftedLevel level, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(level);
        string eraDir = EraDirOf(level);
        int eraIndex = EraIndexOf(level);
        string eraDirPath = System.IO.Path.Combine(contentRoot, eraDir);

        var entries = level.Map.EntryCells.Select(c => new CellCoord(c.X, c.Y)).ToArray();
        HeroSpec? hero = level.HeroId is null
            ? null
            : HeroSpecCatalog.Parse(ReadFile(System.IO.Path.Combine(contentRoot, "era2"), "heroes.json")).Get(level.HeroId);
        var stock = level.InitialStock
            .Select(line => new KeyValuePair<ResourceKey, double>(new ResourceKey(line.Resource), line.Amount))
            .ToList();

        var baseConfig = new SimulationConfig(
            level.Map.Width,
            level.Map.Height,
            level.Map.ForestRatio,
            level.Map.RockRatio,
            level.Map.WaterRatio,
            new CellCoord(level.Map.BaseCell.X, level.Map.BaseCell.Y),
            entries,
            TowerSpec.LoadCollection(ReadFile(eraDirPath, "towers.json")),
            EnemySpecCatalog.Parse(ReadFile(eraDirPath, "enemies.json")),
            new WaveSpawnConfig(
                BaseCount: level.Waves.BaseCount,
                BeginWave: level.Waves.BeginWave,
                Spacing: level.Waves.Spacing,
                Scaling: level.Waves.Scaling,
                ThreatLevel: level.Threat,
                RegularRoster: level.Waves.RegularRoster,
                EliteLineEnabled: level.Waves.EliteLineEnabled,
                EliteEnemyId: level.Waves.EliteEnemyId,
                BossWave: level.Waves.BossWave ?? 20, // ci-allow: R7 T-160 Boss 波缺省占位=WaveSpawnConfig 域常量（T-085 组装律同锚——null 行 BossEnemyId 必为 null 即禁用，非平衡数值）
                BossEnemyId: level.Waves.BossEnemyId,
                FirstWaveGraceMultiplier: level.FirstWaveGraceMultiplier,
                ValleySeconds: level.Waves.ValleySeconds,
                BatchesPerWave: level.Waves.BatchesPerWave,
                EntryCells: entries,
                Trample: TrampleFor(eraIndex)),
            stock,
            WarehouseCell: new CellCoord(level.Map.WarehouseCell.X, level.Map.WarehouseCell.Y),
            Costs: new BuildCostPolicy(new ResourceKey(level.InitialStock[0].Resource)),
            Hero: hero,
            HeroCell: level.Map.HeroCell is null ? null : new CellCoord(level.Map.HeroCell.X, level.Map.HeroCell.Y),
            WarehouseLevel: WarehouseLevelOf(ReadFile(eraDirPath, "era.json")));

        return eraIndex switch
        {
            3 => baseConfig with { EnergyRisk = Era3EnergyRisk(ReadFile(eraDirPath, "era.json")) },
            4 => baseConfig with
            {
                EnergyRisk = Era4EnergyRisk(ReadFile(eraDirPath, "era.json")),
                RadiationZoneHpDecayPerSecond = Era4RadiationZoneHpDecayPerSecond(ReadFile(eraDirPath, "era.json")),
            },
            5 => baseConfig with
            {
                EnergyRisk = Era5EnergyRisk(ReadFile(eraDirPath, "era.json")),
                // 力场塔反湮灭配置位（SP-1b L54：era5:tower-force-field 在场则湮灭半径减半）
                AnnihilationForceFieldTowerId = "era5:tower-force-field",
            },
            _ => baseConfig, // E1/E2 无能源风险通道；E6 零点能=无风险（Era6LevelPlaythroughTests 同口径）
        };
    }

    /// <summary>
    /// 命令流 → <see cref="SimCommand"/>：wave k 步骤落在 k 波开场谷内（wave 1
    /// 在宽限尾）。converter 步骤的配方载荷重建自该时代 recipes.json 行
    /// （inputs amount → items；output amount=1，v3.2 §5 一 craft 产 1 件——
    /// SimE2Fixture 载荷形状）。
    /// </summary>
    public static IReadOnlyList<SimCommand> BuildCommands(HandcraftedLevel level, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(level);
        string eraDir = EraDirOf(level);
        long intervalTicks = (long)Math.Round(
            Balance.Campaign.WaveIntervalSeconds(level.Threat) * Simulation.TicksPerSecond,
            MidpointRounding.AwayFromZero);
        long graceTicks = (long)Math.Round(intervalTicks * level.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        long valleyTicks = (long)Math.Round(level.Waves.ValleySeconds * Simulation.TicksPerSecond, MidpointRounding.AwayFromZero);

        var commands = new List<SimCommand>();
        foreach (ScriptStep step in level.CommandScript.OrderBy(s => s.Wave))
        {
            long tick = Math.Max(0, graceTicks + ((step.Wave - 1) * intervalTicks) - valleyTicks);
            commands.Add(new SimCommand(tick, SimCommandKind.PlayerBuild, Payload(step, contentRoot, eraDir)));
        }

        return commands;
    }

    /// <summary>仓库等级 of one era (era.json warehouse_level，v3.2 §5).</summary>
    public static int WarehouseLevelOf(string eraJson)
    {
        using var doc = JsonDocument.Parse(eraJson);
        return doc.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("warehouse_level").GetInt32();
    }

    /// <summary>
    /// The E3 overheat risk table of era3/era.json energy.risk, assembled into
    /// the production <see cref="EnergyRiskTuning"/> (T-113: E3+ 关卡把内容
    /// 阈值行挂进 SimulationConfig；数值全部出自内容表，CS-2.3).
    /// </summary>
    public static EnergyRiskTuning Era3EnergyRisk(string eraJson)
    {
        using var doc = JsonDocument.Parse(eraJson);
        JsonElement row = doc.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("energy").GetProperty("risk");
        JsonElement trigger = row.GetProperty("trigger");
        JsonElement heat = row.GetProperty("heat_dynamics");
        JsonElement consequences = row.GetProperty("consequences");
        var tuning = new EnergyRiskTuning(
            Overheat: new OverheatTuning(
                OverloadLoadRatio: trigger.GetProperty("overload_threshold_percent").GetDouble() / 100.0,
                OverloadSustainedSeconds: trigger.GetProperty("sustained_s").GetDouble(),
                HeatCapacity: trigger.GetProperty("heat_capacity").GetDouble(),
                BaseHeatPerSecond: heat.GetProperty("gain_per_s_overloaded").GetDouble(),
                HeatLoadStepRatio: heat.GetProperty("overload_step_percent").GetDouble() / 100.0,
                HeatPerLoadStepPerSecond: heat.GetProperty("bonus_gain_per_s_per_extra_10pct_overload").GetDouble(),
                HeatCooldownPerSecond: heat.GetProperty("decay_per_s_unloaded").GetDouble(),
                ExplosionRadiusCells: row.GetProperty("explosion_radius_cells").GetDouble(),
                BuildingDamageMaxHpFraction: consequences.GetProperty("building_damage_fraction_of_max_hp").GetDouble(),
                EnemyDamage: consequences.GetProperty("enemy_damage").GetDouble(),
                GeneratorDowntimeSeconds: consequences.GetProperty("culprit_outage_s").GetDouble(),
                GeneratorDerateFraction: consequences.GetProperty("culprit_degraded_output_fraction").GetDouble(),
                GeneratorDerateSeconds: consequences.GetProperty("culprit_degraded_duration_s").GetDouble()),
            Meltdown: null); // E3 无核电（Sp4RiskTables.OverheatOnly 同口径）
        tuning.Overheat.Validate();
        return tuning;
    }

    /// <summary>
    /// The E4 grid risk tables of era4/era.json (overheat + meltdown), assembled
    /// into the production <see cref="EnergyRiskTuning"/> (T-124: E4 关卡把
    /// 内容阈值行挂进 SimulationConfig——过热沿用表值+熔毁通道 E4 起挂入；
    /// 数值全部出自内容表，CS-2.3).
    /// </summary>
    public static EnergyRiskTuning Era4EnergyRisk(string eraJson)
    {
        using var doc = JsonDocument.Parse(eraJson);
        JsonElement energy = doc.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("energy");
        JsonElement row = energy.GetProperty("risk");
        JsonElement trigger = row.GetProperty("trigger");
        JsonElement heat = row.GetProperty("heat_dynamics");
        JsonElement consequences = row.GetProperty("consequences");
        JsonElement meltdown = energy.GetProperty("meltdown");
        JsonElement mTrigger = meltdown.GetProperty("trigger");
        JsonElement mConsequences = meltdown.GetProperty("consequences");
        var tuning = new EnergyRiskTuning(
            Overheat: new OverheatTuning(
                OverloadLoadRatio: trigger.GetProperty("overload_threshold_percent").GetDouble() / 100.0,
                OverloadSustainedSeconds: trigger.GetProperty("sustained_s").GetDouble(),
                HeatCapacity: trigger.GetProperty("heat_capacity").GetDouble(),
                BaseHeatPerSecond: heat.GetProperty("gain_per_s_overloaded").GetDouble(),
                HeatLoadStepRatio: heat.GetProperty("overload_step_percent").GetDouble() / 100.0,
                HeatPerLoadStepPerSecond: heat.GetProperty("bonus_gain_per_s_per_extra_10pct_overload").GetDouble(),
                HeatCooldownPerSecond: heat.GetProperty("decay_per_s_unloaded").GetDouble(),
                ExplosionRadiusCells: row.GetProperty("explosion_radius_cells").GetDouble(),
                BuildingDamageMaxHpFraction: consequences.GetProperty("building_damage_fraction_of_max_hp").GetDouble(),
                EnemyDamage: consequences.GetProperty("enemy_damage").GetDouble(),
                GeneratorDowntimeSeconds: consequences.GetProperty("culprit_outage_s").GetDouble(),
                GeneratorDerateFraction: consequences.GetProperty("culprit_degraded_output_fraction").GetDouble(),
                GeneratorDerateSeconds: consequences.GetProperty("culprit_degraded_duration_s").GetDouble()),
            Meltdown: new MeltdownTuning(
                OverloadLoadRatio: mTrigger.GetProperty("overload_threshold_percent").GetDouble() / 100.0,
                OverloadSustainedSeconds: mTrigger.GetProperty("sustained_s").GetDouble(),
                InstantLoadRatio: mTrigger.GetProperty("instant_threshold_percent").GetDouble() / 100.0,
                CountdownSeconds: meltdown.GetProperty("countdown_s").GetDouble(),
                InterruptUnloadFraction: meltdown.GetProperty("interrupt").GetProperty("unload_percent").GetDouble() / 100.0,
                RequireCoolant: meltdown.GetProperty("coolant_required").GetBoolean(),
                ExplosionRadiusCells: meltdown.GetProperty("explosion_radius_cells").GetDouble(),
                BuildingDamageMaxHpFraction: mConsequences.GetProperty("building_damage_fraction_of_max_hp").GetDouble(),
                RadiationZoneSeconds: mConsequences.GetProperty("radiation_zone_s").GetDouble(),
                RadiationEnemySlowFraction: mConsequences.GetProperty("radiation_enemy_slow_percent").GetDouble() / 100.0,
                WreckRecoveryCostFraction: mConsequences.GetProperty("wreck_recovery_cost_fraction").GetDouble()));
        tuning.Validate();
        return tuning;
    }

    /// <summary>The E4 radiation-zone per-second decay of era4/era.json (T-123 [NEW] 行).</summary>
    public static double Era4RadiationZoneHpDecayPerSecond(string eraJson)
    {
        using var doc = JsonDocument.Parse(eraJson);
        return doc.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("energy").GetProperty("meltdown").GetProperty("consequences")
            .GetProperty("radiation_zone_hp_decay_per_s").GetDouble();
    }

    /// <summary>
    /// The E5 fusion risk tables of era5/era.json (overheat 沿用 + annihilation),
    /// assembled into the production <see cref="EnergyRiskTuning"/> (T-134: E5
    /// 关卡把内容阈值行挂进 SimulationConfig——过热沿用表值+湮灭通道 E5 起
    /// 挂入、熔毁通道=null（聚变代无核电，SP-4 行域：E4=熔毁/E5=湮灭）；数值
    /// 全部出自内容表，CS-2.3).
    /// </summary>
    public static EnergyRiskTuning Era5EnergyRisk(string eraJson)
    {
        using var doc = JsonDocument.Parse(eraJson);
        JsonElement energy = doc.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("energy");
        JsonElement row = energy.GetProperty("risk");
        JsonElement trigger = row.GetProperty("trigger");
        JsonElement heat = row.GetProperty("heat_dynamics");
        JsonElement consequences = row.GetProperty("consequences");
        JsonElement annihilation = energy.GetProperty("annihilation");
        JsonElement aTrigger = annihilation.GetProperty("trigger");
        JsonElement aConsequences = annihilation.GetProperty("consequences");
        var tuning = new EnergyRiskTuning(
            Overheat: new OverheatTuning(
                OverloadLoadRatio: trigger.GetProperty("overload_threshold_percent").GetDouble() / 100.0,
                OverloadSustainedSeconds: trigger.GetProperty("sustained_s").GetDouble(),
                HeatCapacity: trigger.GetProperty("heat_capacity").GetDouble(),
                BaseHeatPerSecond: heat.GetProperty("gain_per_s_overloaded").GetDouble(),
                HeatLoadStepRatio: heat.GetProperty("overload_step_percent").GetDouble() / 100.0,
                HeatPerLoadStepPerSecond: heat.GetProperty("bonus_gain_per_s_per_extra_10pct_overload").GetDouble(),
                HeatCooldownPerSecond: heat.GetProperty("decay_per_s_unloaded").GetDouble(),
                ExplosionRadiusCells: row.GetProperty("explosion_radius_cells").GetDouble(),
                BuildingDamageMaxHpFraction: consequences.GetProperty("building_damage_fraction_of_max_hp").GetDouble(),
                EnemyDamage: consequences.GetProperty("enemy_damage").GetDouble(),
                GeneratorDowntimeSeconds: consequences.GetProperty("culprit_outage_s").GetDouble(),
                GeneratorDerateFraction: consequences.GetProperty("culprit_degraded_output_fraction").GetDouble(),
                GeneratorDerateSeconds: consequences.GetProperty("culprit_degraded_duration_s").GetDouble()),
            Meltdown: null, // E5 聚变代无熔毁通道（SP-4 行域：E4=熔毁/E5=湮灭）
            Annihilation: new AnnihilationTuning(
                OverloadLoadRatio: aTrigger.GetProperty("overload_threshold_percent").GetDouble() / 100.0,
                OverloadSustainedSeconds: aTrigger.GetProperty("sustained_s").GetDouble(),
                ExplosionRadiusCells: annihilation.GetProperty("explosion_radius_cells").GetDouble(),
                UnitDamage: aConsequences.GetProperty("unit_damage").GetDouble(),
                ChainDamageDecayFraction: aConsequences.GetProperty("chain_reaction").GetProperty("damage_decay_per_chain").GetDouble(),
                ForceFieldRadiusMultiplier: aConsequences.GetProperty("force_field_radius_multiplier").GetDouble(),
                WreckRecoveryCostFraction: aConsequences.GetProperty("wreck_recovery_cost_fraction").GetDouble()));
        tuning.Validate();
        return tuning;
    }

    // ------------------------------------------------------------------ 私有

    private static string Payload(ScriptStep step, string contentRoot, string eraDir) => step.Verb switch
    {
        "build-labor-camp" => CellPayload("build-labor-camp", step.Cell!),
        "build-harvester" => "{\"verb\":\"build-harvester\",\"resource\":\"" + step.Resource
            + "\",\"seats\":" + step.Seats + ",\"cell\":{\"x\":" + step.Cell!.X + ",\"y\":" + step.Cell.Y + "}}",
        "build-converter" => step.ConverterRecipe is null
            ? CellPayload("build-converter", step.Cell!)
            : "{\"verb\":\"build-converter\",\"recipe\":" + ConverterRecipePayload(contentRoot, eraDir, step.ConverterRecipe)
                + ",\"cell\":{\"x\":" + step.Cell!.X + ",\"y\":" + step.Cell.Y + "}}",
        "build-tower" => "{\"verb\":\"build-tower\",\"tower\":\"" + step.Tower
            + "\",\"cell\":{\"x\":" + step.Cell!.X + ",\"y\":" + step.Cell.Y + "}}",
        "assign-carrier" => "{\"verb\":\"assign-carrier\",\"from\":" + step.FromEntity + ",\"to\":" + step.ToEntity
            + (step.CarrierTier is null ? "" : ",\"tier\":\"" + step.CarrierTier + "\"") + "}",
        "upgrade-tower" => "{\"verb\":\"upgrade-tower\",\"target\":" + step.Entity + ",\"to_tier\":" + step.ToTier + "}",
        "hero-order" => step.Order == "move"
            ? "{\"verb\":\"hero-order\",\"order\":\"move\",\"cell\":{\"x\":" + step.Cell!.X + ",\"y\":" + step.Cell.Y + "}}"
            : "{\"verb\":\"hero-order\",\"order\":\"" + step.Order + "\"}",
        _ => throw new HandcraftedLevelSchemaException($"未登记命令动词 {step.Verb}"),
    };

    private static string CellPayload(string verb, CellPoint cell) =>
        "{\"verb\":\"" + verb + "\",\"cell\":{\"x\":" + cell.X + ",\"y\":" + cell.Y + "}}";

    /// <summary>
    /// Rebuilds the converter recipe payload JSON of one recipes.json row
    /// (inputs amount → items；output amount=1，v3.2 §5 一 craft 产 1 件) — the
    /// same shape the E2 fixture passes to the build-converter command.
    /// </summary>
    private static string ConverterRecipePayload(string contentRoot, string eraDir, string recipeId)
    {
        using var doc = JsonDocument.Parse(ReadFile(System.IO.Path.Combine(contentRoot, eraDir), "recipes.json"));
        JsonElement row = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == recipeId);
        var inputs = row.GetProperty("inputs").EnumerateArray()
            .Select(i => $"{{\"resource\":\"{i.GetProperty("resource").GetString()}\",\"items\":{i.GetProperty("amount").GetInt32()}}}");
        return "{\"inputs\":[" + string.Join(",", inputs) + "],\"output\":\""
            + row.GetProperty("output").GetProperty("resource").GetString()
            + "\",\"craft_ticks\":" + row.GetProperty("craft_ticks").GetInt32() + "}";
    }

    private static string ReadFile(string dir, string fileName)
    {
        string path = System.IO.Path.Combine(dir, fileName);
        return System.IO.File.Exists(path)
            ? System.IO.File.ReadAllText(path)
            : throw new InvalidOperationException($"内容文件缺失：{dir}/{fileName}（战役宿主须在仓库内运行）");
    }
}
