using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Regression;

// [F-172] T-122 无头数值回归 E3 扩展——E3 长程快照回归（1000 波播种模拟）：
// Era3WaveSimulator（E3 关卡内容驱动的波级无头平衡模拟器：levels/e3.json
// 波表+塔线、era3 塔/敌/配方内容表、波次盾 E3 起）确定性跑 1000 波（40 波×
// 25 轮，每轮按声明序轮换一关），每 50 波统计摘要（敌量/击杀/泄漏/资源曲线
// 采样点）+窗口哈希入 game/content/golden/regression-e3-v1.json（首版新文件
// ——T-083 regression-v1.json 原样保持零漂移）；重算 vs 落盘任一采样漂移 →
// exit 1（CS-6 流程：再生成唯一入口=环境开关，禁手改）。守卫（波均击杀时间
// 带 [5,120]s/通胀率带 [0.8,1.5]）WARN 级——越带不阻断，逐条留档到测试输出
// 与 CI 日志（T-083 验收 08 第 3 条同口径；E3 弹链瓶颈 0.2 件/s 对通胀带的
// 压力观察=T-122 REV 留档项）。
public class Era3RegressionTests
{
    private const int Waves = 1_000;
    private const int SampleEvery = 50;
    private const long Seed = 20261021; // T-122 seed family（E3 扩展日历）
    private const double KillMinS = 5.0;   // T-083 同带（[NEW] 首版阈值沿用——改带须过任务卡依据）
    private const double KillMaxS = 120.0; // 同上（波间隔带内的清场带）
    private const double RatioMin = 0.8;   // T-083 同带：波均收入/支出比下界
    private const double RatioMax = 1.5;   // 同上上界
    private const double RuntimeBudgetSeconds = 30.0; // T-083 验收 08 第 4 条同预算：<30s（E3 含盾再生节拍）

    public Era3RegressionTests(ITestOutputHelper output) => Output = output;

    private readonly ITestOutputHelper Output;

    // 1000 波全程只真算一次供金样/守卫/预算共用（xUnit 同类串行，静态缓存=
    // 确定性结果共享）；确定性回放测试自费第二次真算做对照。
    private static readonly Lazy<(Era3WaveSimulator.RunResult Result, double Seconds)> CachedRun = new(() =>
    {
        var watch = Stopwatch.StartNew();
        Era3WaveSimulator.RunResult result = FreshRun();
        watch.Stop();
        return (result, watch.Elapsed.TotalSeconds);
    });

    private static Era3WaveSimulator NewSimulator() => new(
        Seed,
        RegressionContent.Era3Levels(),
        RegressionContent.Era3Towers(),
        RegressionContent.Era3Enemies(),
        RegressionContent.Era3Recipes(),
        RegressionContent.Era3ShotsPerAmmoItem(),
        RegressionContent.WaveGoldenHeader().ShieldStartWave); // 31=wave 金样冻结头单一事实源

    private static Era3WaveSimulator.RunResult FreshRun() =>
        NewSimulator().Run(Waves, SampleEvery, KillMinS, KillMaxS, RatioMin, RatioMax);

