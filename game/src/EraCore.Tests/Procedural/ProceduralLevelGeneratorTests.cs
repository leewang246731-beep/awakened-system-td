using EraCore.Contracts.Foundation;
using EraCore.Pathfinding;
using EraCore.Procedural;
using EraCore.Tests.Sim;
using EraCore.World;
using Xunit;
using static EraCore.Tests.Procedural.ProceduralEntryContent;

namespace EraCore.Tests.Procedural;

// [C-19][F-007][F-048] T-140 生成器核心：同 seed 同图（逐字节/逐格全等）/
// 异 seed 相异/威胁度标定（§7 公式对齐）/资源配比带/出生点→基地可达性
//（流场复用）/确定性重摇链（attempt 入哈希）。装载兼容带
//（water/rock=0）下首摇恒过=保险机制；水域带在核心层实证重摇。
public class ProceduralLevelGeneratorTests
{
    private static ProceduralLevel Generate(
        long seed,
        ProceduralTier? tier = null,
        ProceduralConstraints? constraints = null) =>
        ProceduralLevelGenerator.Generate(
            new ProceduralRequest(seed, tier ?? Load().DefaultTier(), Era1Pack(), constraints ?? Load().Constraints),
            derived => new SimRng(derived));

    // ---- 同 seed 同图（硬验收①） -------------------------------------------

    [Fact]
    public void Same_seed_generates_identical_levels_byte_for_byte()
    {
        // legal — 同 seed 同图：双实例逐字节（JSON 串）+逐格（GridWorld
        // 地形哈希）+全字段（身份哈希）全等。
        ProceduralLevel first = Generate(20261018);
        ProceduralLevel second = Generate(20261018);
        Assert.Equal(first.ToLevelJson(), second.ToLevelJson());
        Assert.Equal(first.TerrainHash, second.TerrainHash);
        Assert.Equal(first.IdentityHash(), second.IdentityHash());
        Assert.Equal(first.Rerolls, second.Rerolls);
    }

    [Fact]
    public void Same_seed_terrain_matches_the_pipeline_gridworld_draw()
    {
        // legal — 生成器地形=装载管线重放：用产物 map 行（seed+尺寸+比例）
        // 走生产 GridWorld 构造，地形哈希与生成器侧全等（生成器不画第二套地形）。
        ProceduralLevel level = Generate(20261018);
        var replay = new GridWorld(
            level.Map.Width, level.Map.Height, new SimRng(level.Map.Seed),
            level.Map.ForestRatio, level.Map.RockRatio, level.Map.WaterRatio);
        Assert.Equal(replay.ComputeMapHash(), level.TerrainHash);
    }

    [Fact]
    public void Different_seeds_generate_distinct_maps()
    {
        // legal — 异 seed 相异：100 个 seed 的地形哈希两两互异
        //（同 seed 同图的对偶面）。
        var hashes = new HashSet<long>();
        for (long seed = 1; seed <= 100; seed++)
        {
            Assert.True(hashes.Add(Generate(seed).TerrainHash), $"seed={seed} 与此前某 seed 地形重复");
        }
    }

    // ---- 威胁度标定与结构带（对齐手工关） -----------------------------------

    [Fact]
    public void Generated_rows_carry_the_formula_threat_and_win_waves()
    {
        // legal — 威胁度标定：产物威胁/胜波=§7 公式（10+⌊t×10⌋），
        // 宽限落非教学带 ×1.5~2，预算=档位行值。
        ProceduralCampaignEntry entry = Load();
        foreach (ProceduralTier tier in entry.Tiers)
        {
            ProceduralLevel level = Generate(777, tier);
            Assert.Equal(tier.Threat, level.Threat, 5);
            Assert.Equal(EraCore.Balance.Campaign.WinWaves(level.Threat), level.WinWaves);
            Assert.InRange(level.GraceMultiplier, 1.5, 2.0);
            Assert.Equal(tier.BudgetSeconds, level.BudgetSeconds);
            Assert.Equal(tier.AdjacentEnemyCamps, level.AdjacentEnemyCamps);
        }
    }

