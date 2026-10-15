using EraCore.Balance;
using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.Units;
using EraCore.World;
using Xunit;

namespace EraCore.Tests.Units;

// 堡垒号可战（T-114 集成验收）：E3 塔（T-112 三塔数值，era3/towers.json
// 数据驱动 CS-4.4）+ 堡垒号[盾 600（max 裁定压过波次盾 330）→ 破盾 5 s 窗
// → 本体 9000×1.5（v3.2 §6 Boss 状态）/甲 16]——120 s 窗口内战斗必有结果；
// 穿甲弹 vs 无穿甲两案手算对照（无穿甲的盾再生压制 DPS=「必须穿甲」设计
// 断言）；战吼/践踏技能在 Boss 战生效（T-066 参数经 spawner 接本模拟）；
// 确定性双跑（同载入重放全等，含状态哈希）。
// [C-10] [F-032] [F-033]
public class FortressBattleTests
{
    private const int CellSize = 32;
    private const int MapWidth = 12;
    private const int MapHeight = 34;   // 进攻走廊长 ~32 格：堡垒号 0.53 px/tick 全程 ~66 s
    private const int PathCol = 6;
    private const long WindowTicks = 3600; // 完整 120 s 模拟窗口（30 Hz；任务卡 60~120 s 上沿）
    private const int E3ShieldStartWave = 31; // E2 金样表 shield_start_wave 记录值=首个 E3 波
    private const int BossWave = 40;          // E3 Boss 波（era2 终关 30 的下一时代步）

    /// <summary>践踏调参（[NEW-原型校准] 测试侧配置；Boss 状态 ×1.3 → 130/45 ticks）。</summary>
    private static readonly TrampleProfile FortressTrample = new(Damage: 100, PeriodTicks: 45);

    private sealed record BattleOutcome(
        bool Killed,
        bool ArrivedAtBase,
        long EndTick,
        int ShotsFired,
        int TowersDestroyed,
        int TowerCount,
        int WallsDestroyed,
        int WallCount,
        int WallTrampleHits,
        int TotalDamage,
        int BossHpLost,
        int BossShieldAbsorbed,
        int EscortPoolsDrained,
        int WarCryGridPulses,
        int WarCryBuffTicks,
        int MaxRamCryBonus,
        long StateHash);

    private sealed record CrowdOutcome(long AllDeadTick, int ShieldAbsorbed, int DamageDealt);

    private sealed record Loadout(
        IReadOnlyList<(int Col, int Row, UnitsContent.TowerStats Stats)> Towers,
        IReadOnlyList<(int Col, int Row)> Walls,
        bool PierceRounds,
        int GuardEscorts,
        int RamEscorts);

    /// <summary>堡垒号入场：spawner 第 40 波 Boss 批次（波次盾 31 起+个体盾 max 裁定+战吼/践踏 spec 接线全走内容数据）。</summary>
    private static (WaveSpawner Spawner, EnemySpecCatalog Catalog) NewSpawner()
    {
        EnemySpecCatalog catalog = UnitsContent.LoadEra3EnemyCatalog();
        var spawner = new WaveSpawner(
            catalog,
            new WaveSpawnConfig(
                BaseCount: 10, BeginWave: 5, Spacing: 5, Scaling: 2.0, ThreatLevel: 0.75,
                RegularRoster: new[] { "era3:enemy-line-infantry" },
                BossWave: BossWave, BossEnemyId: "era3:enemy-boss",
                BatchesPerWave: 1,
                EntryCells: new[] { new CellCoord(PathCol, 0) },
                Trample: FortressTrample,
                ShieldStartWave: E3ShieldStartWave),
            new SplitMixRng(11),
            new FixedClock());
        return (spawner, catalog);
    }

    private static Unit SpawnFortress(WaveSpawner spawner)
    {
        spawner.BeginWave(BossWave, startTick: 0);
        Unit boss = spawner.Poll(0).Single(u => u.IsBoss);
        Assert.Equal(0, boss.SpawnTick); // 践踏/战吼节拍相位基准（网格计数依赖）
        return boss;
    }

