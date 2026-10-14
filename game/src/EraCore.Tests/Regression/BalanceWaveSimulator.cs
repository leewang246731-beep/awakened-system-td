using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Units;
using FixedClock = EraCore.Tests.Units.FixedClock;
using SimRng = EraCore.Tests.Sim.SimRng;

namespace EraCore.Tests.Regression;

// T-083 (F-172) 无头批量平衡模拟器 v1（06 §1.4 回归级="金样 JSON+1000 波播种
// 模拟"）：波级确定性模型——敌情由生产 WaveSpawner.PlanWave/Poll 真排程
// （WaveFormula/Elite/Threat 全走 T-036 公式库，单位 HP 含威胁/Boss 乘子），
// 战斗为 30 Hz 确定性 tick 步进（无渲染、无实体场景），防御采购为四组验算
// 基准阵容的里程碑策略。经济/采购为 v1 启发式口径，逐常量注明出处：
// 内容锚点（F.5 供需/弹药公约、夹具初始授予）直引，无锚点处标 [NEW]。
// 全随机经注入 IRng（CS-1.4），同 seed+同参数 ⇒ 同摘要哈希（03 §1.2）。
//
// 路径注（如实）：T-080 无尽模块未落（EraCore 无 Endless/ 外推），按任务卡
// 回退口径=WaveFormula 直推非无尽 40 波+循环重开（1000 波=25 轮）；每轮=
// 新关卡（波号重置 1..40，阵容/库存按关卡初始授予重置——非无尽关卡无跨关
// 防御继承），_spawnCounter 与 IRng 流跨轮连续（混编组成随轮漂移——确定性
// 漂移，非随机）。

/// <summary>Seeded wave-level balance simulator (headless, deterministic).</summary>
internal sealed class BalanceWaveSimulator
{
    /// <summary>非无尽关卡波数（任务卡回退口径：40 波×循环重开）。</summary>
    public const int WavesPerCycle = 40;

    /// <summary>K-1 tick 域（30 Hz；与内容 reload_ticks 同域）。</summary>
    public const int TickRateHz = 30;

    /// <summary>初始授予（E2 夹具口径：SimE2Fixture.InitialStock 3000 木材）。</summary>
    public const double InitialStockPoints = 3000.0;

    /// <summary>民夫吞吐 0.3 件/s（F.5 供需基准"民夫 0.3 件/s"）。</summary>
    public const double PorterItemsPerSecond = 0.3;

    /// <summary>每 2 塔 1 民夫线（F.5 供需基准"2 塔:1 民夫:1 伐木场"）。</summary>
    public const int TowersPerPorterLine = 2;

    /// <summary>1 件=1 资源点换算（[NEW] v1 口径：F.5 未定件→点折算）。</summary>
    public const double PointsPerItem = 1.0;

    /// <summary>
    /// 每发弹药 0.2 点（F.5"1 件弹药=10 发"×E2 夹具配方 2 木材→1 件弹药：
    /// 2/10=0.2 点/发；[NEW] 口径=夹具配方入模拟）。
    /// </summary>
    public const double PointsPerShot = 0.2;

    /// <summary>防御阵容塔数上限（里程碑清单长度=11，封顶只作护栏）。</summary>
    public const int TowerCap = 12;

    private readonly long _seed;
    private readonly IReadOnlyList<TowerSpec> _towers;
    private readonly EnemySpecCatalog _enemies;
    private readonly RegressionContent.WaveGoldenParams _waveParams;
    private readonly WaveSpawner _spawner;
    private readonly Dictionary<string, TowerSpec> _towerById;

    public BalanceWaveSimulator(
        long seed,
        IReadOnlyList<TowerSpec> towers,
        EnemySpecCatalog enemies,
        RegressionContent.WaveGoldenParams waveParams)
    {
        _seed = seed;
        _towers = towers;
        _enemies = enemies;
        _waveParams = waveParams;
        _towerById = towers.ToDictionary(t => t.Id);

        // 波次参数头（wave-e2-golden.json 冻结校准集）单一事实源驱动生产排程器；
        // 常规 roster=enemies.json 全部非精英非 Boss 行（混编），精英线=攻城锤，
        // Boss 波=20（WaveSpawnConfig 记录缺省，E1 终关口径）。护盾 ShieldStartWave
        // 留 null——E2 无波盾（v3.2 §6 护盾 E3 起；wave 金样头的 31 为 30 行表外
        // 首 E3 波记录值）。
        _spawner = new WaveSpawner(
            enemies,
            new WaveSpawnConfig(
                BaseCount: waveParams.Base,
                BeginWave: waveParams.Begin,
                Spacing: waveParams.Spacing,
                Scaling: waveParams.Scaling,
                ThreatLevel: waveParams.Threat,
                RegularRoster: RegularRosterIds(),
                EliteLineEnabled: true,
                EliteEnemyId: "era2:enemy-ram",
                BossWave: 20,
                BossEnemyId: "era2:enemy-boss",
                ValleySeconds: 15.0,
                BatchesPerWave: 4,
                TickRateHz: TickRateHz,
                CellSizePx: 32,
                EntryCells: new[] { new CellCoord(10, 0), new CellCoord(6, 0), new CellCoord(14, 0) }),
            new SimRng(seed),
            new FixedClock());
    }

