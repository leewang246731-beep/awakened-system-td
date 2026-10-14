using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Regression;

// [F-172] T-083 无头数值回归首版——长程快照回归（1000 波播种模拟）：
// BalanceWaveSimulator（波级无头批量平衡模拟器）确定性跑 1000 波，每 50 波
// 统计摘要（敌量/击杀/泄漏/资源曲线采样点）+窗口哈希入
// game/content/golden/regression-v1.json；重算 vs 落盘任一采样漂移 → exit 1
// （CS-6 流程：再生成唯一入口=环境开关，禁手改）。守卫（波均击杀时间带
// [5,120]s/通胀率带 [0.8,1.5]）为 WARN 级——越带不阻断，逐条留档到测试输出
// 与 CI 日志（验收 08 第 3 条口径；阈值 [NEW] 首版启发式）。
//
// 路径注（如实）：T-080 无尽模块未落——按任务卡回退口径用 WaveFormula 直推
// 非无尽 40 波+循环重开（25 轮=1000 波）；T-099 落 nightly 时切换 Endless 外推。
// [F-172] T-083 长程快照回归测试类（验收 08：全参数化断言+快照对比入 CI）
public class EndlessRegressionTests
{
    private const int Waves = 1_000;
    private const int SampleEvery = 50;
    private const long Seed = 20261020; // T-083 seed family（回归首版日历）
    private const double KillMinS = 5.0;   // [NEW] 首版阈值（验收 08 第 3 条：超带=平衡崩坏信号）
    private const double KillMaxS = 120.0; // [NEW] 同上（波间隔 156s@t=0.75 内的清场带）
    private const double RatioMin = 0.8;   // [NEW] 波均收入/支出比下界
    private const double RatioMax = 1.5;   // [NEW] 同上上界
    private const double RuntimeBudgetSeconds = 30.0; // 验收 08 第 4 条：<30s（超时降 500 波须注明——本版实测留档）

    public EndlessRegressionTests(ITestOutputHelper output) => Output = output;

    private readonly ITestOutputHelper Output;

    // 1000 波全程只真算一次供金样/守卫/预算共用（xUnit 同类串行，静态缓存=
    // 确定性结果共享）；确定性回放测试自费第二次真算做对照。
    private static readonly Lazy<(BalanceWaveSimulator.RunResult Result, double Seconds)> CachedRun = new(() =>
    {
        var watch = Stopwatch.StartNew();
        BalanceWaveSimulator.RunResult result = FreshRun();
        watch.Stop();
        return (result, watch.Elapsed.TotalSeconds);
    });

    private static BalanceWaveSimulator.RunResult FreshRun() =>
        new BalanceWaveSimulator(
            Seed,
            RegressionContent.Era2Towers(),
            RegressionContent.Era2Enemies(),
            RegressionContent.WaveGoldenHeader())
            .Run(Waves, SampleEvery, KillMinS, KillMaxS, RatioMin, RatioMax);

