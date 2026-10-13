using System.Diagnostics;
using EraCore.Combat;
using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Pathfinding;
using EraCore.Tests.Pathfinding;
using EraCore.Units;
using Xunit;
using Xunit.Abstractions;

namespace EraCore.Tests.Spikes;

/// <summary>
/// T-044 spike② (F-167, C-11×C-07 前体): 迷宫化布局 + 供弹链同图共存的可玩性
/// 验证。一个 64×64 复合迷宫（Wall 地形蛇廊 + BuildingRegistry 带血量城墙 +
/// 双代价补给走廊）上，敌群沿 FlowField 梯度从出生点涌向基地，民夫沿 A* 路线
/// 穿越同一张图送弹药；1800 tick（60 s @30 Hz）采样窗口内计数器留档：
/// 敌通行量（到达/在途/被杀）、民夫交付量、流场重建次数与耗时、A* 重算次数、
/// 迷宫路径长度比。互不阻塞的证明是计数器级的：
/// <list type="bullet">
/// <item>敌流场不把民夫当障碍 —— BloodCost 只由建筑表提供；民夫从不注册占用
/// （registry 计数与地图哈希在纯交通窗口内恒定），且重建只由两次建筑事件触发
/// （塔放置+落闸），单位交通从不弄脏流场；</item>
/// <item>民夫 A* 不因敌群改道 —— A* 只读地形/建筑（敌我单位不进入网格状态），
/// 敌群满图时同一查询的路径哈希与空图一致，SearchCount 在纯交通窗口内恒定；</item>
/// <item>走廊双代价 —— IFlowGrid 单代价不支持双表，用两个视图实现同一世界
/// 状态的敌/民夫代价（敌 50 / 民 1），城门交规=逻辑层 Cost 差异。</item>
/// </list>
/// 性能采样全部来自真实组件（RL2/CS-4.5：真 Stopwatch 计时、真计数器，无跳过
/// 无捷径）；预热 900 tick 让民夫与敌群分布到全程，采样窗从均匀态起算。
/// </summary>
// [F-167] spike② 迷宫+供弹共存（T-044；计数器采样留档基准）
public sealed class Spike2CoexistenceTests
{
    private const int WarmupTicks = 900;

    private const int WindowTicks = 1800; // 60 s @ 30 Hz (K-1 tick domain)

    private const int SampleEvery = 60;

    private const int EventTick = 900; // window-relative

    private const int AliveCap = 250;

    private const int SpawnEveryTicks = 5;

    private const int StallLimitTicks = 45; // 1.5 s without any position change = deadlock

    private const float EnemySpeedPxPerTick = 5f;

    private const int EnemyHp = 120;

    private const int TowerDamagePerShot = 120;

    private const int TowerReloadTicks = 30;

    private const float TowerRangePx = 5.5f * 32f;

    private readonly ITestOutputHelper _output;

    public Spike2CoexistenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class EnemyTrack
    {
        public float LastX;
        public float LastY;
        public int StallRun;
        public bool PrevArrived;
        public bool EverInCorridor;
        public bool Deadlock;
    }

    // ------------------------------------------------------------ spike② 主模拟

