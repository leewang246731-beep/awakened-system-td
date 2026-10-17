using System.Text.Json;
using EraCore.Combat;
using EraCore.Energy;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Levels;

/// <summary>
/// Shared content fixture of the handcrafted-level suite (T-085): the era
/// tables the level rows reference (towers/enemies/heroes of both eras, the
/// SP-8 objective pools, the era warehouse levels and the T-079 E2 schedule
/// rows), all loaded repo-relative from game/content (RL9). Pure loader —
/// every number stays in the content tables (CS-2.3).
/// </summary>
internal static class HandcraftedLevelContent
{
    private static DirectoryInfo FindContentDir(string relativeUnderContent)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", relativeUnderContent);
            if (Directory.Exists(probe))
            {
                return new DirectoryInfo(probe);
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/{relativeUnderContent}（测试须在仓库内运行）");
    }

    private static string ReadText(params string[] relativeUnderContent)
    {
        string path = Path.Combine([FindContentDir(relativeUnderContent[0]).FullName, .. relativeUnderContent[1..]]);
        Assert.True(File.Exists(path), $"内容文件缺失：{string.Join('/', relativeUnderContent)}");
        return File.ReadAllText(path);
    }

    /// <summary>Tower specs of one era keyed by id (towers.json, K-2 pipeline).</summary>
    public static IReadOnlyDictionary<string, TowerSpec> Towers(string eraDir) =>
        TowerSpec.LoadCollection(ReadText(eraDir, "towers.json")).ToDictionary(t => t.Id);

    /// <summary>Enemy specs of one era keyed by id (enemies.json; ids from the document, specs via the production parser).</summary>
    public static IReadOnlyDictionary<string, EnemySpec> Enemies(string eraDir)
    {
        string json = ReadText(eraDir, "enemies.json");
        var catalog = EnemySpecCatalog.Parse(json);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToDictionary(id => id, id => catalog.Get(id));
    }

    /// <summary>Hero catalog of one era (heroes.json; era1 carries the cross-era heritage row too).</summary>
    public static HeroSpecCatalog Heroes(string eraDir) => HeroSpecCatalog.Parse(ReadText(eraDir, "heroes.json"));

