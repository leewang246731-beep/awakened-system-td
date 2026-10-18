using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Units;
using FixedClock = EraCore.Tests.Units.FixedClock;
using SimRng = EraCore.Tests.Sim.SimRng;

namespace EraCore.Tests.Regression;

// T-122 (F-172) E3 无头批量平衡模拟器（T-083 BalanceWaveSimulator 同族，只加
// 不改——era2 模拟器与 regression-v1.json 原样保持）：E3 关卡内容驱动 40 波×N
// 轮循环模拟——敌情=生产 WaveSpawner 真排程（波表=levels/e3.json 各关 waves
// 块逐旋钮、roster=各关 regular_roster+精英线、威胁=各关 threat 行），战斗=
// 生产 Unit.ApplyDamage/SetShield/TickShield 全真算（护盾先吸减后入甲、破盾
// 5s 后 20/s 再生、穿甲打断 3s——E3 波 31 起波次盾 10+8×波 与个体盾取最大，
// WaveSpawnConfig.ShieldStartWave=wave-e2-golden.json 冻结头 31），防御=
// 各关 command_script build-tower 塔线里程碑采购，经济=钢链供给（craft_ticks
// 派生 0.2 件/s×线，T-116 弹链瓶颈口径）+弹药点值（配方篮子点值÷10 发/件，
// content 派生：弹链 0.2 点/发、臼炮弹 0.3 点/发）。全随机经注入 IRng（CS-1.4），
// 同 seed+同内容 ⇒ 同摘要哈希（03 §1.2）。
//
// 循环语义（如实）：40 波/轮×25 轮=1000 波（T-083 回退口径同形）；每轮按
// levels/e3.json 声明序轮换一关（4 关一轮换：轮 1=L1 …轮 4=L4、轮 5=L1 …），
// 轮首=新关卡（波号重置 1..40、阵容/库存按该关初始授予重置、新排程器以该关
// 波表构造）；IRng 流跨轮连续（同一 SimRng 实例贯穿全部排程器），roster 混编
// 计数随新排程器按关重置——确定性重置，非随机。波号 31..40 超 E3 关卡胜波窗
// （13~19）为外推段（WaveFormula 直推，护盾公式照常生效——波次盾公式的
// 长程快照覆盖面）。

/// <summary>E3 content-driven seeded wave-level balance simulator (headless, deterministic).</summary>
internal sealed class Era3WaveSimulator
{
    /// <summary>每轮波数（T-083 循环重开口径同形：40 波×循环）。</summary>
    public const int WavesPerCycle = 40;

    /// <summary>K-1 tick 域（30 Hz；与内容 reload_ticks/craft_ticks 同域）。</summary>
    public const int TickRateHz = 30;

    /// <summary>1 件=1 资源点换算（[NEW] v1 口径，T-083 同款：文档未定件→点折算）。</summary>
    public const double PointsPerItem = 1.0;

    /// <summary>每 2 塔 1 供给线（F.5 供需"2 塔:1 线"比例形状沿用——[NEW] E3 v1 口径）。</summary>
    public const int TowersPerSupplyLine = 2;

    /// <summary>防御阵容塔数上限（塔线脚本 ≤4，封顶只作护栏——T-083 同款）。</summary>
    public const int TowerCap = 12;

    private readonly long _seed;
    private readonly IReadOnlyList<EraCore.CampaignLoop.HandcraftedLevel> _levels;
    private readonly IReadOnlyList<TowerSpec> _towers;
    private readonly EnemySpecCatalog _enemies;
    private readonly IReadOnlyList<RegressionContent.RecipeRow> _recipes;
    private readonly int _shotsPerAmmoItem;
    private readonly int _shieldStartWave;
    private readonly IReadOnlyDictionary<string, TowerSpec> _towerById;

    public Era3WaveSimulator(
        long seed,
        IReadOnlyList<EraCore.CampaignLoop.HandcraftedLevel> levels,
        IReadOnlyList<TowerSpec> towers,
        EnemySpecCatalog enemies,
        IReadOnlyList<RegressionContent.RecipeRow> recipes,
        int shotsPerAmmoItem,
        int? shieldStartWave)
    {
        _seed = seed;
        _levels = levels;
        _towers = towers;
        _enemies = enemies;
        _recipes = recipes;
        _shotsPerAmmoItem = shotsPerAmmoItem;
        _shieldStartWave = shieldStartWave ?? throw new ArgumentNullException(nameof(shieldStartWave));
        _towerById = towers.ToDictionary(t => t.Id);
    }