    [Fact] // [F-167] [C-11] 敌流与弹流同图互不阻塞 + 计数器采样
    public void MazeSupplyCoexistenceOverSamplingWindow()
    {
        // ---- 场景与引擎（全部真实组件） -------------------------------
        var scenario = new Spike2MazeScenario(seed: 20261013);
        var porterProbe = new CountingGrid(scenario.Registry); // porter view = composed registry grid
        var pathfinder = new AStarPathfinder(porterProbe);
        var cache = new PathCache(pathfinder);
        var flow = new FlowFieldManager(scenario.EnemyGrid, maxActiveFields: 6, defaultFrameBudgetMs: 8.0);
        var enemyKey = new FlowFieldKey(Lane: 0, CostType: 0);
        var jitter = new EraCore.Tests.World.LcgRng(777);

        // ---- 初建流场（预算分片推进到收敛，计时留档） -------------------
        var buildSw = Stopwatch.StartNew();
        FlowField field = flow.Acquire(enemyKey, scenario.Base);
        while (!field.IsReady)
        {
            flow.Update(FlowFieldManager.DefaultFrameBudgetMs);
            field = flow.Acquire(enemyKey, scenario.Base);
        }

        buildSw.Stop();
        double initialBuildMs = buildSw.Elapsed.TotalMilliseconds;
        Assert.Equal(FlowFieldMode.Normal, field.Mode); // 迷宫连通，无拆墙必要
        Assert.Equal(1, flow.GetRebuildCount(enemyKey));

        // ---- 迷宫路径长度比（敌梯度路径 vs 直线） -----------------------
        var enemyPath = field.BuildPath(scenario.PathProbe);
        int dx = Math.Abs(scenario.Base.X - scenario.PathProbe.X);
        int dy = Math.Abs(scenario.Base.Y - scenario.PathProbe.Y);
        double straightCells = Math.Sqrt((dx * dx) + (dy * dy));
        double ratio = enemyPath.Count / straightCells;
        Assert.True(ratio >= 3.0, $"迷宫化路径长度比 {ratio:F2} 低于验收线 3.0（路径 {enemyPath.Count} 格 vs 直线 {straightCells:F1} 格）");

        int enemyCorridorOnPath = enemyPath.Count(c => scenario.IsCorridor(c.X, c.Y));
        Assert.True(enemyCorridorOnPath <= 16, $"敌梯度路径走廊格 {enemyCorridorOnPath} > 16：敌流被走廊吸走，交规失效");

        // ---- 民夫初始路线（缓存冷启动，逐条真实搜索） -------------------
        var routes = scenario.DepotCells.Select(home => cache.GetPath(home, scenario.Destination)).ToList();
        Assert.All(routes, r => Assert.Equal(PathQueryStatus.Found, r.Status));
        int initialSearches = (int)pathfinder.SearchCount;
        Assert.Equal(scenario.DepotCells.Count, initialSearches);
        int porterCorridorOnRoute = routes.Sum(r => r.Cells.Count(c => scenario.IsCorridor(c.X, c.Y))) / routes.Count;
        Assert.True(porterCorridorOnRoute >= 40, $"民夫路线走廊格均值 {porterCorridorOnRoute} < 40：民夫没有走补给走廊");
        long porterRouteHashAtStart = routes[0].Hash;

        // ---- 城墙塔（贴敌路径尾段放置，真占用+真 BloodCost） ------------
        var towerCells = PickTowerCells(scenario, enemyPath);
        for (int i = 0; i < towerCells.Count; i++)
        {
            scenario.Registry.Place(new EntityId(700_000 + i), SpikeTowerSpec(), towerCells[i]);
        }

        flow.MarkDirty(new CellRegion(
            towerCells.Min(c => c.X) - 1, towerCells.Min(c => c.Y) - 1,
            towerCells.Max(c => c.X) - towerCells.Min(c => c.X) + 3,
            towerCells.Max(c => c.Y) - towerCells.Min(c => c.Y) + 3));
        int expectedRebuilds = 2; // 初建 + 塔放置
        var towerSw = Stopwatch.StartNew();
        while (flow.GetRebuildCount(enemyKey) < expectedRebuilds)
        {
            flow.Update(FlowFieldManager.DefaultFrameBudgetMs);
        }

        towerSw.Stop();
        Assert.Equal(FlowFieldMode.Normal, flow.GetField(enemyKey)!.Mode);
        double towerRebuildMs = towerSw.Elapsed.TotalMilliseconds;

        // ---- 民夫（同一供应商/接收端，各自独立路线） ---------------------
        var supplier = new InfiniteSupplier();
        var receiver = new AcceptAllReceiver();
        var carriers = new List<Carrier>();
        var carrierPrevPhase = new List<CarrierPhase>();
        var porterInCorridor = new List<bool>();
        var porterVisitedCells = new HashSet<CellCoord>();
        var porterCorridorCells = new HashSet<CellCoord>();
        for (int i = 0; i < routes.Count; i++)
        {
            carriers.Add(new Carrier(new EntityId(500_000 + i), scenario.Ammo, supplier, receiver, routes[i].Cells));
            carrierPrevPhase.Add(CarrierPhase.Loading);
            porterInCorridor.Add(false);
        }

        var movement = new UnitMovement(() => flow.GetField(enemyKey)!);

        // ---- 计数器 ------------------------------------------------------
        var enemies = new List<Unit>();
        var track = new Dictionary<Unit, EnemyTrack>();
        int spawned = 0, arrived = 0, killed = 0, towerShots = 0;
        int spawnIdx = 0;
        long nextId = 600_000;
        int reroutesIssued = 0, rerouteFallbacks = 0, droppedEntries = 0;
        bool staleDepartures = false;
        bool eventPendingRebuild = false;
        double eventRebuildMs = 0;
        int maxStall = 0;
        var updateSw = new Stopwatch();
        double updateMsTotal = 0, updateMsMax = 0;
        long updateCalls = 0;
        var towerCentres = towerCells
            .Select(c => (X: c.X * 32f + 16f, Y: c.Y * 32f + 16f))
            .ToList();

        void SpawnEnemy(long tick)
        {
            var cell = scenario.SpawnCells[spawnIdx++ % scenario.SpawnCells.Count];
            float jx = jitter.Next(-6, 7);
            float jy = jitter.Next(-6, 7);
            var unit = new Unit(
                new EntityId(nextId++), "spike2:skirmisher",
                (cell.X * 32f) + 16f + jx, (cell.Y * 32f) + 16f + jy,
                maxHp: EnemyHp, armor: 0, speedPxPerTick: EnemySpeedPxPerTick);
            unit.MarkSpawned(tick);
            unit.Died += _ => killed++;
            enemies.Add(unit);
            track[unit] = new EnemyTrack { LastX = unit.X, LastY = unit.Y };
            spawned++;
        }

        void TryReroute(Carrier carrier)
        {
            var target = carrier.Phase == CarrierPhase.Outbound ? carrier.Destination : carrier.Home;
            var fresh = cache.GetPath(carrier.CurrentCell, target);
            if (fresh.Status == PathQueryStatus.Found && fresh.Cells.Count >= 2)
            {
                carrier.Reroute(fresh.Cells);
                reroutesIssued++;
            }
            else
            {
                // 站在落闸格上的民夫：无路可询（A* 端点不可通行），保留原折线
                // 继续走（移动层不检查通行性）——计入异常计数，断言封顶。
                rerouteFallbacks++;
            }
        }

        void PlaceEventWall()
        {
            scenario.PlaceEventWall(900_000); // 城门落闸：封第一道蛇廊的走廊闸口
            flow.MarkDirty(new CellRegion(SerpentineColumns0() - 1, Spike2MazeScenario.CorridorY0 - 1, 3, 3));
            droppedEntries = cache.Invalidate(scenario.EventWallCells);
            expectedRebuilds++;
            eventPendingRebuild = true;
            foreach (var carrier in carriers)
            {
                if (carrier.Phase is CarrierPhase.Outbound or CarrierPhase.Inbound)
                {
                    TryReroute(carrier); // 在途民夫立即改道（加墙→失效→重算→改道）
                }
            }

            staleDepartures = true; // 装载/卸载中的民夫在出发相位切换时换新路
        }

        void FireTowers(long tick)
        {
            for (int ti = 0; ti < towerCentres.Count; ti++)
            {
                if ((tick + (ti * 15)) % TowerReloadTicks != 0)
                {
                    continue;
                }

                Unit? best = null;
                float bestD2 = TowerRangePx * TowerRangePx;
                foreach (var unit in enemies)
                {
                    if (!unit.Alive || unit.ArrivedAtTarget)
                    {
                        continue;
                    }

                    float ex = unit.X - towerCentres[ti].X;
                    float ey = unit.Y - towerCentres[ti].Y;
                    float d2 = (ex * ex) + (ey * ey);
                    if (d2 <= bestD2)
                    {
                        bestD2 = d2;
                        best = unit;
                    }
                }

                if (best is not null)
                {
                    best.ApplyDamage(TowerDamagePerShot, tick); // 真实结算入口
                    towerShots++;
                }
            }
        }

        void TickOnce(long tick)
        {
            // 1) 敌流滴灌（上限内每 5 tick 一只）
            if (tick % SpawnEveryTicks == 0 && (spawned - killed - arrived) < AliveCap)
            {
                SpawnEnemy(tick);
            }

            // 2) 流场帧切片（真预算、真计时）
            updateSw.Restart();
            flow.Update(FlowFieldManager.DefaultFrameBudgetMs);
            updateSw.Stop();
            double ms = updateSw.Elapsed.TotalMilliseconds;
            updateMsTotal += ms;
            updateCalls++;
            if (ms > updateMsMax)
            {
                updateMsMax = ms;
            }

            if (eventPendingRebuild)
            {
                eventRebuildMs += ms;
                if (flow.GetRebuildCount(enemyKey) >= expectedRebuilds)
                {
                    eventPendingRebuild = false;
                }
            }

            // 3) 敌群梯度推进 + 通行计数
            movement.StepAll(enemies);
            foreach (var unit in enemies)
            {
                var tr = track[unit];
                if (unit.Alive && !unit.ArrivedAtTarget)
                {
                    if (unit.X == tr.LastX && unit.Y == tr.LastY)
                    {
                        tr.StallRun++;
                        if (tr.StallRun > maxStall)
                        {
                            maxStall = tr.StallRun;
                        }

                        if (tr.StallRun > StallLimitTicks)
                        {
                            tr.Deadlock = true;
                        }
                    }
                    else
                    {
                        tr.StallRun = 0;
                    }

                    tr.LastX = unit.X;
                    tr.LastY = unit.Y;
                    int cx = (int)Math.Floor(unit.X / 32f);
                    int cy = (int)Math.Floor(unit.Y / 32f);
                    if (scenario.IsCorridor(cx, cy))
                    {
                        tr.EverInCorridor = true; // 敌横穿走廊带（付费过路）留痕
                    }
                }

                if (!tr.PrevArrived && unit.ArrivedAtTarget)
                {
                    arrived++;
                }

                tr.PrevArrived = unit.ArrivedAtTarget;
            }

            // 4) 塔火力（错峰齐射）
            FireTowers(tick);

            // 5) 民夫节拍 + 相位切换换路 + 走廊使用留痕
            for (int i = 0; i < carriers.Count; i++)
            {
                Carrier carrier = carriers[i];
                carrier.Tick();
                var now = carrier.Phase;
                if (now != carrierPrevPhase[i])
                {
                    if (staleDepartures
                        && ((carrierPrevPhase[i] == CarrierPhase.Loading && now == CarrierPhase.Outbound)
                            || (carrierPrevPhase[i] == CarrierPhase.Unloading && now == CarrierPhase.Inbound)))
                    {
                        TryReroute(carrier);
                    }

                    carrierPrevPhase[i] = now;
                }

                var cell = carrier.CurrentCell;
                porterVisitedCells.Add(cell);
                if (scenario.IsCorridor(cell.X, cell.Y))
                {
                    porterInCorridor[i] = true;
                    porterCorridorCells.Add(cell);
                }
            }
        }

        void SampleLine(int windowTick, long deliveries)
        {
            _output.WriteLine(
                $"t={windowTick,4} | alive={spawned - killed - arrived,3} arr={arrived,3} kill={killed,3}" +
                $" | deliv={deliveries,3} reroute={reroutesIssued + rerouteFallbacks,2}" +
                $" | rebuild={flow.GetRebuildCount(enemyKey)} search={pathfinder.SearchCount}" +
                $" | updMax={updateMsMax:F2}ms");
        }

        // ---- 预热：让民夫与敌群分布到全程 -------------------------------
        for (long w = 0; w < WarmupTicks; w++)
        {
            TickOnce(w);
        }

        long deliveriesAtWindowStart = carriers.Sum(c => c.DeliveredItems);

        // 纯交通窗口快照（t=300 与 t=899，均无建筑事件）：互不阻塞的计数器证明
        long searchesSpanA = 0, mapHashSpanA = 0;
        int rebuildsSpanA = 0, registrySpanA = 0;

        // ---- 采样窗（1800 tick） ----------------------------------------
        for (int t = 0; t < WindowTicks; t++)
        {
            if (t == EventTick)
            {
                PlaceEventWall();
            }

            TickOnce(WarmupTicks + t);

            if (t == 300)
            {
                searchesSpanA = pathfinder.SearchCount;
                rebuildsSpanA = flow.GetRebuildCount(enemyKey);
                mapHashSpanA = scenario.World.ComputeMapHash();
                registrySpanA = scenario.Registry.Count;
            }

            if (t == 899)
            {
                // 断言块 A：纯单位交通窗口内，网格状态、流场与 A* 全部静止
                Assert.Equal(searchesSpanA, pathfinder.SearchCount); // 敌群不引发民夫重算
                Assert.Equal(rebuildsSpanA, flow.GetRebuildCount(enemyKey)); // 民夫/敌不弄脏流场
                Assert.Equal(mapHashSpanA, scenario.World.ComputeMapHash()); // 单位不写地形
                Assert.Equal(registrySpanA, scenario.Registry.Count); // 民夫从不注册占用

                // 敌群满图（~250 在途）时，同一民夫查询的无缓存重算与
                // 空图初始路线哈希一致——A* 只看地形，敌我不互斥。
                var probeAt899 = new AStarPathfinder(scenario.Registry)
                    .FindPath(scenario.DepotCells[0], scenario.Destination);
                Assert.Equal(PathQueryStatus.Found, probeAt899.Status);
                Assert.Equal(porterRouteHashAtStart, probeAt899.Hash);
            }

            if (t % SampleEvery == 0)
            {
                SampleLine(t, carriers.Sum(c => c.DeliveredItems) - deliveriesAtWindowStart);
            }
        }

        // ---- 收口断言与汇总 ---------------------------------------------
        long windowDeliveries = carriers.Sum(c => c.DeliveredItems) - deliveriesAtWindowStart;
        int inTransit = enemies.Count(u => u.Alive && !u.ArrivedAtTarget);
        int corridorCrossers = enemies.Count(u => track[u].EverInCorridor);
        int deadlocks = enemies.Count(u => track[u].Deadlock);
        int sharedCells = porterVisitedCells.Intersect(enemyPath).Count();
        double avgUpdateMs = updateCalls > 0 ? updateMsTotal / updateCalls : 0;

        // 敌流推进：无死锁、有到达、有击杀、有在途（四态计数全部真实采样）
        Assert.True(deadlocks == 0, $"敌死锁 {deadlocks} 只（stall>{StallLimitTicks}），maxStall={maxStall}");
        Assert.True(arrived >= 5, $"敌到达量 {arrived} < 5：迷宫通道不可玩");
        Assert.True(killed >= 3, $"塔击杀量 {killed} < 3");
        Assert.True(inTransit >= 1, "采样窗结束时无在途敌");

        // 民夫交付：迷宫内供弹链有产出
        Assert.True(windowDeliveries >= 4, $"采样窗民夫交付 {windowDeliveries} 件 < 4");

        // 流场重建：只由两次建筑事件触发，且均在 8 ms 帧预算内收敛
        Assert.Equal(expectedRebuilds, flow.GetRebuildCount(enemyKey));
        Assert.Equal(FlowFieldMode.Normal, flow.GetField(enemyKey)!.Mode); // 落闸后仍连通
        Assert.True(updateMsMax <= 10.0, $"流场单帧耗时 {updateMsMax:F2} ms 超预算余量（8 ms 预算 + 批过冲）");

        // 事件链：缓存受影响失效 + 在途改道
        Assert.True(droppedEntries >= 5, $"落闸只失效 {droppedEntries} 条缓存路线");
        Assert.True(reroutesIssued + rerouteFallbacks >= 5, $"落闸后只改道 {reroutesIssued + rerouteFallbacks} 次");
        Assert.True(rerouteFallbacks <= 2, $"{rerouteFallbacks} 只民夫站在落闸格上无法改道（异常态>2）");

        // 双代价交规：两类流都真实使用走廊（同图共存、同格共用、互不阻塞）。
        // 敌并非每只都穿走廊：走廊带两端的侧翼（x<4 / x>58）是平价森林，
        // 梯度合法绕行；中段蛇廊区无侧翼，敌流在那里付费 150/格穿走廊——
        // 计数的是"敌流真实借道共享走廊格"，不是全员穿越。
        Assert.True(porterInCorridor.Count(b => b) >= 8, $"进入走廊的民夫 {porterInCorridor.Count(b => b)} < 8");
        Assert.True(corridorCrossers >= 100, $"借道走廊共享区的敌 {corridorCrossers}/{spawned} < 100：敌流从未进入走廊");
        Assert.True(sharedCells >= 5, $"民夫足迹与敌路径重叠仅 {sharedCells} 格：两流未真实共用地图");
        Assert.True(porterCorridorCells.Count >= 20, $"民夫走廊覆盖 {porterCorridorCells.Count} 格 < 20");

        // 民夫 A* 语义：全程 0 次 BloodCost 查询（拆墙价码只属于敌流场）
        Assert.Equal(0, porterProbe.BloodCalls);

        // ---- 汇总留档（数值即本测试真实采样，报告表由此誊录） -----------
        _output.WriteLine("==== spike② 计数器采样汇总（T-044 / F-167） ====");
        _output.WriteLine($"窗口: {WindowTicks} tick (60 s @30 Hz), 预热 {WarmupTicks} tick, 落闸事件 @t={EventTick}");
        _output.WriteLine($"敌流: 生成={spawned} 到达={arrived} 被杀={killed} 在途={inTransit} 横穿走廊={corridorCrossers} 死锁={deadlocks} maxStall={maxStall} tick");
        _output.WriteLine($"弹流: 民夫={carriers.Count} 窗口交付={windowDeliveries} 件 改道={reroutesIssued} 回退={rerouteFallbacks} 缓存失效路线={droppedEntries}");
        _output.WriteLine($"流场: 重建={flow.GetRebuildCount(enemyKey)} (初建+塔+落闸) 初建={initialBuildMs:F2}ms 塔重建={towerRebuildMs:F2}ms 落闸重建={eventRebuildMs:F2}ms");
        _output.WriteLine($"流场帧切片: 调用={updateCalls} 均值={avgUpdateMs:F3}ms 最大={updateMsMax:F2}ms (预算 8 ms/帧)");
        _output.WriteLine($"A*: 搜索={pathfinder.SearchCount} 命中={cache.Hits} 未中={cache.Misses} 民夫BloodCost查询={porterProbe.BloodCalls}");
        _output.WriteLine($"迷宫: 敌路径={enemyPath.Count} 格 直线={straightCells:F1} 格 长度比={ratio:F2} 敌路径走廊格={enemyCorridorOnPath} 民夫路线走廊格(均值)={porterCorridorOnRoute}");
        _output.WriteLine($"共用: 民夫足迹∩敌路径={sharedCells} 格 民夫走廊覆盖={porterCorridorCells.Count} 格 塔射击={towerShots}");
        _output.WriteLine($"地图: Wall地形={scenario.WallTerrainCells} 格 血量墙={scenario.HpWallBuildings + scenario.EventWallCells.Count} 座 建筑总数={scenario.Registry.Count}");
        _output.WriteLine($"判定: 共存可玩 —— 敌流与弹流同图互不阻塞（计数器证明见断言块 A），重建/切片在预算内");
    }