    [Fact] // legal — 主断言：1000 波 E3 快照回归（漂移=红，exit 1）
    public void Golden_window_summary_regression_e3()
    {
        string path = RegressionE3Golden.GoldenPath();
        if (!File.Exists(path))
        {
            if (Environment.GetEnvironmentVariable(RegressionE3Golden.RegenEnvironmentVariable) == "1")
            {
                WriteGolden(path);
                return;
            }

            Assert.Fail($"金样缺失：{path}（首次生成：{RegressionE3Golden.RegenEnvironmentVariable}=1 dotnet test --filter Era3RegressionTests）");
        }

        RegressionE3GoldenFile golden = RegressionE3Golden.Deserialize(File.ReadAllText(path));

        if (Environment.GetEnvironmentVariable(RegressionE3Golden.RegenEnvironmentVariable) == "1")
        {
            WriteGolden(path);
            return;
        }

        // 头部不变量：被篡改的头=金样纪律违规（CS-6.1），直接红
        Assert.Equal(RegressionE3Golden.Schema, golden.Schema);
        Assert.Equal("T-122", golden.Task);
        Assert.Equal(Waves, golden.Waves);
        Assert.Equal(SampleEvery, golden.SampleEvery);
        Assert.Equal(Era3WaveSimulator.WavesPerCycle, golden.WaveCycle);
        Assert.Equal(RegressionContent.Era3Levels().Count, golden.LevelRotation); // 4 关一轮换
        Assert.Equal(Seed, golden.Seed);

        // 守卫带钉住 T-083 同款原文带（[NEW] 阈值——改带须过任务卡依据）
        Assert.Equal(KillMinS, golden.Guards.KillMinS, precision: 9);
        Assert.Equal(KillMaxS, golden.Guards.KillMaxS, precision: 9);
        Assert.Equal(RatioMin, golden.Guards.IncomeSpendMin, precision: 9);
        Assert.Equal(RatioMax, golden.Guards.IncomeSpendMax, precision: 9);
        Assert.Equal("WARN", golden.Guards.Level); // 越带不阻断——留档级

        // 循环语义注在档（40 波×循环重开+E3 关卡轮换——T-080 无尽外推切换前口径）
        Assert.Contains("关卡轮换", golden.PathNote);

        // 关卡参数快照 ↔ 活内容互锁（单一事实源 levels/e3.json+era3 内容表，
        // 内容表漂移即红——先排查内容变更依据，再走 CS-6 再生成）
        AssertLevelsInterlockWithLiveContent(golden);

        // 引擎重算 vs 落盘：任一窗口漂移 → 首分叉窗口报告（exit 1）
        Era3WaveSimulator.RunResult recomputed = RunSimulator();
        Assert.Equal(golden.Windows.Count, recomputed.Windows.Count);
        for (int i = 0; i < recomputed.Windows.Count; i++)
        {
            RegressionWindowRow expected = golden.Windows[i];
            Era3WaveSimulator.WindowRow actual = recomputed.Windows[i];
            bool equal = expected.Wave == actual.Wave
                && expected.Enemies == actual.Enemies
                && expected.Kills == actual.Kills
                && expected.Leaks == actual.Leaks
                && expected.MedianKillS == actual.MedianKillS
                && expected.Income == actual.Income
                && expected.Spend == actual.Spend
                && expected.Stock == actual.Stock
                && expected.Towers == actual.Towers
                && expected.WindowHash == actual.WindowHash;
            Assert.True(equal, $"E3 回归金样漂移：窗口 #{i}（wave {expected.Wave}）落盘 "
                + $"[{expected.Enemies}/{expected.Kills}/{expected.MedianKillS}/{expected.WindowHash}]，"
                + $"重算 [{actual.Enemies}/{actual.Kills}/{actual.MedianKillS}/{actual.WindowHash}]——"
                + "先排查 E3 内容/公式/口径变更依据，再走 CS-6 再生成流程（独立 commit+依据编号）");
        }

        // 全程摘要：首跑基线数字+摘要哈希（击杀时间中位/通胀率）
        Assert.Equal(golden.Summary.MedianKillS, recomputed.Summary.MedianKillS, precision: 9);
        Assert.Equal(golden.Summary.IncomeSpendRatio, recomputed.Summary.IncomeSpendRatio, precision: 9);
        Assert.Equal(golden.Summary.SummaryHash, recomputed.Summary.SummaryHash);
        Assert.Equal(Waves / SampleEvery, golden.Windows.Count); // 20 窗
        Assert.Equal(Waves, golden.Windows[^1].Wave);

        ReportWarnings(recomputed);
    }