    /// <summary>护卫按内容 spec 构造（CS-4.4：hp/甲/速出自 era3/enemies.json；盾=波次公式值）。</summary>
    private static Unit SpawnEscort(EnemySpecCatalog catalog, string contentId, int col, int row)
    {
        EnemySpec spec = catalog.Get(contentId);
        var escort = new Unit(
            new EntityId(900 + col), contentId,
            col * CellSize + CellSize / 2f, row * CellSize + CellSize / 2f,
            spec.Hp, spec.Armor, (float)spec.SpeedPxPerTick);
        escort.SetShield(Shield.At(BossWave, E3ShieldStartWave)); // 波 40 值 330（同代常规行口径）
        escort.MarkSpawned(0);
        return escort;
    }

    /// <summary>
    /// 铁甲墙（era3:tower-ironclad-wall 支援行）：纯阻挡零伤害——只吃践踏的
    /// <see cref="ITrampleTarget"/> sink（hp 出自 towers.json）。
    /// </summary>
    private sealed class WallBlock : ITrampleTarget
    {
        public WallBlock(int col, int row, int hp)
        {
            X = col * CellSize + CellSize / 2f;
            Y = row * CellSize + CellSize / 2f;
            Hp = hp;
        }

        public float X { get; }

        public float Y { get; }

        public bool Alive { get; private set; } = true;

        public int Hp { get; private set; }

        public int TrampleHitsTaken { get; private set; }

        public void TakeTrample(int damage)
        {
            if (!Alive)
            {
                return;
            }

            TrampleHitsTaken++;
            Hp = Math.Max(0, Hp - damage);
            if (Hp == 0)
            {
                Alive = false;
            }
        }
    }

