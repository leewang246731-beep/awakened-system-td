using System.Text.Json;
using EraCore.Combat;
using EraCore.Procedural;
using EraCore.Units;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Procedural;

// T-140 测试基建：procedural.json 内容装载（RL9 仓库相对寻址）+ era 内容包
// 组装（宿主递交流：塔表/敌册/资源/仓库等级全出自 content 表）。

/// <summary>
/// Repo-relative loader of the procedural entry registry
/// (game/content/campaign/procedural.json) — strict JsonDocument mapping
/// into the production records, 装载即校验.
/// </summary>
internal static class ProceduralEntryContent
{
    private static string ReadRegistry()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "campaign", "procedural.json");
            if (File.Exists(probe))
            {
                return File.ReadAllText(probe);
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/campaign/procedural.json（测试须在仓库内运行）");
    }

    /// <summary>Parses and validates the registry; one instance per test session use.</summary>
    public static ProceduralCampaignEntry Load()
    {
        using var doc = JsonDocument.Parse(ReadRegistry());
        JsonElement root = doc.RootElement;
        JsonElement entry = root.GetProperty("entry");
        JsonElement constraints = root.GetProperty("constraints");

        var tiers = new List<ProceduralTier>();
        foreach (JsonElement tierEl in root.GetProperty("tiers").EnumerateArray())
        {
            JsonElement wave = tierEl.GetProperty("wave");
            JsonElement plan = tierEl.GetProperty("tower_plan");
            JsonElement economy = tierEl.GetProperty("economy");
            JsonElement boss = tierEl.GetProperty("boss_line");
            JsonElement objectives = tierEl.GetProperty("objectives");
            tiers.Add(new ProceduralTier(
                Id: tierEl.GetProperty("id").GetString()!,
                Slot: tierEl.GetProperty("slot").GetInt32(),
                AdjacentEnemyCamps: tierEl.GetProperty("adjacent_enemy_camps").GetInt32(),
                WaveBaseCount: wave.GetProperty("base_count").GetInt32(),
                WaveBeginWave: wave.GetProperty("begin_wave").GetInt32(),
                WaveSpacing: wave.GetProperty("spacing").GetInt32(),
                WaveScaling: wave.GetProperty("scaling").GetDouble(),
                BatchesPerWave: wave.GetProperty("batches_per_wave").GetInt32(),
                ValleySeconds: wave.GetProperty("valley_seconds").GetDouble(),
                ArrowTowerId: plan.GetProperty("arrow_tower_id").GetString()!,
                ArrowTowerCount: plan.GetProperty("arrow_tower_count").GetInt32(),
                RockTowerId: plan.GetProperty("rock_tower_id").GetString()!,
                RockTowerCount: plan.GetProperty("rock_tower_count").GetInt32(),
                WaveOneTowerCount: plan.GetProperty("wave_one_tower_count").GetInt32(),
                TowerWaveStep: plan.GetProperty("tower_wave_step").GetInt32(),
                BossLineEnabled: boss.GetProperty("enabled").GetBoolean(),
                BossEnemyId: boss.GetProperty("boss_enemy_id").GetString(),
                LaborCamps: economy.GetProperty("labor_camps").GetInt32(),
                Harvesters: economy.GetProperty("harvesters").GetInt32(),
                Converters: economy.GetProperty("converters").GetInt32(),
                HarvesterLanePorters: economy.GetProperty("harvester_lane_porters").GetInt32(),
                StockMultiplier: tierEl.GetProperty("stock_multiplier").GetDouble(),
                BudgetSeconds: tierEl.GetProperty("budget_seconds").GetInt32(),
                StrategyObjectives: objectives.GetProperty("strategy").EnumerateArray()
                    .Select(o => o.GetString()!).ToArray(),
                ChallengeObjectives: objectives.GetProperty("challenge").EnumerateArray()
                    .Select(o => o.GetString()!).ToArray(),
                NameZh: tierEl.GetProperty("name_zh").GetString()!,
                NameEn: tierEl.GetProperty("name_en").GetString()!));
        }

        double[] band = entry.GetProperty("duration_band_minutes").EnumerateArray()
            .Select(v => v.GetDouble()).ToArray();
        var parsed = new ProceduralCampaignEntry(
            Id: entry.GetProperty("id").GetString()!,
            NameZh: entry.GetProperty("name_zh").GetString()!,
            NameEn: entry.GetProperty("name_en").GetString()!,
            Eras: entry.GetProperty("eras").EnumerateArray().Select(e => e.GetString()!).ToArray(),
            DefaultTierId: entry.GetProperty("default_tier").GetString()!,
            DurationBandMinutesMin: (int)band[0],
            DurationBandMinutesMax: (int)band[1],
            Constraints: new ProceduralConstraints(
                constraints.GetProperty("width_min").GetInt32(),
                constraints.GetProperty("width_max").GetInt32(),
                constraints.GetProperty("height_min").GetInt32(),
                constraints.GetProperty("height_max").GetInt32(),
                constraints.GetProperty("forest_ratio_min").GetDouble(),
                constraints.GetProperty("forest_ratio_max").GetDouble(),
                constraints.GetProperty("rock_ratio_min").GetDouble(),
                constraints.GetProperty("rock_ratio_max").GetDouble(),
                constraints.GetProperty("water_ratio_min").GetDouble(),
                constraints.GetProperty("water_ratio_max").GetDouble(),
                constraints.GetProperty("entry_count_min").GetInt32(),
                constraints.GetProperty("entry_count_max").GetInt32(),
                constraints.GetProperty("grace_min").GetDouble(),
                constraints.GetProperty("grace_max").GetDouble(),
                constraints.GetProperty("max_rerolls").GetInt32()),
            Tiers: tiers);
        parsed.Validate();
        return parsed;
    }

    /// <summary>The era1 content pack handed to the generator (host-supplied content).</summary>
    public static ProceduralEraContent Era1Pack() => new(
        EraId: "era1:era-ancient",
        RegularRoster: new[] { "era1:enemy-beastling", "era1:enemy-hunter", "era1:enemy-tribesman" },
        Towers: TowerSpec.LoadCollection(ReadEra1Text("towers.json")),
        CurrencyResource: "era1:res-wood",
        WarehouseLevel: WarehouseLevel("era1"),
        CarrierTier: "porter");

    private static string ReadEra1Text(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "era1", file);
            if (File.Exists(probe))
            {
                return File.ReadAllText(probe);
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/era1/{file}（测试须在仓库内运行）");
    }

    /// <summary>Sanity helper for the pack assertions (roster rows exist in the catalog).</summary>
    public static EnemySpecCatalog Era1Enemies() => EnemySpecCatalog.Parse(ReadEra1Text("enemies.json"));
}