    [Fact] // legal — 确定性回放：同 seed 重跑摘要哈希相等（03 §1.2 回放根）
    public void Same_seed_replays_identical_summary_hash()
    {
        Era3WaveSimulator.RunResult first = FreshRun();
        Era3WaveSimulator.RunResult second = FreshRun();

        Assert.Equal(first.Summary.SummaryHash, second.Summary.SummaryHash);
        Assert.Equal(first.Windows.Count, second.Windows.Count);
        Assert.Equal(first.Warnings.Count, second.Warnings.Count);
    }

    [Fact] // legal — 运行预算：1000 波确定性 tick 步进（含盾再生节拍，无渲染）<30s
    public void Thousand_wave_run_stays_inside_ci_budget()
    {
        (Era3WaveSimulator.RunResult result, double seconds) = CachedRun.Value;

        Output.WriteLine($"E3 1000 波模拟耗时 {seconds:F2}s（预算 {RuntimeBudgetSeconds:F0}s）");
        Assert.True(
            seconds < RuntimeBudgetSeconds,
            $"E3 1000 波模拟超预算：{seconds:F2}s ≥ {RuntimeBudgetSeconds:F0}s"
            + "（降波数方案须在金样 notes 注明并过任务卡依据）");

        // 预算测试也顺带守结构：20 窗全落，敌量/击杀守恒（泄漏不计击杀）
        Assert.Equal(Waves / SampleEvery, result.Windows.Count);
        foreach (Era3WaveSimulator.WindowRow window in result.Windows)
        {
            Assert.True(window.Enemies >= window.Kills, "击杀数不可能超过敌数");
            Assert.True(window.Enemies > 0, "窗口敌量为零——排程器未产敌（模拟断链）");
        }
    }

    [Fact] // legal — WARN 级守卫留档：越带不阻断，逐条输出（E3 弹链瓶颈对通胀带
           // 的压力观察=T-122 REV 留档项：T-116 发现钢链 craft 150t=0.2 件/s 为
           // E3 弹链经济瓶颈——首跑数字见输出与 REV）
    public void Balance_guards_report_out_of_band_windows_without_blocking()
    {
        Era3WaveSimulator.RunResult result = RunSimulator();

        // 守卫真跑过的结构证据：每窗指标有限（NaN/∞=守卫失效，红）
        foreach (Era3WaveSimulator.WindowRow window in result.Windows)
        {
            Assert.True(double.IsFinite(window.MedianKillS), $"窗口 {window.Wave} 击杀时间中位非有限值");
        }

        // WARN 语义：越带仅留档（不断言越带数上界——越带即红与"不阻断"矛盾；
        // 首跑越带数与基线数字见测试输出与 REV-20261016-T122）
        ReportWarnings(result);
    }

    private Era3WaveSimulator.RunResult RunSimulator() => CachedRun.Value.Result;

    private void ReportWarnings(Era3WaveSimulator.RunResult result)
    {
        Output.WriteLine($"E3 基线：击杀时间中位 {result.Summary.MedianKillS:F3}s，"
            + $"通胀率（收入/支出）{result.Summary.IncomeSpendRatio:F3}，"
            + $"摘要哈希 {result.Summary.SummaryHash}");
        if (result.Warnings.Count == 0)
        {
            Output.WriteLine("守卫：20 窗全部落带（零 WARN）");
            return;
        }

        Output.WriteLine($"守卫 WARN {result.Warnings.Count} 条（不阻断，留档）：");
        foreach (Era3WaveSimulator.GuardWarning warning in result.Warnings)
        {
            Output.WriteLine(
                $"  [WARN] {warning.Metric} @wave {warning.Wave}: {warning.Value:F3} 超带 [{warning.Min:F2}, {warning.Max:F2}]");
        }
    }