    /// <summary>
    /// 30 Hz 主循环：光环面（E3 无鼓手，保管线对齐）→ 速度折算/盾再生 → 行走
    /// → 塔开火 → 践踏脉冲（塔+墙）→ 战吼脉冲（友军），直至堡垒号死亡、
    /// 推进到基地 (6,33) 或窗口耗尽。
    /// </summary>
    private static BattleOutcome RunBattle(Loadout loadout, long windowTicks = WindowTicks)
    {
        var world = new GridWorld(MapWidth, MapHeight, new SplitMixRng(5), forestRatio: 0, rockRatio: 0, waterRatio: 0);
        FlowField field = FlowField.BuildComplete(world, new CellCoord(PathCol, MapHeight - 1));
        var movement = new UnitMovement(() => field, CellSize);

        (WaveSpawner spawner, EnemySpecCatalog catalog) = NewSpawner();
        Unit boss = SpawnFortress(spawner);
        var units = new List<Unit> { boss };
        var escorts = new List<Unit>();
        int guardCol = 4;
        for (int i = 0; i < loadout.GuardEscorts; i++) // 前出护卫（0.85 px/tick 跑在 Boss 前=火力诱饵）
        {
            Unit guard = SpawnEscort(catalog, "era3:enemy-ironclad-guard", guardCol, 0);
            escorts.Add(guard);
            units.Add(guard);
            guardCol += 4;
        }

        var rams = new List<Unit>();
        for (int i = 0; i < loadout.RamEscorts; i++) // 同速随行（0.53=lockstep，恒在战吼半径内）
        {
            Unit ram = SpawnEscort(catalog, "era3:enemy-ram", i % 2 == 0 ? PathCol - 1 : PathCol + 1, 0);
            rams.Add(ram);
            escorts.Add(ram);
            units.Add(ram);
        }

        var towers = new List<TestTower>();
        int index = 0;
        foreach ((int col, int row, UnitsContent.TowerStats stats) in loadout.Towers)
        {
            var tower = new TestTower(col * CellSize + 16f, row * CellSize + 16f, stats)
            {
                NextFireTick = index % Math.Max(1, stats.ReloadTicks), // 供弹错相：持续火力而非齐射浪涌
                PierceRounds = loadout.PierceRounds,
            };
            towers.Add(tower);
            index++;
        }

        int wallHp = UnitsContent.LoadIroncladWallHp();
        var walls = loadout.Walls.Select(w => new WallBlock(w.Col, w.Row, wallHp)).ToList();
        var trampleTargets = new List<ITrampleTarget>(towers.Count + walls.Count);
        trampleTargets.AddRange(towers);
        trampleTargets.AddRange(walls);

        int destroyed = 0;
        int cryGrid = 0;
        int cryBuffTicks = 0;
        int maxRamCry = 0;
        for (long tick = 1; tick <= windowTicks; tick++)
        {
            UnitAuras.ApplyWarDrums(units);
            foreach (Unit unit in units)
            {
                unit.ResolveSpeedEffects(tick);
                unit.TickShield(tick);
                movement.Step(unit);
            }

            foreach (Unit ram in rams)
            {
                if (!ram.Alive)
                {
                    continue;
                }

                maxRamCry = Math.Max(maxRamCry, ram.ResolvedSpeedBonusPercent);
                if (ram.ResolvedSpeedBonusPercent == 15)
                {
                    cryBuffTicks++;
                }
            }

            foreach (TestTower tower in towers)
            {
                tower.FireAt(units, tick);
                if (!tower.Alive && tower.DestroyedTick == -1)
                {
                    tower.MarkDestroyed(tick);
                    destroyed++;
                }
            }

            _ = movement.PulseTrample(boss, trampleTargets, tick);
            _ = movement.PulseWarCry(boss, units, tick);

            long phase = tick - boss.SpawnTick;
            if (boss.Alive && phase > 0 && phase % boss.WarCryCooldownTicks == 0)
            {
                cryGrid++; // 战吼节拍格点（与 PulseWarCry 同判据；半径内无友军亦开嗓）
            }

            if (!boss.Alive || boss.ArrivedAtTarget)
            {
                return Outcome(tick);
            }
        }

        return Outcome(windowTicks);

        BattleOutcome Outcome(long endTick) => new(
            Killed: !boss.Alive,
            ArrivedAtBase: boss.ArrivedAtTarget,
            EndTick: endTick,
            ShotsFired: towers.Sum(t => t.ShotsFired),
            TowersDestroyed: destroyed,
            TowerCount: towers.Count,
            WallsDestroyed: walls.Count(w => !w.Alive),
            WallCount: walls.Count,
            WallTrampleHits: walls.Sum(w => w.TrampleHitsTaken),
            TotalDamage: towers.Sum(t => t.DamageDealt),
            BossHpLost: boss.MaxHp - boss.Hp,
            BossShieldAbsorbed: boss.ShieldAbsorbedTotal,
            EscortPoolsDrained: escorts.Sum(e => (e.MaxHp - e.Hp) + e.ShieldAbsorbedTotal),
            WarCryGridPulses: cryGrid,
            WarCryBuffTicks: cryBuffTicks,
            MaxRamCryBonus: maxRamCry,
            StateHash: UnitStateHash.Compute(units));
    }

    /// <summary>
    /// 群体清场对照（穿甲 vs 无穿甲的「必须穿甲」决定性案）：波 40 盾 330 的
    /// 线列兵×4 静置于单加特林射界内（移动面与盾税正交，故不行走），直至
    /// 全灭。每兵时序=排液期（盾 330）+ 破盾窗内本体期（600 hp @150 eff/s
    /// =4 s＜5 s 窗→再生门未开即亡，税只落在排液期）。
    /// </summary>
    private static CrowdOutcome RunCrowdClear(bool pierceRounds)
    {
        UnitsContent.TowerStats stats = UnitsContent.LoadGatlingTower();
        var tower = new TestTower((PathCol - 1) * CellSize + 16f, 17 * CellSize + 16f, stats)
        {
            PierceRounds = pierceRounds,
        };

        EnemySpecCatalog catalog = UnitsContent.LoadEra3EnemyCatalog();
        EnemySpec spec = catalog.Get("era3:enemy-line-infantry");
        var units = new List<Unit>();
        for (int row = 16; row <= 19; row++)
        {
            var unit = new Unit(
                new EntityId(500 + row), "era3:enemy-line-infantry",
                PathCol * CellSize + CellSize / 2f, row * CellSize + CellSize / 2f,
                spec.Hp, spec.Armor, (float)spec.SpeedPxPerTick);
            unit.SetShield(Shield.At(BossWave, E3ShieldStartWave)); // 330（波 40；线列兵无个体盾）
            unit.MarkSpawned(0);
            units.Add(unit);
        }

        for (long tick = 1; tick <= WindowTicks; tick++)
        {
            foreach (Unit unit in units)
            {
                unit.ResolveSpeedEffects(tick);
                unit.TickShield(tick);
            }

            tower.FireAt(units, tick);
            if (units.All(u => !u.Alive))
            {
                return new CrowdOutcome(tick, units.Sum(u => u.ShieldAbsorbedTotal), tower.DamageDealt);
            }
        }

        return new CrowdOutcome(WindowTicks, units.Sum(u => u.ShieldAbsorbedTotal), tower.DamageDealt);
    }