    /// <summary>常规 roster：era2 敌表剔除精英线物种与 Boss（声明序，确定性）。</summary>
    public static IReadOnlyList<string> RegularRosterIds() => new[]
    {
        "era2:enemy-skirmisher", "era2:enemy-warrior", "era2:enemy-raider",
        "era2:enemy-crossbowman", "era2:enemy-guard", "era2:enemy-firearrow",
        "era2:enemy-centurion",
    };

    /// <summary>波结算行（窗口聚合与摘要哈希的输入）。</summary>
    public sealed record WaveRow(
        int WaveIndex,
        int WaveNumber,
        int Enemies,
        int Kills,
        int Leaks,
        long ClearTicks,
        double Income,
        double Spend,
        double Stock,
        int Towers);

    /// <summary>每 sampleEvery 波的窗口摘要（入金样的采样点）。</summary>
    public sealed record WindowRow(
        int Wave,
        int Enemies,
        int Kills,
        int Leaks,
        double MedianKillS,
        double Income,
        double Spend,
        double Stock,
        int Towers,
        long WindowHash);

    /// <summary>全程摘要（首版基线数字+摘要哈希）。</summary>
    public sealed record RunSummary(double MedianKillS, double IncomeSpendRatio, long SummaryHash);

    /// <summary>守卫越带项（WARN 级留档：不阻断，仅报告）。</summary>
    public sealed record GuardWarning(string Metric, int Wave, double Value, double Min, double Max);

    /// <summary>整跑结果：窗口行+全程摘要+守卫告警（告警不入金样，仅留档输出）。</summary>
    public sealed record RunResult(
        IReadOnlyList<WindowRow> Windows,
        RunSummary Summary,
        IReadOnlyList<GuardWarning> Warnings);

    /// <summary>
    /// 跑 <paramref name="waves"/> 波（波号按 40 波循环重开），每
    /// <paramref name="sampleEvery"/> 波落一个窗口采样；守卫带为 WARN 级
    /// （越带只记 <see cref="GuardWarning"/>，不影响摘要）。
    /// </summary>
    public RunResult Run(int waves, int sampleEvery, double killMinS, double killMaxS, double ratioMin, double ratioMax)
    {
        long intervalTicks = _spawner.IntervalTicks; // 4680 @ t=0.75（120×1.3×30）
        double intervalSeconds = intervalTicks / (double)TickRateHz;

        var guns = new List<(int DamagePerShot, int ReloadTicks)>();
        var shopping = BuildShoppingPlan();
        int shopIndex = 0;
        double stock = InitialStockPoints;

        var waveRows = new List<WaveRow>(waves);
        var windows = new List<WindowRow>();
        var warnings = new List<GuardWarning>();
        var windowKillSeconds = new List<double>(sampleEvery);
        int windowEnemies = 0, windowKills = 0, windowLeaks = 0;
        double windowIncome = 0, windowSpend = 0;

        for (int waveIndex = 1; waveIndex <= waves; waveIndex++)
        {
            int waveNumber = ((waveIndex - 1) % WavesPerCycle) + 1; // 循环重开（T-080 未落回退口径）

            // 循环重开=新关卡（非无尽 40 波=一关）：阵容/采购进度/库存按新关
            // 口径重置（关卡初始授予，F.7 两级存档语义——防御不跨关继承）；
            // IRng 流与 roster 混编计数器跨轮连续（同 seed 全程确定）。
            if (waveNumber == 1)
            {
                guns.Clear();
                shopIndex = 0;
                stock = InitialStockPoints;
            }

            // 波初采购：里程碑按关内波号给定（四组验算口径——F.5 四组就是在
            // 给定波次直接给定基准阵容：组 1 波 5 前 2 箭楼/组 2 波 10 前 T2/
            // 组 3 波 20 前 3 弩炮/组 4 波 30 前 4 火油），不走库存门（v1 收入
            // 口径无研究分期与关卡奖励源，硬门会让弹药烧穿库存→阵容饿死→击杀
            // 时间崩带——首版标定教训，见金样 notes）；库存曲线仍全额记账
            // （收入−支出）。
            double spend = 0;
            while (shopIndex < shopping.Count
                && guns.Count < TowerCap
                && shopping[shopIndex].FromWave <= waveNumber)
            {
                spend += shopping[shopIndex].Price;
                guns.Add((shopping[shopIndex].DamagePerShot, shopping[shopIndex].ReloadTicks));
                shopIndex++;
            }

            // 敌情：生产排程器真排程（HP 含威胁/Boss 乘子；roster 混编循环；
            // 批次偏移入快照哈希，不入击杀钟——见下）
            WavePlan plan = _spawner.BeginWave(waveNumber, startTick: 0);
            IReadOnlyList<Unit> units = _spawner.Poll(plan.LastEntryTick);
            var entries = new List<(int EntryTick, int Hp, int Armor)>(units.Count);
            int unitCursor = 0;
            foreach (SpawnBatch batch in plan.Batches)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    Unit unit = units[unitCursor++];
                    entries.Add(((int)batch.TickOffset, unit.Hp, unit.Armor));
                }
            }