    [Fact] // legal — 主断言：1000 波快照回归（漂移=红，exit 1）
    public void Golden_window_summary_regression()
    {
        string path = RegressionGolden.GoldenPath();
        if (!File.Exists(path))
        {
            if (Environment.GetEnvironmentVariable(RegressionGolden.RegenEnvironmentVariable) == "1")
            {
                WriteGolden(path);
                return;
            }

            Assert.Fail($"金样缺失：{path}（首次生成：{RegressionGolden.RegenEnvironmentVariable}=1 dotnet test --filter EndlessRegressionTests）");
        }

        RegressionGoldenFile golden = RegressionGolden.Deserialize(File.ReadAllText(path));

        if (Environment.GetEnvironmentVariable(RegressionGolden.RegenEnvironmentVariable) == "1")
        {
            WriteGolden(path);
            return;
        }

        // 头部不变量：被篡改的头=金样纪律违规（CS-6.1），直接红
        Assert.Equal(RegressionGolden.Schema, golden.Schema);
        Assert.Equal("T-083", golden.Task);
        Assert.Equal(Waves, golden.Waves);
        Assert.Equal(SampleEvery, golden.SampleEvery);
        Assert.Equal(BalanceWaveSimulator.WavesPerCycle, golden.WaveCycle);
        Assert.Equal(Seed, golden.Seed);

        // 守卫带钉住验收 08 第 3 条原文（[NEW] 阈值——改带须过任务卡依据）
        Assert.Equal(KillMinS, golden.Guards.KillMinS, precision: 9);
        Assert.Equal(KillMaxS, golden.Guards.KillMaxS, precision: 9);
        Assert.Equal(RatioMin, golden.Guards.IncomeSpendMin, precision: 9);
        Assert.Equal(RatioMax, golden.Guards.IncomeSpendMax, precision: 9);
        Assert.Equal("WARN", golden.Guards.Level); // 越带不阻断——留档级

        // 回退路径注在档（T-080 未落：40 波+循环重开；T-099 切换时须改此注）
        Assert.Contains("循环重开", golden.PathNote);

        // 波次参数与 wave-e2-golden.json 冻结头互锁（单一事实源，漂移即红）
        RegressionContent.WaveGoldenParams wave = RegressionContent.WaveGoldenHeader();
        Assert.Equal(wave.Threat, golden.Params.Threat, precision: 9);
        Assert.Equal(wave.Base, golden.Params.Base);
        Assert.Equal(wave.Begin, golden.Params.Begin);
        Assert.Equal(wave.Spacing, golden.Params.Spacing);
        Assert.Equal(wave.Scaling, golden.Params.Scaling, precision: 9);

        // 引擎重算 vs 落盘：任一窗口漂移 → 首分叉窗口报告（exit 1）
        BalanceWaveSimulator.RunResult recomputed = RunSimulator();
        Assert.Equal(golden.Windows.Count, recomputed.Windows.Count);
        for (int i = 0; i < recomputed.Windows.Count; i++)
        {
            RegressionWindowRow expected = golden.Windows[i];
            BalanceWaveSimulator.WindowRow actual = recomputed.Windows[i];
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
            Assert.True(equal, $"回归金样漂移：窗口 #{i}（wave {expected.Wave}）落盘 "
                + $"[{expected.Enemies}/{expected.Kills}/{expected.MedianKillS}/{expected.WindowHash}]，"
                + $"重算 [{actual.Enemies}/{actual.Kills}/{actual.MedianKillS}/{actual.WindowHash}]——"
                + "先排查内容/公式/口径变更依据，再走 CS-6 再生成流程（独立 commit+依据编号）");
        }

        // 全程摘要：首版基线数字+摘要哈希（击杀时间中位/通胀率）
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
        BalanceWaveSimulator.RunResult first = FreshRun();
        BalanceWaveSimulator.RunResult second = FreshRun();

        Assert.Equal(first.Summary.SummaryHash, second.Summary.SummaryHash);
        Assert.Equal(first.Windows.Count, second.Windows.Count);
        Assert.Equal(first.Warnings.Count, second.Warnings.Count);
    }

    [Fact] // legal — 运行预算：1000 波确定性 tick 步进（无渲染）<30s（验收 08 第 4 条）
    public void Thousand_wave_run_stays_inside_ci_budget()
    {
        (BalanceWaveSimulator.RunResult result, double seconds) = CachedRun.Value;

        Output.WriteLine($"1000 波模拟耗时 {seconds:F2}s（预算 {RuntimeBudgetSeconds:F0}s）");
        Assert.True(
            seconds < RuntimeBudgetSeconds,
            $"1000 波模拟超预算：{seconds:F2}s ≥ {RuntimeBudgetSeconds:F0}s"
            + "（降 500 波方案须在金样 notes 注明并过任务卡依据）");

        // 预算测试也顺带守结构：20 窗全落，敌量/击杀守恒（泄漏不计击杀）
        Assert.Equal(Waves / SampleEvery, result.Windows.Count);
        foreach (BalanceWaveSimulator.WindowRow window in result.Windows)
        {
            Assert.True(window.Enemies >= window.Kills, "击杀数不可能超过敌数");
            Assert.True(window.Enemies > 0, "窗口敌量为零——排程器未产敌（模拟断链）");
        }
    }

    [Fact] // legal — WARN 级守卫留档：越带不阻断，逐条输出（报告留档=测试输出+CI 日志）
    public void Balance_guards_report_out_of_band_windows_without_blocking()
    {
        BalanceWaveSimulator.RunResult result = RunSimulator();

        // 守卫真跑过的结构证据：每窗指标有限（NaN/∞=守卫失效，红）
        foreach (BalanceWaveSimulator.WindowRow window in result.Windows)
        {
            Assert.True(double.IsFinite(window.MedianKillS), $"窗口 {window.Wave} 击杀时间中位非有限值");
        }

        // WARN 语义：越带仅留档（本断言对越带数不做上界约束——若越带即红则与
        // 验收 08 第 3 条"不阻断"矛盾；基线越带数见金样重算输出与任务报告）
        ReportWarnings(result);
    }

    private BalanceWaveSimulator.RunResult RunSimulator() => CachedRun.Value.Result;

    private void ReportWarnings(BalanceWaveSimulator.RunResult result)
    {
        Output.WriteLine($"基线：击杀时间中位 {result.Summary.MedianKillS:F3}s，"
            + $"通胀率（收入/支出）{result.Summary.IncomeSpendRatio:F3}，"
            + $"摘要哈希 {result.Summary.SummaryHash}");
        if (result.Warnings.Count == 0)
        {
            Output.WriteLine("守卫：20 窗全部落带（零 WARN）");
            return;
        }

        Output.WriteLine($"守卫 WARN {result.Warnings.Count} 条（不阻断，留档）：");
        foreach (BalanceWaveSimulator.GuardWarning warning in result.Warnings)
        {
            Output.WriteLine(
                $"  [WARN] {warning.Metric} @wave {warning.Wave}: {warning.Value:F3} 超带 [{warning.Min:F2}, {warning.Max:F2}]");
        }
    }

