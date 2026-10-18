using System.Text.Json;
using EraCore.Combat;
using EraCore.Units;
using Xunit;
using SimEra2Content = EraCore.Tests.Sim.SimEra2Content;

namespace EraCore.Tests.Regression;

// T-122 (F-172) E3 扩展装载器（只加不改——T-083 既有方法原样保持）：era3
// 塔/敌表走生产解析器（TowerSpec.LoadCollection / EnemySpecCatalog.Parse，
// 单一入口），era3/era.json 能源阶梯与 SP-4 风险块、era3/towers.json 弹药
// 公约、era3/recipes.json 配方行、levels/e3.json 关卡行（T-116 装载器
// HandcraftedLevelData 同源）以 JsonDocument 直读——数值永不回灌为输入
//（CS-4.4）。

// T-083 (F-172) 回归套件内容装载：仓库相对寻址（RL9，同 SimEra2Content
// 模式——从测试程序集向上找 game/content），era2 塔/敌表复用 EraCore.Tests
// 程序集内已有的生产解析器装载（TowerSpec.LoadCollection /
// EnemySpecCatalog.Parse，单一入口），era1 era.json 的能源阶梯块与
// campaign/techtree.json 的研究节点、wave-e2-golden.json 的冻结波次参数头
// 以 JsonDocument 直读（先例=Balance/WaveE2GoldenTableTests 直读金样）。

/// <summary>Repo-relative loaders for the T-083 numeric-regression content.</summary>
internal static class RegressionContent
{
    /// <summary>Frozen wave-parameter set header of wave-e2-golden.json (T-035 calibration).</summary>
    public sealed record WaveGoldenParams(
        double Threat,
        int Base,
        int Begin,
        int Spacing,
        double Scaling,
        int ShieldStartWave,
        double IntervalBaseSeconds);

    /// <summary>One techtree node row reduced to the fields the regression pins.</summary>
    public sealed record TechNodeRow(string Id, int CostSum, string Unlocks);

    /// <summary>Energy-ladder block of an era.json (levels + band bounds).</summary>
    public sealed record EnergyLadderRow(int Tier, double Output, double MinMultiplier, double MaxMultiplier);