    // ------------------------------------------------------------ 双代价视图

    [Fact] // [F-167] [C-11] 走廊双代价：同一格敌 50 / 民 1，越界即恢复地形价
    public void CorridorDualCostViewPricesCorridorForEnemyOnly()
    {
        var scenario = new Spike2MazeScenario();

        // legal: 走廊格对敌视图 50、对民夫视图 1（Road），同格双价
        var corridorCell = new CellCoord(20, Spike2MazeScenario.CorridorY0);
        Assert.Equal(Spike2EnemyCorridorView.EnemyCorridorCost, scenario.EnemyGrid.Cost(corridorCell));
        Assert.Equal(1, scenario.Registry.Cost(corridorCell));

        // boundary: 北衬墙以外（y=26，森林），敌视图恢复地形价 2（覆写只命中走廊带）
        var offCorridor = new CellCoord(20, Spike2MazeScenario.CorridorY0 - 4);
        Assert.Equal(2, scenario.EnemyGrid.Cost(offCorridor));
        Assert.Equal(scenario.Registry.Cost(offCorridor), scenario.EnemyGrid.Cost(offCorridor));

        // illegal: 越界格两个视图都拒绝（抛出而非返回默认价）
        Assert.Throws<ArgumentOutOfRangeException>(() => scenario.EnemyGrid.Cost(new CellCoord(-1, 30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => scenario.Registry.Cost(new CellCoord(64, 30)));
    }

    [Fact] // [F-167] [C-11] 敌梯度绕开走廊、民夫 A* 穿走廊（同图分流）
    public void EnemyFieldAvoidsCorridorWhilePorterRouteUsesIt()
    {
        var scenario = new Spike2MazeScenario();
        FlowField field = FlowField.BuildComplete(scenario.EnemyGrid, scenario.Base);
        Assert.Equal(FlowFieldMode.Normal, field.Mode);

        var enemyPath = field.BuildPath(scenario.PathProbe);
        int enemyCorridor = enemyPath.Count(c => scenario.IsCorridor(c.X, c.Y));
        Assert.True(enemyCorridor <= 16, $"敌路径走廊格 {enemyCorridor} 过多：敌流被走廊吸收");

        var porter = new AStarPathfinder(scenario.Registry);
        var route = porter.FindPath(scenario.DepotCells[0], scenario.Destination);
        Assert.Equal(PathQueryStatus.Found, route.Status);
        int porterCorridor = route.Cells.Count(c => scenario.IsCorridor(c.X, c.Y));
        Assert.True(porterCorridor >= 40, $"民夫路线走廊格 {porterCorridor} < 40：走廊没有成为弹流干线");

        // 敌路径比民夫路径长得多（迷宫化敌道 vs 走廊弹道）——同图两类流形态分离
        Assert.True(enemyPath.Count > route.Cells.Count, "敌迷宫路径应显著长于民夫走廊路径");
    }

    // ------------------------------------------------------------ 辅助

    private static int SerpentineColumns0() => Spike2MazeScenario.SerpentineColumns[0];

    /// <summary>
    /// Picks three tower cells hugging the FINAL LEG of the enemy path (the
    /// last-region climb, cells [-40,-8] from the end; distance 1 from the
    /// route, passable, off-path and off-corridor) — enemies spend ~200 ticks
    /// inside the overlapping fire windows there, enough to accumulate kills.
    /// </summary>
    private static List<CellCoord> PickTowerCells(Spike2MazeScenario scenario, IReadOnlyList<CellCoord> enemyPath)
    {
        (int Dx, int Dy)[] offsets =
        {
            (-1, 0), (1, 0), (0, -1), (0, 1),
            (-1, -1), (1, -1), (-1, 1), (1, 1),
        };
        var pathSet = new HashSet<CellCoord>(enemyPath);
        var picked = new List<CellCoord>();
        int start = Math.Max(enemyPath.Count - 8, 0);
        int stop = Math.Max(enemyPath.Count - 40, 0);
        for (int i = start; i > stop && picked.Count < 3; i--)
        {
            var p = enemyPath[i];
            foreach (var (ox, oy) in offsets)
            {
                var n = new CellCoord(p.X + ox, p.Y + oy);
                if (scenario.World.Contains(n)
                    && scenario.Registry.Passable(n)
                    && !pathSet.Contains(n)
                    && !scenario.IsCorridor(n.X, n.Y)
                    && !picked.Contains(n))
                {
                    picked.Add(n);
                    break;
                }
            }
        }

        Assert.True(picked.Count == 3, "贴路塔位不足 3 个：场景构造与预期不符");
        return picked;
    }

    /// <summary>
    /// Spike-local tower spec (Pierce post guarding the final approach):
    /// registry bookkeeping entry; the firing loop reads the constants from
    /// the test class. Product content arrives from data/towers.json.
    /// </summary>
    private static TowerSpec SpikeTowerSpec() => new(
        Id: "spike2:arrow-post",
        Class: TowerClass.Pierce,
        Tier: 1,
        MaxTier: 1,
        CostTotal: 30,
        Dps: TowerDamagePerShot * 30.0 / TowerReloadTicks,
        DamagePerShot: TowerDamagePerShot,
        ReloadTicks: TowerReloadTicks,
        RangeCells: 4.5,
        Hp: 80,
        AmmoItem: null,
        AmmoPerShot: 0,
        TierGrowth: null,
        Traits: null);
}