    /// <summary>Warehouse level of one era (era.json 仓库等级， v3.2 §5).</summary>
    public static int WarehouseLevel(string eraDir)
    {
        using var doc = JsonDocument.Parse(ReadText(eraDir, "era.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("warehouse_level").GetInt32();
    }

    /// <summary>Resource ids of one era row (era.json resources — includes the legacy-refresh rows).</summary>
    public static IReadOnlyList<string> EraResources(string eraDir)
    {
        using var doc = JsonDocument.Parse(ReadText(eraDir, "era.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("resources").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()!).ToList();
    }

    /// <summary>Whether a recipe id exists in one era's recipes.json (era2 carries the table).</summary>
    public static bool RecipeExists(string eraDir, string recipeId)
    {
        string path = Path.Combine(FindContentDir(eraDir).FullName, "recipes.json");
        if (!File.Exists(path))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Any(r => r.GetProperty("id").GetString() == recipeId);
    }

    /// <summary>
    /// Rebuilds the converter recipe payload JSON of one recipes.json row
    /// (inputs amount → items；output amount=1，v3.2 §5 一 craft 产 1 件) — the
    /// same shape the E2 fixture passes to the build-converter command.
    /// </summary>
    public static string ConverterRecipePayload(string eraDir, string recipeId)
    {
        using var doc = JsonDocument.Parse(ReadText(eraDir, "recipes.json"));
        JsonElement row = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == recipeId);
        var inputs = row.GetProperty("inputs").EnumerateArray()
            .Select(i => $"{{\"resource\":\"{i.GetProperty("resource").GetString()}\",\"items\":{i.GetProperty("amount").GetInt32()}}}");
        return "{\"inputs\":[" + string.Join(",", inputs) + "],\"output\":\""
            + row.GetProperty("output").GetProperty("resource").GetString()
            + "\",\"craft_ticks\":" + row.GetProperty("craft_ticks").GetInt32() + "}";
    }

    /// <summary>SP-8 objective pools (campaign/objectives.json): (strategy ids, challenge ids).</summary>
    public static (IReadOnlyList<string> Strategy, IReadOnlyList<string> Challenge) ObjectivePools()
    {
        using var doc = JsonDocument.Parse(ReadText("campaign", "objectives.json"));
        var strategy = doc.RootElement.GetProperty("strategy_pool").EnumerateArray()
            .Select(o => o.GetProperty("id").GetString()!).ToList();
        var challenge = doc.RootElement.GetProperty("challenge_pool").EnumerateArray()
            .Select(o => o.GetProperty("id").GetString()!).ToList();
        return (strategy, challenge);
    }

    /// <summary>Hard-flag lookup of the SP-8 challenge pool (SP-8b C1~C4 ★ hard rows).</summary>
    public static IReadOnlyDictionary<string, bool> ChallengeHardFlags()
    {
        using var doc = JsonDocument.Parse(ReadText("campaign", "objectives.json"));
        return doc.RootElement.GetProperty("challenge_pool").EnumerateArray()
            .ToDictionary(o => o.GetProperty("id").GetString()!, o => o.GetProperty("hard").GetBoolean());
    }

    /// <summary>
    /// The frozen T-079 E2 schedule rows (era2/levels.json) keyed by slot.
    /// JsonElements outlive the method: the document is NOT disposed here
    /// (small fixed file, GC-owned — the Era2ContentTests annotation
    /// convention; disposing would cut the returned elements off).
    /// </summary>
    public static IReadOnlyDictionary<int, JsonElement> Era2ScheduleRows()
    {
        var doc = JsonDocument.Parse(ReadText("era2", "levels.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(row => row.GetProperty("slot").GetInt32());
    }

    /// <summary>
    /// The frozen T-112 E3 schedule rows (era3/levels.json — the 排期侧 file;
    /// map blocks live in levels/e3.json per T-115 §四 G2) keyed by slot.
    /// Same non-disposing convention as <see cref="Era2ScheduleRows"/>.
    /// </summary>
    public static IReadOnlyDictionary<int, JsonElement> Era3ScheduleRows()
    {
        var doc = JsonDocument.Parse(ReadText("era3", "levels.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(row => row.GetProperty("slot").GetInt32());
    }

    /// <summary>
    /// The frozen T-123 E4 schedule rows (era4/levels.json — the 排期侧 file;
    /// map blocks live in levels/e4.json, T-124) keyed by slot. Same
    /// non-disposing convention as <see cref="Era3ScheduleRows"/>.
    /// </summary>
    public static IReadOnlyDictionary<int, JsonElement> Era4ScheduleRows()
    {
        var doc = JsonDocument.Parse(ReadText("era4", "levels.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(row => row.GetProperty("slot").GetInt32());
    }

    /// <summary>
    /// The frozen T-134 E5 schedule rows (era5/levels.json — the 排期侧 file;
    /// map blocks live in levels/e5.json, T-134) keyed by slot. Same
    /// non-disposing convention as <see cref="Era4ScheduleRows"/>.
    /// </summary>
    public static IReadOnlyDictionary<int, JsonElement> Era5ScheduleRows()
    {
        var doc = JsonDocument.Parse(ReadText("era5", "levels.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(row => row.GetProperty("slot").GetInt32());
    }

    /// <summary>
    /// The frozen T-135 终章 schedule rows (era6/levels.json — the 排期侧 file;
    /// map blocks live in levels/e6.json, T-135) keyed by slot. Same
    /// non-disposing convention as <see cref="Era5ScheduleRows"/>.
    /// </summary>
    public static IReadOnlyDictionary<int, JsonElement> Era6ScheduleRows()
    {
        var doc = JsonDocument.Parse(ReadText("era6", "levels.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(row => row.GetProperty("slot").GetInt32());
    }

    /// <summary>The era directory segment of a level row ("era1".."era6" from its era id).</summary>
    public static string EraDir(HandcraftedLevel level) => level.Era switch
    {
        string era when era.StartsWith("era6:", StringComparison.Ordinal) => "era6",
        string era when era.StartsWith("era5:", StringComparison.Ordinal) => "era5",
        string era when era.StartsWith("era4:", StringComparison.Ordinal) => "era4",
        string era when era.StartsWith("era3:", StringComparison.Ordinal) => "era3",
        string era when era.StartsWith("era2:", StringComparison.Ordinal) => "era2",
        _ => "era1",
    };

    /// <summary>
    /// The E3 overheat risk table of era3/era.json energy.risk, assembled into
    /// the production <see cref="EnergyRiskTuning"/> (T-113: E3+ 关卡把内容
    /// 阈值行挂进 SimulationConfig；数值全部出自内容表，CS-2.3).
    /// </summary>
    public static EnergyRiskTuning Era3EnergyRisk()
    {
        using var doc = JsonDocument.Parse(ReadText("era3", "era.json"));
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
    /// 内容阈值行挂进 SimulationConfig——过热沿用 E3 表值+熔毁通道 E4 起挂入；
    /// 数值全部出自内容表，CS-2.3).
    /// </summary>
    public static EnergyRiskTuning Era4EnergyRisk()
    {
        using var doc = JsonDocument.Parse(ReadText("era4", "era.json"));
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

    /// <summary>The E4 radiation-zone per-second decay of era4/era.json (T-123 [NEW] 行: 过热对敌 300 ÷ 污染 60s=5 点/s).</summary>
    public static double Era4RadiationZoneHpDecayPerSecond()
    {
        using var doc = JsonDocument.Parse(ReadText("era4", "era.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("energy").GetProperty("meltdown").GetProperty("consequences")
            .GetProperty("radiation_zone_hp_decay_per_s").GetDouble();
    }

    /// <summary>
    /// The E5 fusion risk tables of era5/era.json (overheat 沿用 + annihilation),
    /// assembled into the production <see cref="EnergyRiskTuning"/> (T-134: E5
    /// 关卡把内容阈值行挂进 SimulationConfig——过热沿用 E3 表值+湮灭通道 E5 起
    /// 挂入、熔毁通道=null（聚变代无核电，SP-4 行域：E4=熔毁/E5=湮灭）；数值
    /// 全部出自内容表，CS-2.3).
    /// </summary>
    public static EnergyRiskTuning Era5EnergyRisk()
    {
        using var doc = JsonDocument.Parse(ReadText("era5", "era.json"));
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
}