    /// <summary>金样关卡快照 ↔ 活内容逐字段互锁（levels/e3.json+towers.json 漂移即红）。</summary>
    private static void AssertLevelsInterlockWithLiveContent(RegressionE3GoldenFile golden)
    {
        var levels = RegressionContent.Era3Levels();
        var towerById = RegressionContent.Era3Towers().ToDictionary(t => t.Id);
        Assert.Equal(levels.Count, golden.Levels.Count);

        foreach (var (row, level) in golden.Levels.Zip(levels))
        {
            Assert.Equal(level.Id, row.LevelId);
            Assert.Equal(level.Threat, row.Threat, precision: 9);
            Assert.Equal(level.WinWaves, row.WinWaves);
            Assert.Equal(level.Waves.BaseCount, row.BaseCount);
            Assert.Equal(level.Waves.BeginWave, row.BeginWave);
            Assert.Equal(level.Waves.Spacing, row.Spacing);
            Assert.Equal(level.Waves.Scaling, row.Scaling, precision: 9);
            Assert.Equal(level.Waves.BatchesPerWave, row.BatchesPerWave);
            Assert.Equal(level.Waves.ValleySeconds, row.ValleySeconds, precision: 9);
            Assert.Equal(level.Waves.RegularRoster, row.RegularRoster);
            Assert.Equal(level.Waves.EliteLineEnabled, row.EliteLineEnabled);
            Assert.Equal(level.Waves.EliteEnemyId, row.EliteEnemyId);
            Assert.Equal(level.InitialStock[0].Amount, row.InitialStockPoints, precision: 9);

            var expectedLine = level.CommandScript
                .Where(s => s.Verb == "build-tower")
                .Select(s => (s.Wave, s.Tower!, towerById[s.Tower!].CostTotal))
                .ToList();
            Assert.Equal(expectedLine.Count, row.TowerLine.Count);
            foreach (var ((fromWave, towerId, price), milestone) in expectedLine.Zip(row.TowerLine))
            {
                Assert.Equal(fromWave, milestone.FromWave);
                Assert.Equal(towerId, milestone.TowerId);
                Assert.Equal(price, milestone.Price, precision: 9);
            }
        }
    }