    [Fact]
    public void Generated_layouts_stay_inside_the_constraint_bands()
    {
        // legal — 资源配比带：尺寸/森林比/出生点数/宽限全部落约束带内
        //（25 seed 抽样）。
        ProceduralConstraints c = Load().Constraints;
        for (long seed = 500; seed < 525; seed++)
        {
            ProceduralLevel level = Generate(seed);
            Assert.InRange(level.Map.Width, c.WidthMin, c.WidthMax);
            Assert.InRange(level.Map.Height, c.HeightMin, c.HeightMax);
            Assert.InRange(level.Map.ForestRatio, c.ForestRatioMin, c.ForestRatioMax);
            Assert.InRange(level.Map.EntryCells.Count, c.EntryCountMin, c.EntryCountMax);
            Assert.InRange(level.GraceMultiplier, c.GraceMin, c.GraceMax);
        }
    }

    [Fact]
    public void Drawn_forest_share_stays_near_the_requested_ratio()
    {
        // legal — 抽样森林占比带：逐格实绘占比与请求比例偏差 ≤0.05
        //（384+ 格二项分布的诚实统计界）。
        for (long seed = 900; seed < 910; seed++)
        {
            ProceduralLevel level = Generate(seed);
            var world = new GridWorld(
                level.Map.Width, level.Map.Height, new SimRng(level.Map.Seed),
                level.Map.ForestRatio, level.Map.RockRatio, level.Map.WaterRatio);
            int forestCells = 0;
            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.TerrainAt(new CellCoord(x, y)) == TerrainKind.Forest)
                    {
                        forestCells++;
                    }
                }
            }

            double share = forestCells / (double)(world.Width * world.Height);
            Assert.InRange(share, level.Map.ForestRatio - 0.05, level.Map.ForestRatio + 0.05);
        }
    }

    // ---- 出生点→基地可达性（流场复用） --------------------------------------

    [Fact]
    public void Every_entry_reaches_the_base_on_the_drawn_terrain()
    {
        // legal — 可达性闸：产物每个出生点在实绘地形上以真流场可达基地
        //（C-11 复用，非 mock；出生点不与基地重合、两两互异）。
        for (long seed = 700; seed < 710; seed++)
        {
            ProceduralLevel level = Generate(seed);
            var world = new GridWorld(
                level.Map.Width, level.Map.Height, new SimRng(level.Map.Seed),
                level.Map.ForestRatio, level.Map.RockRatio, level.Map.WaterRatio);
            Assert.Equal(level.TerrainHash, world.ComputeMapHash());

            var baseCell = new CellCoord(level.Map.BaseCell.X, level.Map.BaseCell.Y);
            FlowField flow = FlowField.BuildComplete(world, baseCell);
            foreach (CellCoord entry in level.Map.EntryCells)
            {
                Assert.NotEqual(baseCell, entry);
                Assert.True(world.IsPassable(entry), $"seed={seed} 出生点落阻隔格 {entry}");
                Assert.True(flow.IsReachable(entry), $"seed={seed} 出生点 {entry} 不可达基地");
            }

            Assert.True(world.IsPassable(level.Map.WarehouseCell));
            Assert.All(level.CommandScript, step => Assert.True(step.Cell is null || world.IsPassable(
                new CellCoord(step.Cell.X, step.Cell.Y))));
        }
    }

    [Fact]
    public void Initial_stock_covers_the_full_tower_plan_with_margin()
    {
        // legal — 资源配平：初始资源=塔线总价×档位乘数（上取整）——
        // 全计划可由初始资源独立承担（中途收入是裕度不是依赖）。
        ProceduralCampaignEntry entry = Load();
        ProceduralEraContent pack = Era1Pack();
        foreach (ProceduralTier tier in entry.Tiers)
        {
            ProceduralLevel level = Generate(424242, tier);
            double towerTotal = (tier.ArrowTowerCount * pack.TowerCost(tier.ArrowTowerId)!.Value)
                + (tier.RockTowerCount * pack.TowerCost(tier.RockTowerId)!.Value);
            Assert.Equal(
                Math.Ceiling(towerTotal * tier.StockMultiplier),
                level.InitialStock.Single(line => line.Resource == pack.CurrencyResource).Amount);
            Assert.True(level.InitialStock[0].Amount >= towerTotal);
        }
    }

    // ---- 确定性重摇链（不可玩生成=重摇） -------------------------------------

    [Fact]
    public void Water_bands_trigger_deterministic_rerolls()
    {
        // legal — 重摇链：水域带（water 0.30~0.32——部分出生点被水隔绝）
        // 下存在确定性重摇 seed（attempt>0 过闸），且同 seed 双跑重摇计数
        // 与产物全等（重摇链自身确定）。
        var watery = Load().Constraints with { WaterRatioMin = 0.30, WaterRatioMax = 0.32 };
        long rerolledSeed = FindRerolledSeed(watery);
        ProceduralLevel first = Generate(rerolledSeed, constraints: watery);
        Assert.True(first.Rerolls > 0, $"seed={rerolledSeed} 应为重摇产物");

        ProceduralLevel second = Generate(rerolledSeed, constraints: watery);
        Assert.Equal(first.Rerolls, second.Rerolls);
        Assert.Equal(first.TerrainHash, second.TerrainHash);
        Assert.Equal(first.ToLevelJson(), second.ToLevelJson());
    }

    [Fact]
    public void Rerolled_levels_still_pass_the_reachability_gate()
    {
        // legal — 重摇产物可玩：水域带下重摇出的关卡同样全出生点可达
        //（重摇不是把闸放宽——是换一张过闸的图）。
        var watery = Load().Constraints with { WaterRatioMin = 0.30, WaterRatioMax = 0.32 };
        for (long seed = 1; seed <= 12; seed++)
        {
            ProceduralLevel level = Generate(seed, constraints: watery);
            var world = new GridWorld(
                level.Map.Width, level.Map.Height, new SimRng(level.Map.Seed),
                level.Map.ForestRatio, level.Map.RockRatio, level.Map.WaterRatio);
            var baseCell = new CellCoord(level.Map.BaseCell.X, level.Map.BaseCell.Y);
            FlowField flow = FlowField.BuildComplete(world, baseCell);
            Assert.All(level.Map.EntryCells, entry => Assert.True(flow.IsReachable(entry)));
        }
    }

    [Fact]
    public void Exhausted_reroll_budget_throws_honestly()
    {
        // illegal — 重摇预算耗尽：MaxRerolls=0 且首摇必败（高水域+单出生
        // 点带——确定性 pinned seed）→ ProceduralGenerationException，
        // 绝不静默降级出不可玩图。
        var hostile = Load().Constraints with
        {
            WaterRatioMin = 0.42,
            WaterRatioMax = 0.44,
            EntryCountMin = 4,
            EntryCountMax = 4,
            MaxRerolls = 0,
        };
        long doomed = FindDoomedSeed(hostile);
        var ex = Assert.Throws<ProceduralGenerationException>(
            () => Generate(doomed, constraints: hostile));
        Assert.Equal(1, ex.Attempts);
    }

    [Fact]
    public void Null_request_or_factory_is_rejected()
    {
        // illegal — 入参守卫。
        Assert.Throws<ArgumentNullException>(
            () => ProceduralLevelGenerator.Generate(null!, _ => new SimRng(1)));
        Assert.Throws<ArgumentNullException>(
            () => ProceduralLevelGenerator.Generate(
                new ProceduralRequest(1, Load().DefaultTier(), Era1Pack(), Load().Constraints), null!));
    }

    // ---- 搜索工具（写测时定钉；确定性保证同 seed 每次运行同一结论） ---------

    private static long FindRerolledSeed(ProceduralConstraints constraints)
    {
        for (long seed = 1; seed <= 64; seed++)
        {
            if (Generate(seed, constraints: constraints).Rerolls > 0)
            {
                return seed;
            }
        }

        throw new InvalidOperationException("水域带 64 seed 内未找到重摇样本——带参数失效");
    }

    private static long FindDoomedSeed(ProceduralConstraints constraints)
    {
        for (long seed = 1; seed <= 64; seed++)
        {
            try
            {
                Generate(seed, constraints: constraints);
            }
            catch (ProceduralGenerationException ex)
            {
                if (ex.Attempts == 1)
                {
                    return seed;
                }
            }
        }

        throw new InvalidOperationException("敌意带 64 seed 内未找到必败样本——带参数失效");
    }
}

/// <summary>Extensions consumed by the procedural suite (single purpose: default tier access).</summary>
internal static class ProceduralEntryTestExtensions
{
    /// <summary>The registry's default tier row (validated entry).</summary>
    public static ProceduralTier DefaultTier(this ProceduralCampaignEntry entry)
    {
        ProceduralTier? tier = entry.TryGetTier(entry.DefaultTierId);
        Assert.NotNull(tier);
        return tier!;
    }
}