    private static string FindContentDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content");
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content（测试须在仓库内运行）");
    }

    /// <summary>golden/ 子目录（回归金样与波次金样同居）。</summary>
    public static string GoldenDir() => Path.Combine(FindContentDir(), "golden");

    /// <summary>era2 塔表（生产解析器，T-065 内容行）。</summary>
    public static IReadOnlyList<TowerSpec> Era2Towers() => SimEra2Content.LoadTowerSpecs();

    /// <summary>era2 敌表（生产解析器，SP-3b 九行）。</summary>
    public static EnemySpecCatalog Era2Enemies() => SimEra2Content.LoadEnemyCatalog();
    /// <summary>era2 敌表行按 id 排列（确定性顺序，供 Theory 数据对齐）。</summary>
    public static IReadOnlyList<EnemySpec> Era2EnemyRows()
    {
        EnemySpecCatalog catalog = Era2Enemies();
        return Era2EnemyIds.Select(catalog.Get).ToList();
    }

    /// <summary>SP-3b 表行顺序（enemies.json items 声明序）。</summary>
    public static readonly IReadOnlyList<string> Era2EnemyIds = new[]
    {
        "era2:enemy-skirmisher", "era2:enemy-warrior", "era2:enemy-ram", "era2:enemy-raider",
        "era2:enemy-crossbowman", "era2:enemy-guard", "era2:enemy-firearrow", "era2:enemy-centurion",
        "era2:enemy-boss",
    };

    /// <summary>wave-e2-golden.json 头部的冻结波次参数（T-035 校准集单一事实源）。</summary>
    public static WaveGoldenParams WaveGoldenHeader()
    {
        string path = Path.Combine(GoldenDir(), "wave-e2-golden.json");
        Assert.True(File.Exists(path), "金样文件缺失：game/content/golden/wave-e2-golden.json");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;
        JsonElement p = root.GetProperty("params");
        return new WaveGoldenParams(
            Threat: root.GetProperty("threat").GetDouble(),
            Base: p.GetProperty("base").GetInt32(),
            Begin: p.GetProperty("begin").GetInt32(),
            Spacing: p.GetProperty("spacing").GetInt32(),
            Scaling: p.GetProperty("scaling").GetDouble(),
            ShieldStartWave: root.GetProperty("shield_start_wave").GetInt32(),
            IntervalBaseSeconds: root.GetProperty("interval_base_s").GetDouble());
    }

    /// <summary>campaign/techtree.json 全部节点（cost_sum=Σ成本，附录 F.5 研究公式变量 n）。</summary>
    public static IReadOnlyList<TechNodeRow> TechNodes()
    {
        string path = Path.Combine(FindContentDir(), "campaign", "techtree.json");
        Assert.True(File.Exists(path), "内容文件缺失：campaign/techtree.json");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var rows = new List<TechNodeRow>();
        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string unlocks = item.TryGetProperty("unlocks", out JsonElement u) && u.ValueKind == JsonValueKind.Array
                ? u.EnumerateArray().Select(static e => e.GetString()!).First()
                : string.Empty;
            rows.Add(new TechNodeRow(
                Id: item.GetProperty("id").GetString()!,
                CostSum: item.GetProperty("cost_sum").GetInt32(),
                Unlocks: unlocks));
        }

        return rows;
    }

    /// <summary>era1/era.json 的能源阶梯块（levels[].output + ladder_multiplier_min/max）。</summary>
    public static IReadOnlyList<EnergyLadderRow> Era1EnergyLadder()
    {
        string path = Path.Combine(FindContentDir(), "era1", "era.json");
        Assert.True(File.Exists(path), "内容文件缺失：era1/era.json");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement energy = doc.RootElement.GetProperty("items")[0].GetProperty("energy");
        double min = energy.GetProperty("ladder_multiplier_min").GetDouble();
        double max = energy.GetProperty("ladder_multiplier_max").GetDouble();

        var rows = new List<EnergyLadderRow>();
        foreach (JsonElement level in energy.GetProperty("levels").EnumerateArray())
        {
            rows.Add(new EnergyLadderRow(
                Tier: level.GetProperty("tier").GetInt32(),
                Output: level.GetProperty("output").GetDouble(),
                MinMultiplier: min,
                MaxMultiplier: max));
        }

        return rows;
    }

    // ------------------------------------------------ T-122 E3 扩展（只加不改）

    /// <summary>SP-4 风险参数行（era3/era.json energy.risk 点路径 → 冻结值）。</summary>
    public sealed record RiskParamRow(string JsonPath, double Value);

    /// <summary>era3 配方行（弹药点值/钢链速率的派生源，T-112 内容表）。</summary>
    public sealed record RecipeRow(string Id, string Kind, string OutputResource, double InputsPoints, int CraftTicks);

    private static string ReadEra3(string fileName)
    {
        string path = Path.Combine(FindContentDir(), "era3", fileName);
        Assert.True(File.Exists(path), $"内容文件缺失：era3/{fileName}");
        return File.ReadAllText(path);
    }

    /// <summary>era3 塔表（生产解析器，T-112 三行：加特林/臼炮/铁甲墙）。</summary>
    public static IReadOnlyList<TowerSpec> Era3Towers() => TowerSpec.LoadCollection(ReadEra3("towers.json"));

    /// <summary>era3 敌表（生产解析器，T-112 九行，含 §4 逐字锚点 Boss 行）。</summary>
    public static EnemySpecCatalog Era3Enemies() => EnemySpecCatalog.Parse(ReadEra3("enemies.json"));

    /// <summary>E3 敌表行按 id 排列（确定性顺序，enemies.json items 声明序）。</summary>
    public static IReadOnlyList<EnemySpec> Era3EnemyRows() =>
        Era3EnemyIds.Select(Era3Enemies().Get).ToList();

    /// <summary>E3 敌表声明序（对位 SP-3b 九行制的 T-112 新设计行）。</summary>
    public static readonly IReadOnlyList<string> Era3EnemyIds = new[]
    {
        "era3:enemy-conscript", "era3:enemy-line-infantry", "era3:enemy-ram",
        "era3:enemy-scout-rider", "era3:enemy-gunner", "era3:enemy-ironclad-guard",
        "era3:enemy-sapper", "era3:enemy-overseer", "era3:enemy-boss",
    };

    /// <summary>era3/era.json 的能源阶梯块（L1=300/L2=1200 + ×3~5 带界）。</summary>
    public static IReadOnlyList<EnergyLadderRow> Era3EnergyLadder()
    {
        using JsonDocument doc = JsonDocument.Parse(ReadEra3("era.json"));
        JsonElement energy = doc.RootElement.GetProperty("items")[0].GetProperty("energy");
        double min = energy.GetProperty("ladder_multiplier_min").GetDouble();
        double max = energy.GetProperty("ladder_multiplier_max").GetDouble();

        var rows = new List<EnergyLadderRow>();
        foreach (JsonElement level in energy.GetProperty("levels").EnumerateArray())
        {
            rows.Add(new EnergyLadderRow(
                Tier: level.GetProperty("tier").GetInt32(),
                Output: level.GetProperty("output").GetDouble(),
                MinMultiplier: min,
                MaxMultiplier: max));
        }

        return rows;
    }

    /// <summary>SP-4 L146『E3 过热爆炸』十四参数（触发 3+热量动力学 5+半径 1+后果 5）。</summary>
    public static IReadOnlyList<RiskParamRow> Era3RiskParams()
    {
        using JsonDocument doc = JsonDocument.Parse(ReadEra3("era.json"));
        JsonElement risk = doc.RootElement.GetProperty("items")[0]
            .GetProperty("energy").GetProperty("risk");

        var rows = new List<RiskParamRow>();
        foreach (string path in new[]
                 {
                     "trigger.overload_threshold_percent", "trigger.sustained_s", "trigger.heat_capacity",
                     "heat_dynamics.gain_per_s_overloaded", "heat_dynamics.overload_step_percent",
                     "heat_dynamics.bonus_gain_per_s_per_extra_10pct_overload",
                     "heat_dynamics.decay_per_s_unloaded",
                     "heat_dynamics.min_seconds_to_full_heat_at_sustained_overload",
                     "explosion_radius_cells", "consequences.building_damage_fraction_of_max_hp",
                     "consequences.enemy_damage", "consequences.culprit_outage_s",
                     "consequences.culprit_degraded_output_fraction",
                     "consequences.culprit_degraded_duration_s",
                 })
        {
            JsonElement node = risk;
            foreach (string segment in path.Split('.')[..^1])
            {
                node = node.GetProperty(segment);
            }

            rows.Add(new RiskParamRow(path, node.GetProperty(path.Split('.')[^1]).GetDouble()));
        }

        return rows;
    }

    /// <summary>era3 towers.json 弹药公约：1 件弹药=10 发（v3.2 §5 L78）。</summary>
    public static int Era3ShotsPerAmmoItem()
    {
        using JsonDocument doc = JsonDocument.Parse(ReadEra3("towers.json"));
        return doc.RootElement.GetProperty("ammo_convention").GetProperty("shots_per_ammo_item").GetInt32();
    }

    /// <summary>era3/recipes.json 配方行（篮子点值=Σ输入 amount，1 件=1 点 [NEW] v1 换算）。</summary>
    public static IReadOnlyList<RecipeRow> Era3Recipes()
    {
        using JsonDocument doc = JsonDocument.Parse(ReadEra3("recipes.json"));
        var rows = new List<RecipeRow>();
        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            double inputs = item.GetProperty("inputs").EnumerateArray()
                .Sum(input => input.GetProperty("amount").GetDouble());
            rows.Add(new RecipeRow(
                Id: item.GetProperty("id").GetString()!,
                Kind: item.GetProperty("kind").GetString()!,
                OutputResource: item.GetProperty("output").GetProperty("resource").GetString()!,
                InputsPoints: inputs,
                CraftTicks: item.GetProperty("craft_ticks").GetInt32()));
        }

        return rows;
    }

    /// <summary>levels/e3.json 四关行（T-116 装载器同源：波表+塔线+初始授予）。</summary>
    public static IReadOnlyList<EraCore.CampaignLoop.HandcraftedLevel> Era3Levels() =>
        Levels.HandcraftedLevelData.Load("e3.json").Items;
}