    /// <summary>战吼观察（无塔）：同速随行冲车×2 在 600/1200 节拍受鼓，180-tick 窗口。</summary>
    private static (int BuffTicks, int MaxCry) RunWarCryObservation()
    {
        var world = new GridWorld(MapWidth, MapHeight, new SplitMixRng(5), 0, 0, 0);
        FlowField field = FlowField.BuildComplete(world, new CellCoord(PathCol, MapHeight - 1));
        var movement = new UnitMovement(() => field, CellSize);
        (WaveSpawner spawner, EnemySpecCatalog catalog) = NewSpawner();
        Unit boss = SpawnFortress(spawner);
        var units = new List<Unit> { boss };
        var rams = new List<Unit>();
        for (int i = 0; i < 2; i++)
        {
            Unit ram = SpawnEscort(catalog, "era3:enemy-ram", i % 2 == 0 ? PathCol - 1 : PathCol + 1, 0);
            rams.Add(ram);
            units.Add(ram);
        }

        int buffTicks = 0;
        int maxCry = 0;
        for (long tick = 1; tick <= 1300; tick++)
        {
            foreach (Unit unit in units)
            {
                unit.ResolveSpeedEffects(tick);
                unit.TickShield(tick);
                movement.Step(unit);
            }

            foreach (Unit ram in rams)
            {
                maxCry = Math.Max(maxCry, ram.ResolvedSpeedBonusPercent);
                if (ram.ResolvedSpeedBonusPercent == 15)
                {
                    buffTicks++;
                }
            }

            _ = movement.PulseWarCry(boss, units, tick);
        }

        return (buffTicks, maxCry);
    }

    // ---- 载入面 ------------------------------------------------------------

    private static Loadout ThreeTowerLoadout(bool pierceRounds = false) => new(
        Towers: new[]
        {
            // 贴廊加特林（±1 列=践踏半径 2 格内，随推进被拆）；外侧加特林（±3 列>64 px 践踏界）
            (PathCol - 1, 8, UnitsContent.LoadGatlingTower()),
            (PathCol + 1, 8, UnitsContent.LoadGatlingTower()),
            (PathCol - 3, 14, UnitsContent.LoadGatlingTower()),
            (PathCol + 3, 18, UnitsContent.LoadGatlingTower()),
            // 臼炮（±3 列践踏安全位、射程 11 格=本体主杀）
            (PathCol - 3, 20, UnitsContent.LoadMortarTower()),
            (PathCol + 3, 20, UnitsContent.LoadMortarTower()),
        },
        Walls: new[]
        {
            (PathCol - 1, 2), (PathCol + 1, 2), (PathCol - 1, 3), (PathCol + 1, 3),
        },
        PierceRounds: pierceRounds,
        GuardEscorts: 2,
        RamEscorts: 2);

    private static Loadout MortarLineLoadout(bool pierceRounds) => new(
        Towers: new[] { (PathCol - 3, 17, UnitsContent.LoadMortarTower()) },
        Walls: Array.Empty<(int, int)>(),
        PierceRounds: pierceRounds,
        GuardEscorts: 0,
        RamEscorts: 0);

    // ---- 验收主案：三塔载入 120 s 内可胜 ------------------------------------