    /// <summary>种子（金样头留档）。</summary>
    public long Seed => _seed;

    /// <summary>钢链供给速率（件/s）：中间件配方 craft_ticks 真算派生——30/150=0.2（T-116 四钢链口径）。</summary>
    public double SteelItemsPerSecond =>
        TickRateHz / (double)_recipes.Single(r => r.Kind == "intermediate").CraftTicks;

    /// <summary>某塔弹药点值/发：弹药件配方篮子点值 ÷ 弹药公约发数/件（弹链 2/10=0.2、臼炮弹 3/10=0.3）。</summary>
    public double PointsPerShotOf(TowerSpec tower)
    {
        RegressionContent.RecipeRow recipe = _recipes.Single(r => r.OutputResource == tower.AmmoItem);
        return recipe.InputsPoints / _shotsPerAmmoItem * PointsPerItem;
    }

    /// <summary>波结算行（窗口聚合与摘要哈希的输入）。</summary>
    public sealed record WaveRow(
        int WaveIndex,
        int WaveNumber,
        string LevelId,
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

    /// <summary>全程摘要（首跑基线数字+摘要哈希）。</summary>
    public sealed record RunSummary(double MedianKillS, double IncomeSpendRatio, long SummaryHash);

    /// <summary>守卫越带项（WARN 级留档：不阻断，仅报告——T-083 验收 08 第 3 条同口径）。</summary>
    public sealed record GuardWarning(string Metric, int Wave, double Value, double Min, double Max);

    /// <summary>整跑结果：窗口行+全程摘要+守卫告警（告警不入金样，仅留档输出）。</summary>
    public sealed record RunResult(
        IReadOnlyList<WindowRow> Windows,
        RunSummary Summary,
        IReadOnlyList<GuardWarning> Warnings);

    /// <summary>炮位（塔线里程碑采购后的作战单元：生产 Unit.ApplyDamage 的射击参数）。</summary>
    private sealed record Gun(int DamagePerShot, int ReloadTicks, bool Pierce, double PointsPerShot);

    /// <summary>
    /// 跑 <paramref name="waves"/> 波（40 波/轮×关轮换），每 <paramref name="sampleEvery"/>
    /// 波落一个窗口采样；守卫带为 WARN 级（越带只记 <see cref="GuardWarning"/>）。
    /// </summary>
    public RunResult Run(int waves, int sampleEvery, double killMinS, double killMaxS, double ratioMin, double ratioMax)
    {
        var rng = new SimRng(_seed); // 单流贯穿全部轮次排程器（跨轮连续，同 seed 全程确定）
        var clock = new FixedClock();

        var waveRows = new List<WaveRow>(waves);
        var windows = new List<WindowRow>();
        var warnings = new List<GuardWarning>();
        var windowKillSeconds = new List<double>(sampleEvery);
        int windowEnemies = 0, windowKills = 0, windowLeaks = 0;
        double windowIncome = 0, windowSpend = 0;

        WaveSpawner spawner = null!;
        EraCore.CampaignLoop.HandcraftedLevel level = null!;
        List<(int FromWave, string TowerId, double Price)> shopping = new();
        var guns = new List<Gun>();
        int shopIndex = 0;
        double stock = 0;
        long intervalTicks = 0;

        for (int waveIndex = 1; waveIndex <= waves; waveIndex++)
        {
            int waveNumber = ((waveIndex - 1) % WavesPerCycle) + 1; // 40 波循环重开（T-083 同形）
            int cycleIndex = (waveIndex - 1) / WavesPerCycle;

            if (waveNumber == 1)
            {
                // 轮首=新关卡（声明序轮换）：该关波表构造新排程器（IRng 流连续）、
                // 塔线/采购进度/库存按该关初始授予重置（非无尽 40 波=一关口径）
                level = _levels[cycleIndex % _levels.Count];
                spawner = new WaveSpawner(_enemies, LevelSpawnConfig(level), rng, clock);
                intervalTicks = spawner.IntervalTicks;
                shopping = BuildShoppingPlan(level);
                guns.Clear();
                shopIndex = 0;
                stock = LevelStockPoints(level);
            }

            // 波初采购：塔线里程碑按关内波号给定（command_script build-tower 行：
            // 波锚=步骤 wave，价格=towers.json cost_total），不走库存门（T-083
            // 首版标定教训同款——v1 收入口径无研究分期/关卡奖励源，硬门会饿死
            // 塔线崩带）；库存曲线仍全额记账（收入−支出）
            double spend = 0;
            while (shopIndex < shopping.Count
                && guns.Count < TowerCap
                && shopping[shopIndex].FromWave <= waveNumber)
            {
                spend += shopping[shopIndex].Price;
                guns.Add(BuildGun(shopping[shopIndex].TowerId));
                shopIndex++;
            }

            // 敌情：生产排程器真排程（威胁 HP 乘子/精英线/波次盾+个体盾取最大
            // 全在 CreateUnit 落位——T-114 裁定①②③）；Poll 至末批入场=全量
            WavePlan plan = spawner.BeginWave(waveNumber, startTick: 0);
            IReadOnlyList<Unit> units = spawner.Poll(plan.LastEntryTick);
            int count = units.Count;
            var hasShield = new bool[count];
            for (int i = 0; i < count; i++)
            {
                hasShield[i] = units[i].Shield > 0; // 入场带盾者才走再生节拍（无盾行 TickShield 恒空转）
            }

            // 战斗：30 Hz 确定性步进，集火最前存活敌（T-083 同口径：波级模型无
            // 位置域，全入场即交战，批次偏移不入击杀钟）；逐发走生产
            // Unit.ApplyDamage——盾先吸减（无视甲）、余量入 Damage.DirectInt
            // 对甲结算、穿甲（加特林=SP-1a 穿刺类）打断盾再生 3s；每 tick 先
            // 步进存活者盾再生（ShieldState：破盾 5s 后 20/s，整数节拍）后开火
            //（顺序冻结，重放位稳定）。溅射面伤不入账（T-083 towers.json 口径
            // 同款：臼炮按单体有效伤害计——v1 波级简化）
            int kills = 0, shots = 0;
            double ammoPoints = 0;
            int front = 0;
            long clearTick = intervalTicks;
            for (int t = 0; t <= intervalTicks && kills < count; t++)
            {
                for (int i = front; i < count; i++)
                {
                    if (hasShield[i] && units[i].Alive)
                    {
                        units[i].TickShield(t);
                    }
                }

                foreach (Gun gun in guns)
                {
                    if (kills >= count)
                    {
                        break;
                    }

                    if (t % gun.ReloadTicks != 0)
                    {
                        continue;
                    }

                    shots++;
                    ammoPoints += gun.PointsPerShot;
                    units[front].ApplyDamage(gun.DamagePerShot, t, gun.Pierce);
                    if (!units[front].Alive)
                    {
                        kills++;
                        front++;
                        clearTick = t;
                    }
                }
            }

            bool leaked = kills < count;

            // 经济：收入=钢链供给线吞吐×波间隔（每 2 塔 1 线×0.2 件/s——craft_ticks
            // 派生，T-116 弹链瓶颈口径）；支出=塔线采购+弹药点值（弹链 0.2/发、
            // 臼炮弹 0.3/发——配方篮子派生）；库存=初始授予+收入−支出累计（可
            // 透支——透支与越带由守卫 WARN 留档，T-083 同口径）
            double intervalSeconds = intervalTicks / (double)TickRateHz;
            double income = Math.Round(
                Math.Ceiling(guns.Count / (double)TowersPerSupplyLine)
                    * SteelItemsPerSecond * PointsPerItem * intervalSeconds,
                3);
            double ammoSpend = Math.Round(ammoPoints, 3);
            spend = Math.Round(spend + ammoSpend, 3);
            stock = Math.Round(stock + income - spend, 3);

            double clearSeconds = Math.Round(clearTick / (double)TickRateHz, 3);
            waveRows.Add(new WaveRow(
                waveIndex, waveNumber, level.Id, count, kills,
                leaked ? count - kills : 0,
                leaked ? intervalTicks : clearTick, income, spend, stock, guns.Count));

            // 窗口累计
            windowEnemies += count;
            windowKills += kills;
            windowLeaks += leaked ? count - kills : 0;
            windowIncome += income;
            windowSpend += spend;
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

                // WARN 级守卫（T-083 首版启发式阈值同带：越带只留档不阻断）
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

    /// <summary>某关的生产排程配置（波表=levels/e3.json waves 块逐旋钮；入口=该关地图行）。</summary>
    private WaveSpawnConfig LevelSpawnConfig(EraCore.CampaignLoop.HandcraftedLevel level)
    {
        var entries = level.Map.EntryCells.Select(c => new CellCoord(c.X, c.Y)).ToArray();
        return new WaveSpawnConfig(
            BaseCount: level.Waves.BaseCount,
            BeginWave: level.Waves.BeginWave,
            Spacing: level.Waves.Spacing,
            Scaling: level.Waves.Scaling,
            ThreatLevel: level.Threat,
            RegularRoster: level.Waves.RegularRoster,
            EliteLineEnabled: level.Waves.EliteLineEnabled,
            EliteEnemyId: level.Waves.EliteEnemyId,
            BossWave: level.Waves.BossWave ?? 20, // E3 四关无终局 Boss 行（BossEnemyId=null 禁用——占位缺省不产 Boss）
            BossEnemyId: level.Waves.BossEnemyId,
            FirstWaveGraceMultiplier: level.FirstWaveGraceMultiplier,
            ValleySeconds: level.Waves.ValleySeconds,
            BatchesPerWave: level.Waves.BatchesPerWave,
            TickRateHz: TickRateHz,
            CellSizePx: 32,
            EntryCells: entries,
            ShieldStartWave: _shieldStartWave); // 波次盾 E3 起（31=wave 金样冻结头单一事实源）
    }

    /// <summary>
    /// 塔线里程碑（command_script build-tower 行：波锚+塔 id；价格=towers.json
    /// cost_total）。铁甲墙（支援类无武器循环）不在 E3 命令流（T-116 诚实披露：
    /// build-tower 通道拒绝无武器循环塔行）——出现即抛（内容演进时的显式红）。
    /// </summary>
    private List<(int FromWave, string TowerId, double Price)> BuildShoppingPlan(EraCore.CampaignLoop.HandcraftedLevel level)
    {
        var plan = new List<(int, string, double)>();
        foreach (EraCore.CampaignLoop.ScriptStep step in level.CommandScript.Where(s => s.Verb == "build-tower"))
        {
            TowerSpec spec = _towerById[step.Tower!];
            if (spec.ReloadTicks is null || spec.Dps <= 0)
            {
                throw new InvalidOperationException(
                    $"塔线含无武器循环塔行 {spec.Id}（支援类不入波级战斗模型——需扩模型后入线）");
            }

            plan.Add((step.Wave, spec.Id, spec.CostTotal));
        }

        return plan;
    }

    private Gun BuildGun(string towerId)
    {
        TowerSpec spec = _towerById[towerId];
        return new Gun(
            DamagePerShot: (int)spec.DamagePerShot,
            ReloadTicks: spec.ReloadTicks!.Value,
            Pierce: spec.Class == TowerClass.Pierce, // SP-1a L28 穿刺类=穿甲（打断盾再生）
            PointsPerShot: PointsPerShotOf(spec));
    }

    /// <summary>关初始授予点值（levels/e3.json initial_stock 首行——E3 四关均为钢单货币行）。</summary>
    private static double LevelStockPoints(EraCore.CampaignLoop.HandcraftedLevel level)
    {
        if (level.InitialStock.Count != 1)
        {
            throw new InvalidOperationException(
                $"{level.Id}: initial_stock 须恰一行（E3 钢单货币口径，得多行须扩模拟器经济模型）");
        }

        return level.InitialStock[0].Amount;
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

    /// <summary>窗口哈希：FNV-1a over 本窗口逐波行（关 id 按字符逐位入混——
    /// string.GetHashCode 带进程随机种子，禁入回放哈希；关轮换序号入哈希——
    /// 关卡切换可辨，03 §1.2 回放根=跨进程位稳定）。</summary>
    private static long WindowHash(IReadOnlyList<WaveRow> rows, int fromIndex, int toIndex)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = fromIndex - 1; i < toIndex; i++)
        {
            WaveRow r = rows[i];
            hash = Mix(hash, (ulong)r.WaveIndex);
            hash = Mix(hash, (ulong)r.WaveNumber);
            foreach (char c in r.LevelId)
            {
                hash = Mix(hash, c);
            }

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