            // 战斗：30 Hz 确定性步进，集火最前存活敌（FocusFire 语义的 v1 简化：
            // 波级模型无位置域——全单位入场即交战（F.5 验算同口径：清场=EHP/
            // 有效 DPS，批次入场偏移只入快照不入击杀钟，[NEW] v1 波级简化），
            // 逐发按 DirectInt 对甲结算——与 F.5 验算同口径，溅射/燃烧不入账
            // （towers.json 口径：独立灼伤源不参与验算））。
            int count = entries.Count;
            var hp = new int[count];
            for (int i = 0; i < count; i++)
            {
                hp[i] = entries[i].Hp;
            }

            int kills = 0, shots = 0, front = 0; // front=最前存活下标（入场序=批次序，偏移升序）
            long clearTick = intervalTicks;
            for (int t = 0; t <= intervalTicks && kills < count; t++)
            {
                foreach ((int damagePerShot, int reloadTicks) in guns)
                {
                    if (kills >= count)
                    {
                        break;
                    }

                    if (t % reloadTicks != 0)
                    {
                        continue;
                    }

                    shots++;
                    hp[front] -= Damage.DirectInt(damagePerShot, entries[front].Armor);
                    if (hp[front] <= 0)
                    {
                        kills++;
                        front++;
                        clearTick = t;
                    }
                }
            }

            bool leaked = kills < count;

            // 经济：收入=民夫线吞吐×波间隔（2 塔:1 线）；支出=采购（里程碑全额
            // 记账）+弹药（0.2 点/发）；库存=收入−支出累计（可透支——v1 无研究
            // 分期/关卡奖励收入源，透支与通胀超带由守卫 WARN 留档）
            double income = Math.Round(
                PorterItemsPerSecond * PointsPerItem * Math.Ceiling(guns.Count / (double)TowersPerPorterLine)
                    * intervalSeconds,
                3);
            double ammoSpend = Math.Round(shots * PointsPerShot, 3);
            spend = Math.Round(spend + ammoSpend, 3);
            stock = Math.Round(stock + income - spend, 3);

            double clearSeconds = Math.Round(clearTick / (double)TickRateHz, 3);
            var row = new WaveRow(
                waveIndex, waveNumber, entries.Count, kills, leaked ? entries.Count - kills : 0,
                leaked ? intervalTicks : clearTick, income, spend, stock, guns.Count);
            waveRows.Add(row);

            // 窗口累计
            windowEnemies += row.Enemies;
            windowKills += row.Kills;
            windowLeaks += row.Leaks;
            windowIncome += row.Income;
            windowSpend += row.Spend;
            windowKillSeconds.Add(clearSeconds);