    [Fact] // legal — 火力线击杀：三塔载入 120 s 窗口内击杀堡垒号，践踏/战吼参战，账目守恒
    public void Fortress_falls_to_the_three_tower_line_inside_the_window()
    {
        var outcome = RunBattle(ThreeTowerLoadout());

        // 手算带：臼炮 (3,20)/(9,20) 践踏安全位射程 11 格=352 px，横距 96 px
        // ⇒ |dy|<338.6 ⇒ 堡垒号 y>317.4 ⇒ 首发最早 t≈569；本体池 13500 eff
        // 与护卫池（2×(1200+330)+2×(3000+330)=10680）对全墙 eff 率（2×464+4×30
        // =1048 eff/s）⇒ 下界 ≈569+ceil(13500/1048×30)=956；上界=零塔到达 1964。
        // 实测 1155（含护卫逐个吸弹与盾相位的真实节拍），落带内。
        Assert.True(outcome.Killed, $"堡垒号未被击杀（终局={outcome.ArrivedAtBase}, 末刻={outcome.EndTick}）");
        Assert.False(outcome.ArrivedAtBase);
        Assert.InRange(outcome.EndTick, 956, 1958); // ≥理论最快下界，＜到达余量 5 tick
        Assert.Equal(1155, outcome.EndTick);        // 确定性钉值（重放全等的观测锚）
        Assert.Equal(13500, outcome.BossHpLost);    // Boss 视角：满额 HP 清零
        Assert.Equal(688, outcome.BossShieldAbsorbed); // 600 个体盾 + 88 再生重吸收（混合火力节拍）

        // 践踏参战：贴廊 ±1 对（仅此对处于践踏半径 2 格=64 px 内）被周期脉冲
        // 拆毁（400 hp 对 130/45 ticks：第 4 拍 520≥400）；±3 对不可达必存活
        // ⇒ 恰 2 毁；铁甲墙 1600 hp 扛过单次通过（≤5 拍 650<1600）=支援位职责。
        Assert.Equal(2, outcome.TowersDestroyed);
        Assert.Equal(6, outcome.TowerCount);
        Assert.Equal(0, outcome.WallsDestroyed);
        Assert.Equal(4, outcome.WallCount);
        Assert.Equal(20, outcome.WallTrampleHits); // 4 墙×5 拍（接触带 ~208 ticks÷45）

        // 战吼参战：600 节拍开嗓（战斗 1155<1200 未见第二拍），lockstep 冲车
        // 入半径（±1 列=32 px<96 px）吃到 +15%；前出铁甲卫兵 0.85 跑出半径=不受益
        // （同速随行才是战吼受众——发现注记）。窗口上限 2 冲车×180=360。
        Assert.Equal(1, outcome.WarCryGridPulses);
        Assert.Equal(15, outcome.MaxRamCryBonus);
        Assert.InRange(outcome.WarCryBuffTicks, 180, 360);

        // 守恒：总结算=13500+688+护卫池引排（9940，含护卫盾再生重吸收）+击杀
        // 溢杀带（≤4 可击杀单位×单发 464）；实测差 1104∈[0,1856]。
        Assert.Equal(25232, outcome.TotalDamage);
        int conservation = outcome.TotalDamage
            - (13500 + outcome.BossShieldAbsorbed + outcome.EscortPoolsDrained);
        Assert.InRange(conservation, 0, 4 * 464);
    }

    [Fact] // legal — 确定性双跑：整场战斗重放一致（结果记录与状态哈希逐位相等）
    public void The_battle_replays_identically()
    {
        BattleOutcome first = RunBattle(ThreeTowerLoadout());
        BattleOutcome second = RunBattle(ThreeTowerLoadout());

        Assert.Equal(first, second); // 记录全等（含 StateHash）
        Assert.True(first.Killed);

        BattleOutcome plainFirst = RunBattle(MortarLineLoadout(pierceRounds: false));
        BattleOutcome plainSecond = RunBattle(MortarLineLoadout(pierceRounds: false));
        Assert.Equal(plainFirst, plainSecond);
    }

    // ---- 两案对照：臼炮线穿甲弹 vs 无穿甲（击杀时间手算）---------------------