    private static void WriteGolden(string path)
    {
        var simulator = new BalanceWaveSimulator(
            Seed,
            RegressionContent.Era2Towers(),
            RegressionContent.Era2Enemies(),
            RegressionContent.WaveGoldenHeader());
        BalanceWaveSimulator.RunResult result = simulator.Run(Waves, SampleEvery, KillMinS, KillMaxS, RatioMin, RatioMax);
        RegressionContent.WaveGoldenParams wave = RegressionContent.WaveGoldenHeader();

        var golden = new RegressionGoldenFile(
            Schema: RegressionGolden.Schema,
            Task: "T-083",
            Seed: Seed,
            Waves: Waves,
            SampleEvery: SampleEvery,
            WaveCycle: BalanceWaveSimulator.WavesPerCycle,
            PathNote: "T-080 无尽模块未落——WaveFormula 直推非无尽 40 波+循环重开（25 轮=1000 波，每轮=新关卡："
                + "波号/阵容/库存按关重置，IRng 流与 roster 混编计数跨轮连续）；T-099 nightly 切 Endless 外推",
            RegenCommand: "ERA_REGRESSION_GOLDEN_UPDATE=1 dotnet test --filter FullyQualifiedName~EraCore.Tests.Regression",
            CheckCommand: "bash tools/ci/run_all.sh",
            Notes: "1000 波播种无头批量平衡模拟（波级 30Hz 确定性步进，无渲染）：敌情=生产 WaveSpawner 排程"
                + "（WaveFormula/Elite/Threat 全走 T-036 公式库，roster=enemies.json 七常规行混编循环，批次入场"
                + "偏移入快照哈希不入击杀钟——波级全入场即交战，F.5 验算同口径），防御=四组验算基准阵容里程碑"
                + "采购（组1 波5 前 2 箭楼/组2 波10 前 2 弩塔/组3 波20 前 3 弩炮/组4 波30 前 4 火油；里程碑不走"
                + "库存门——v1 收入口径无研究分期/关卡奖励源，硬门会饿死阵容崩带），经济=民夫线 0.3 件/s×2塔:1 线"
                + "（F.5 供需）+弹药 0.2 点/发（1 件=10 发×2 木材/件夹具配方）+每关初始授予 3000（E2 夹具口径）；"
                + "1 件=1 资源点为 [NEW] v1 换算。守卫（击杀时间带 [5,120]s/通胀带 [0.8,1.5]）WARN 级不入金样；"
                + "首版基线：击杀中位 40s 落带，通胀率 ~0.31-0.53 全窗超下带（里程碑塔支出占主导——研究分期/奖励"
                + "收入源随经济任务落位后复标）。重生成前须核对 08-task-plan T-083 验收（CS-6.1/CS-6.3）",
            Params: new RegressionWaveParams(
                Threat: wave.Threat,
                Base: wave.Base,
                Begin: wave.Begin,
                Spacing: wave.Spacing,
                Scaling: wave.Scaling,
                BossWave: 20,
                EliteEnemyId: "era2:enemy-ram",
                BossEnemyId: "era2:enemy-boss",
                BatchesPerWave: 4,
                TickRateHz: BalanceWaveSimulator.TickRateHz,
                InitialStockPoints: BalanceWaveSimulator.InitialStockPoints,
                TowerCap: BalanceWaveSimulator.TowerCap),
            Guards: new RegressionGuards(KillMinS, KillMaxS, RatioMin, RatioMax, Level: "WARN"),
            Windows: result.Windows.Select(w => new RegressionWindowRow(
                w.Wave, w.Enemies, w.Kills, w.Leaks, w.MedianKillS, w.Income, w.Spend, w.Stock, w.Towers, w.WindowHash))
                .ToList(),
            Summary: new RegressionSummaryRow(
                result.Summary.MedianKillS, result.Summary.IncomeSpendRatio, result.Summary.SummaryHash));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, RegressionGolden.Serialize(golden) + Environment.NewLine);

        // 回读自检：落盘文件必须能被模拟器逐窗口复现（生成即验证）
        RegressionGoldenFile reread = RegressionGolden.Deserialize(File.ReadAllText(path));
        BalanceWaveSimulator.RunResult verify = new BalanceWaveSimulator(
            Seed, RegressionContent.Era2Towers(), RegressionContent.Era2Enemies(), RegressionContent.WaveGoldenHeader())
            .Run(Waves, SampleEvery, KillMinS, KillMaxS, RatioMin, RatioMax);
        Assert.Equal(reread.Windows.Count, verify.Windows.Count);
        for (int i = 0; i < verify.Windows.Count; i++)
        {
            Assert.Equal(reread.Windows[i].WindowHash, verify.Windows[i].WindowHash);
        }

        Assert.Equal(reread.Summary.SummaryHash, verify.Summary.SummaryHash);
    }
}