            if (waveIndex % sampleEvery == 0)
            {
                double median = Median(windowKillSeconds);
                windows.Add(new WindowRow(
                    waveIndex, windowEnemies, windowKills, windowLeaks,
                    Math.Round(median, 3),
                    Math.Round(windowIncome, 3), Math.Round(windowSpend, 3),
                    Math.Round(stock, 3), guns.Count,
                    WindowHash(waveRows, waveIndex - sampleEvery + 1, waveIndex)));

                // WARN 级守卫（首版启发式，[NEW] 阈值：越带只留档不阻断）
                if (median < killMinS || median > killMaxS)
                {
                    warnings.Add(new GuardWarning("median_kill_s", waveIndex, median, killMinS, killMaxS));
                }

                double windowRatio = windowSpend > 0 ? windowIncome / windowSpend : double.PositiveInfinity;
                if (windowRatio < ratioMin || windowRatio > ratioMax)
                {
                    warnings.Add(new GuardWarning("income_spend_ratio", waveIndex, Math.Round(windowRatio, 3), ratioMin, ratioMax));
                }

                windowEnemies = 0; windowKills = 0; windowLeaks = 0;
                windowIncome = 0; windowSpend = 0;
                windowKillSeconds.Clear();
            }
        }

        double globalMedian = Math.Round(Median(waveRows.Select(r => r.ClearTicks / (double)TickRateHz).ToList()), 3);
        double totalIncome = waveRows.Sum(r => r.Income);
        double totalSpend = waveRows.Sum(r => r.Spend);
        double ratio = Math.Round(totalSpend > 0 ? totalIncome / totalSpend : double.PositiveInfinity, 3);
        long summaryHash = SummaryHash(windows);

        return new RunResult(windows, new RunSummary(globalMedian, ratio, summaryHash), warnings);
    }

    /// <summary>种子（金样头留档）。</summary>
    public long Seed => _seed;

    /// <summary>波间隔秒（Campaign.WaveIntervalSeconds×1 经排程器换算 tick）。</summary>
    public double IntervalSeconds => _spawner.IntervalTicks / (double)TickRateHz;

    /// <summary>
    /// 采购里程碑（v1 策略，[NEW-原型校准]：F.5 四组验算基准阵容——组 1 波 5
    /// 前 2 箭楼、组 2 波 10 前 T2 上场、组 3 波 20 前 3 弩炮、组 4 波 30 前
    /// 4 火油；价格=towers.json cost_total，付不起顺延到首个付得起的波）。
    /// </summary>
    private List<(int FromWave, double Price, int DamagePerShot, int ReloadTicks)> BuildShoppingPlan()
    {
        TowerSpec Arrow() => _towerById["era2:tower-arrow"];
        TowerSpec Crossbow() => _towerById["era2:tower-crossbow"];
        TowerSpec Onager() => _towerById["era2:tower-onager"];
        TowerSpec FireOil() => _towerById["era2:tower-fire-oil"];

        var plan = new List<(int, double, int, int)>();
        for (int i = 0; i < 2; i++)
        {
            plan.Add((1, Arrow().CostTotal, (int)Arrow().DamagePerShot, Arrow().ReloadTicks!.Value)); // 组 1
        }

        for (int i = 0; i < 2; i++)
        {
            plan.Add((10, Crossbow().CostTotal, (int)Crossbow().DamagePerShot, Crossbow().ReloadTicks!.Value)); // 组 2
        }

        for (int i = 0; i < 3; i++)
        {
            plan.Add((20, Onager().CostTotal, (int)Onager().DamagePerShot, Onager().ReloadTicks!.Value)); // 组 3
        }

        for (int i = 0; i < 4; i++)
        {
            plan.Add((30, FireOil().CostTotal, (int)FireOil().DamagePerShot, FireOil().ReloadTicks!.Value)); // 组 4
        }

        return plan; // 11 座 ≤ TowerCap=12
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(static v => v).ToList();
        int n = sorted.Count;
        return n == 0
            ? double.NaN
            : n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        return hash * 1099511628211UL;
    }

    /// <summary>窗口哈希：FNV-1a over 本窗口逐波行（敌量/击杀/清场 tick/支出）。</summary>
    private static long WindowHash(IReadOnlyList<WaveRow> rows, int fromIndex, int toIndex)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = fromIndex - 1; i < toIndex; i++)
        {
            WaveRow r = rows[i];
            hash = Mix(hash, (ulong)r.WaveIndex);
            hash = Mix(hash, (ulong)r.WaveNumber);
            hash = Mix(hash, (ulong)r.Enemies);
            hash = Mix(hash, (ulong)r.Kills);
            hash = Mix(hash, (ulong)r.Leaks);
            hash = Mix(hash, (ulong)r.ClearTicks);
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(r.Income));
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(r.Spend));
        }

        return unchecked((long)hash);
    }

    /// <summary>摘要哈希：FNV-1a over 全部窗口行（采样点+窗口哈希）。</summary>
    private static long SummaryHash(IReadOnlyList<WindowRow> windows)
    {
        ulong hash = 14695981039346656037UL;
        foreach (WindowRow w in windows)
        {
            hash = Mix(hash, (ulong)w.Wave);
            hash = Mix(hash, (ulong)w.Enemies);
            hash = Mix(hash, (ulong)w.Kills);
            hash = Mix(hash, (ulong)w.Leaks);
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(w.MedianKillS));
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(w.Income));
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(w.Spend));
            hash = Mix(hash, (ulong)w.WindowHash);
        }

        return unchecked((long)hash);
    }
}