    [Fact] // legal — 两案同窗同杀点：无穿甲的盾再生压制 DPS 恰为 100 raw 税，穿甲归零
    public void Mortar_line_two_cases_match_the_hand_calc_and_the_regen_tax()
    {
        BattleOutcome pierce = RunBattle(MortarLineLoadout(pierceRounds: true));
        BattleOutcome plain = RunBattle(MortarLineLoadout(pierceRounds: false));

        // 手算（era3 towers.json 臼炮 d=480/reload 30/range 11；堡垒号甲 16 →
        // 单发有效 max(480−16,48)=464；塔 (3,17)→px(112,560)，横距 96 ⇒
        // |dy|<sqrt(352²−96²)=338.65 ⇒ y>221.35 ⇒ 首发恰 t=388
        // （ceil((221.35−16)/0.53)）；此后每 30 tick 一发，第 k 发落 388+30(k−1)）。
        // 盾相：首发吸 480（余 120）；第 2 发吸余量+溢出穿本体；破盾门 B+150；
        // 第 31 发（t=388+30×30=1288）终结：2 发盾相+29 发本体相。
        foreach (BattleOutcome outcome in new[] { pierce, plain })
        {
            Assert.True(outcome.Killed, $"臼炮单线必须可胜（末刻={outcome.EndTick}）");
            Assert.False(outcome.ArrivedAtBase);
            Assert.Equal(1288, outcome.EndTick); // 两案同杀点（税<末发松弛量 300，未过一发量子）
            Assert.Equal(31, outcome.ShotsFired);
        }

        // 穿甲弹案：每发刷新 90-tick 打断门>30-tick 供弹节拍 ⇒ 再生永不记账
        // ——盾总吸收恰=个体盾 600（恢复之门被持续关死）；账目=600+
        // 344（第 2 发溢出 360 raw→eff max(360−16,36)=344）+29×464=14400，
        // 溢杀 300。
        Assert.Equal(600, pierce.BossShieldAbsorbed);
        Assert.Equal(14400, pierce.TotalDamage);
        Assert.Equal(600 + 344 + (29 * 464), pierce.TotalDamage); // 手算逐项展开式

        // 无穿甲案：再生记账 100（破盾前 389..417 排液期 floor(2×29/3)=19 +
        // 破盾后 180-tick 再破周期 4 拍×~20——150 门+30 供弹对齐）；总吸收 700。
        // 溢出效按 1:1 折减（480−吸收−16 在 10% 地板之上）⇒ 总账 14400 中
        // 本体效=13700=13800−100，溢杀 200=300−100——被压制的 DPS 恰为 100
        // raw 点，且全部由再生通道产生。
        Assert.Equal(700, plain.BossShieldAbsorbed);
        Assert.Equal(14400, plain.TotalDamage);
        Assert.Equal(100, plain.BossShieldAbsorbed - pierce.BossShieldAbsorbed); // 税=再生记账
        Assert.Equal(
            pierce.TotalDamage - 13500 - (pierce.BossShieldAbsorbed - 600),
            plain.TotalDamage - 13500 - (plain.BossShieldAbsorbed - 600) + 100); // 溢杀差=税

        // 「必须穿甲」设计成立断言：无穿甲时盾再生持续压制 DPS（税>0 且恰等于
        // 再生记账）；穿甲把同一通道归零（税=0）。击杀点同刻是量子化结果
        // （税 100＜末发松弛 300），群体维度的决定性差异见下案。
        Assert.True(plain.BossShieldAbsorbed > pierce.BossShieldAbsorbed);
    }

