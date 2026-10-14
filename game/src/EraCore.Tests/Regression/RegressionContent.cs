using System.Text.Json;
using EraCore.Combat;
using EraCore.Units;
using Xunit;
using SimEra2Content = EraCore.Tests.Sim.SimEra2Content;

namespace EraCore.Tests.Regression;

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
}