    private static void WriteGolden(string path)
    {
        Era3WaveSimulator simulator = NewSimulator();
        Era3WaveSimulator.RunResult result = simulator.Run(Waves, SampleEvery, KillMinS, KillMaxS, RatioMin, RatioMax);

        var towerById = RegressionContent.Era3Towers().ToDictionary(t => t.Id);
        var levels = RegressionContent.Era3Levels()
            .Select(level => new RegressionE3LevelRow(
                LevelId: level.Id,
                Threat: level.Threat,
                WinWaves: level.WinWaves,
                BaseCount: level.Waves.BaseCount,
                BeginWave: level.Waves.BeginWave,
                Spacing: level.Waves.Spacing,
                Scaling: level.Waves.Scaling,
                BatchesPerWave: level.Waves.BatchesPerWave,
                ValleySeconds: level.Waves.ValleySeconds,
                RegularRoster: level.Waves.RegularRoster,
                EliteLineEnabled: level.Waves.EliteLineEnabled,
                EliteEnemyId: level.Waves.EliteEnemyId,
                InitialStockPoints: level.InitialStock[0].Amount,
                TowerLine: level.CommandScript
                    .Where(s => s.Verb == "build-tower")
                    .Select(s => new RegressionE3TowerMilestone(s.Wave, s.Tower!, towerById[s.Tower!].CostTotal))
                    .ToList()))
            .ToList();

        var golden = new RegressionE3GoldenFile(
            Schema: RegressionE3Golden.Schema,
            Task: "T-122",
            Seed: Seed,
            Waves: Waves,
            SampleEvery: SampleEvery,
            WaveCycle: Era3WaveSimulator.WavesPerCycle,
            LevelRotation: levels.Count,
            PathNote: "T-080 无尽外推未接（T-083 回退口径同形）：40 波×循环重开（25 轮=1000 波，每轮=新关卡）"
                + "+E3 关卡轮换（4 关/轮换：轮 k 取 levels/e3.json 声明序第 (k−1) mod 4 关——波表/roster/威胁/"
                + "初始授予/塔线全部按该关内容行；波号/阵容/库存按关重置，IRng 流跨轮连续，roster 混编计数随"
                + "新排程器按关重置=确定性重置）；波 31 起波次盾 10+8×波 生效（ShieldStartWave=wave-e2-golden.json"
                + " 冻结头 31）；T-099 nightly 切 Endless 外推时同 T-083 口径一并切换",
            RegenCommand: "ERA_REGRESSION_E3_GOLDEN_UPDATE=1 dotnet test --filter FullyQualifiedName~EraCore.Tests.Regression",
            CheckCommand: "bash tools/ci/run_all.sh",
            Notes: "1000 波 E3 关卡内容驱动无头批量平衡模拟（波级 30Hz 确定性步进，无渲染）：敌情=生产 WaveSpawner"
                + " 排程（波表=levels/e3.json 各关 waves 块，roster=各关 regular_roster+精英线，威胁 HP 乘子全走"
                + " T-036 公式库；波次盾+个体盾取最大在 CreateUnit 落位=T-114 裁定），战斗=生产 Unit.ApplyDamage"
                + "+TickShield 真算（盾先吸减后入甲、破盾 5s 后 20/s 再生、穿甲打断 3s——加特林=穿刺类；溅射面伤"
                + "不入账，臼炮按单体有效伤害计=v1 波级简化，T-083 towers.json 口径同款），防御=各关 command_script"
                + " build-tower 塔线里程碑采购（波锚+cost_total，不走库存门——T-083 首版标定教训同款），经济=钢链"
                + "供给线 0.2 件/s×每 2 塔 1 线（中间件配方 craft 150t 派生=30/150，T-116 弹链瓶颈口径）+弹药点值"
                + "（配方篮子点值÷10 发/件：弹链 (1钢+1铜)/10=0.2 点/发、臼炮弹 (2钢+1煤)/10=0.3 点/发）+每轮初始"
                + "授予=该关 initial_stock 钢点。守卫（击杀时间带 [5,120]s/通胀带 [0.8,1.5]）WARN 级不入金样；"
                + "E3 弹链瓶颈 0.2 件/s 对通胀带的压力观察=REV-20261016-T122 留档。重生成前须核对 08-task-plan"
                + " T-122 验收（CS-6.1/CS-6.3）",
            Levels: levels,
            Guards: new RegressionGuards(KillMinS, KillMaxS, RatioMin, RatioMax, Level: "WARN"),
            Windows: result.Windows.Select(w => new RegressionWindowRow(
                w.Wave, w.Enemies, w.Kills, w.Leaks, w.MedianKillS, w.Income, w.Spend, w.Stock, w.Towers, w.WindowHash))
                .ToList(),
            Summary: new RegressionSummaryRow(
                result.Summary.MedianKillS, result.Summary.IncomeSpendRatio, result.Summary.SummaryHash));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, RegressionE3Golden.Serialize(golden) + Environment.NewLine);

        // 回读自检：落盘文件必须能被模拟器逐窗口复现（生成即验证）
        RegressionE3GoldenFile reread = RegressionE3Golden.Deserialize(File.ReadAllText(path));
        Era3WaveSimulator.RunResult verify = NewSimulator()
            .Run(Waves, SampleEvery, KillMinS, KillMaxS, RatioMin, RatioMax);
        Assert.Equal(reread.Windows.Count, verify.Windows.Count);
        for (int i = 0; i < verify.Windows.Count; i++)
        {
            Assert.Equal(reread.Windows[i].WindowHash, verify.Windows[i].WindowHash);
        }

        Assert.Equal(reread.Summary.SummaryHash, verify.Summary.SummaryHash);
    }
}