    [Fact] // legal — 群体维度决定性案：波次盾再生税使无穿甲清场严格变慢（必须穿甲）
    public void Wave_shield_regen_tax_makes_pierce_mandatory_for_crowd_clearing()
    {
        CrowdOutcome pierce = RunCrowdClear(pierceRounds: true);
        CrowdOutcome plain = RunCrowdClear(pierceRounds: false);

        // 手算（加特林 d=16@15 发/s=240 raw/s；线列兵甲 6→eff max(16−6,2)=10
        // =150 eff/s；盾 330）：排液期穿甲 330/240=1.375 s、无穿甲净排
        // 330/(240−20)=1.5 s（未破之盾持续 20/s 再生，T-066 裁定②）——每兵
        // 慢 ~0.125 s=3.75 ticks；本体 600@150=4 s 落在 5 s 破盾窗内（税不
        // 再累积）；×4 兵 ⇒ 预期 ~15 ticks 严格差，实测 16。
        Assert.True(
            plain.AllDeadTick > pierce.AllDeadTick,
            $"无穿甲清场必须严格更慢（穿甲 {pierce.AllDeadTick} vs 无穿甲 {plain.AllDeadTick}）");
        Assert.Equal(16, plain.AllDeadTick - pierce.AllDeadTick);

        // 穿甲：总吸收恰=4×330（零再生记账）；无穿甲：+116 再生税（4×排液期
        // ~29 ticks×2/3 点/tick）——与击杀时间差同源。
        Assert.Equal(4 * 330, pierce.ShieldAbsorbed);
        Assert.Equal(4 * 330 + 116, plain.ShieldAbsorbed);
        Assert.True(plain.DamageDealt > pierce.DamageDealt); // 税体现为多结算的吸收面
    }

    // ---- 零塔对照 ------------------------------------------------------------

    [Fact] // boundary — 零塔对照：堡垒号无伤推进，到达时刻=纯移动手算值，战吼空拍照常
    public void Fortress_walks_unharmed_to_the_base_with_zero_towers()
    {
        var outcome = RunBattle(new Loadout(
            Array.Empty<(int, int, UnitsContent.TowerStats)>(),
            Array.Empty<(int, int)>(),
            PierceRounds: false,
            GuardEscorts: 0,
            RamEscorts: 0));

        // 手算：走廊 (6,0)→(6,33)，出生 Y=16，到达需 Y≥33×32=1056；
        // (1056−16)=1040 px ÷ 0.53 px/tick ≈ 1962.3 → 预计 1963（±3=格心
        // Snap 取整容差），实测 1964。
        long expectedArrival = (long)Math.Ceiling(1040.0 / 0.53);
        Assert.InRange(expectedArrival, 1960, 1966);
        Assert.False(outcome.Killed, "零火力下堡垒号必存活至到达");
        Assert.True(outcome.ArrivedAtBase, "基地必须被突破（零塔时唯一结局）");
        Assert.InRange(outcome.EndTick, expectedArrival - 3, expectedArrival + 3);
        Assert.Equal(0, outcome.BossHpLost);
        Assert.Equal(0, outcome.TotalDamage);
        Assert.Equal(0, outcome.BossShieldAbsorbed); // 满盾入库：零吸收
        Assert.Equal(3, outcome.WarCryGridPulses); // 600/1200/1800 三拍（无友军亦开嗓）
    }

    // ---- 战吼专项 ------------------------------------------------------------

    [Fact] // legal — 战吼参数接线+节拍窗口：T-066 参数位（era3/enemies.json war_cry）经 spawner 全量生效
    public void War_cry_wires_the_trait_params_and_buffs_lockstep_escorts_on_the_grid()
    {
        // 参数接线（③：战吼参数接模拟——spawner 侧消费 spec 四元组）
        (WaveSpawner spawner, _) = NewSpawner();
        Unit boss = SpawnFortress(spawner);
        Assert.Equal(96, boss.WarCryRadiusPx);        // 3 格×32（war_cry.radius_cells）
        Assert.Equal(15, boss.WarCrySpeedBonusPercent);
        Assert.Equal(180, boss.WarCryDurationTicks);  // 6 s
        Assert.Equal(600, boss.WarCryCooldownTicks);  // 20 s
        Assert.True(boss.TramplePeriodTicks > 0, "践踏参数位同链接线（TrampleProfile）");

        // 节拍窗口：600 拍于 tick 末生效 → Resolve 在 601..779 可见（tick<780
        // 严格界=179 ticks）；1200 拍 → 1201..1300（窗口被观察截止截断=100）；
        // 2 冲车 ⇒ 恰 (179+100)×2=558。
        (int buffTicks, int maxCry) = RunWarCryObservation();
        Assert.Equal(15, maxCry);
        Assert.Equal((179 + 100) * 2, buffTicks);
    }
}
