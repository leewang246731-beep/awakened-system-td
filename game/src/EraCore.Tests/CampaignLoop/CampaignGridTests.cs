using EraCore.Balance;
using EraCore.CampaignLoop;
using EraCore.Economy;
using EraCore.Tests.Sim;
using Xunit;
using static EraCore.Tests.CampaignLoop.CampaignLoopFixture;

namespace EraCore.Tests.CampaignLoop;

// 选图薄版大陆网格（T-059）：seed 确定性生成、威胁度=邻接敌营公式（v3.2
// §7）、只能选相邻已占格（4 邻接）、占领奖励入池（上限执法）——CS-3.1 三行
// legal/boundary/illegal 全覆盖；胜波/威胁全部经 Balance 公式断言，禁手填
// （CS-4.4）。[C-12][F-005]
public class CampaignGridTests
{
    // ---------------------------------------------------------- legal

    [Fact] // legal — 同 seed 同布局：两次独立生成逐格一致（seed 化）
    public void Same_seed_regenerates_the_identical_layout()
    {
        CampaignGrid first = NewCampaign().Grid;
        CampaignGrid second = NewCampaign().Grid;

        for (int y = 0; y < GridHeight; y++)
        {
            for (int x = 0; x < GridWidth; x++)
            {
                Assert.Equal(first.TryGetCell(x, y)!.Kind, second.TryGetCell(x, y)!.Kind);
            }
        }

        Assert.Equal(first.Home, second.Home);
        Assert.Equal(first.OccupiedCells, second.OccupiedCells);
    }

    [Fact] // legal — 威胁度=clamp(0.3+0.3×邻接敌营)（v3.2 §7 公式链），胜波=10+⌊t×10⌋
    public void Every_level_node_derives_threat_from_adjacent_camps()
    {
        CampaignGrid grid = NewCampaign().Grid;

        for (int y = 0; y < GridHeight; y++)
        {
            for (int x = 0; x < GridWidth; x++)
            {
                CampaignCell? cell = grid.TryGetCell(x, y);
                if (cell is not { Kind: CampaignCellKind.Level })
                {
                    continue;
                }

                LevelNodeInfo node = grid.NodeInfo(x, y);
                Assert.Equal(Threat.ClampThreat(node.AdjacentEnemyCamps), node.Threat);
                Assert.Equal(Campaign.WinWaves(node.Threat), node.WinWaves);
                Assert.InRange(node.Threat, Threat.Min, Threat.Max);
            }
        }
    }

    [Fact] // legal — 只能选相邻已占格：开局仅主城四邻域中的关卡格可选
    public void Initial_selection_is_the_occupied_home_ring()
    {
        CampaignGrid grid = NewCampaign().Grid;

        foreach (CampaignCell selectable in grid.SelectableCells())
        {
            Assert.Equal(CampaignCellKind.Level, selectable.Kind);
            Assert.False(grid.IsOccupied(selectable.X, selectable.Y));
            Assert.Contains(
                CampaignGrid.NeighbourOffsets,
                offset => grid.IsOccupied(selectable.X + offset.Dx, selectable.Y + offset.Dy));
        }

        // Home itself is occupied and NOT selectable.
        Assert.True(grid.IsOccupied(grid.Home.X, grid.Home.Y));
        Assert.False(grid.CanSelect(grid.Home.X, grid.Home.Y));
    }

    [Fact] // legal — 占领入库：奖励=每胜波×WinWaves 存入战役池（入池只计上限内）
    public void Occupy_banks_the_reward_into_the_campaign_pool()
    {
        CampaignState campaign = NewCampaign();
        CampaignGrid grid = campaign.Grid;
        LevelNodeInfo node = CheapestSelectableNode(grid);
        double poolBefore = campaign.Pool.AmountOf(Wood);

        OccupyOutcome outcome = grid.Occupy(node.Cell.X, node.Cell.Y);

        Assert.Equal(node, outcome.Node);
        ResourceGrant reward = Assert.Single(outcome.Rewards);
        Assert.Equal(Wood, reward.Resource);
        Assert.Equal(RewardPerWinWave * node.WinWaves, reward.Granted, 9); // 容差=double 乘法舍入
        Assert.Equal(reward.Granted, reward.Accepted, 9);                  // 池远未满：全额入库
        Assert.Equal(0.0, reward.Refused, 9);
        Assert.Equal(poolBefore + reward.Accepted, campaign.Pool.AmountOf(Wood), 9);
        Assert.True(grid.IsOccupied(node.Cell.X, node.Cell.Y));
        Assert.Equal(node.Cell, grid.OccupiedCells[^1]); // 占领顺序入列（主城之后）
        Assert.Contains( // 新占领格的邻域现在可选（选下一张图）
            grid.SelectableCells(),
            candidate => Math.Abs(candidate.X - node.Cell.X) + Math.Abs(candidate.Y - node.Cell.Y) == 1);
    }

    // ---------------------------------------------------------- boundary

    [Fact] // boundary — 池上限执法：占领奖励只入上限内，超出部分拒收并留痕
    public void Occupy_reward_is_capped_at_pool_headroom()
    {
        CampaignState campaign = NewCampaign();
        CampaignGrid grid = campaign.Grid;
        LevelNodeInfo node = CheapestSelectableNode(grid);

        // Fill the pool to one item below the cap: the reward must accept
        // exactly the headroom and refuse the rest (入池只计上限内).
        double headroom = campaign.Pool.Capacity - 1;
        Assert.True(headroom > 0);
        Assert.Equal(headroom, campaign.Pool.Deposit(Wood, headroom), 9);

        OccupyOutcome outcome = grid.Occupy(node.Cell.X, node.Cell.Y);
        ResourceGrant reward = Assert.Single(outcome.Rewards);

        Assert.Equal(1.0, reward.Accepted, 9);
        Assert.Equal(reward.Granted - 1.0, reward.Refused, 9);
        Assert.Equal(campaign.Pool.Capacity, campaign.Pool.AmountOf(Wood), 9);
        Assert.Equal(1, campaign.Pool.OverflowCount); // 拒收留痕（F-024 观测面）
    }

    [Fact] // boundary — 比率和恰为 1（无空格）合法；3×3 最小网格合法
    public void Boundary_ratios_and_minimum_grid_are_accepted()
    {
        var pool = FreshPool();
        var grid = new CampaignGrid(3, 3, new SimRng(1), 1L, pool, new CampaignGridOptions(0.5, 0.5));
        Assert.Equal(3, grid.Width);
        Assert.Equal(3, grid.Height);

        var full = new CampaignGrid(5, 5, new SimRng(2), 2L, FreshPool(), new CampaignGridOptions(0.6, 0.4));
        Assert.NotNull(full);
    }

    // ---------------------------------------------------------- illegal

    [Theory] // illegal — 构造参数越界确实被拒（具体异常断言）
    [InlineData(2, 7)]   // 宽 < 3
    [InlineData(7, 2)]   // 高 < 3
    public void Constructor_rejects_undersized_grid(int width, int height)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => new CampaignGrid(width, height, new SimRng(1), 1L, FreshPool()));
        Assert.Contains("3×3", ex.Message);
    }

    [Theory] // illegal — 比例越界：非正或和 > 1
    [InlineData(0.0, 0.4)]
    [InlineData(0.3, 0.0)]
    [InlineData(0.6, 0.5)]
    [InlineData(-0.1, 0.4)]
    public void Constructor_rejects_out_of_band_ratios(double camp, double level)
    {
        Assert.Throws<ArgumentException>(
            () => new CampaignGrid(7, 7, new SimRng(1), 1L, FreshPool(), new CampaignGridOptions(camp, level)));
    }

    [Fact] // illegal — 非法选择逐类确实被拒：已占格/不相邻关格/敌营格/主城重复占领
    public void Non_selectable_cells_reject_occupation()
    {
        CampaignGrid grid = NewCampaign().Grid;

        Assert.Throws<InvalidOperationException>(() => grid.Occupy(grid.Home.X, grid.Home.Y)); // 已占

        CampaignCell far = FarLevelCell(grid);
        Assert.NotNull(far);
        Assert.Throws<InvalidOperationException>(() => grid.Occupy(far.X, far.Y)); // 不相邻已占格

        CampaignCell camp = EnemyCampCell(grid);
        Assert.Throws<InvalidOperationException>(() => grid.Occupy(camp.X, camp.Y)); // 敌营不可占领
    }

    [Fact] // illegal — 网格外坐标与非法节点查询确实被拒
    public void Out_of_grid_and_non_level_queries_are_rejected()
    {
        CampaignGrid grid = NewCampaign().Grid;

        Assert.Throws<ArgumentException>(() => grid.NodeInfo(-1, 0));
        Assert.Throws<ArgumentException>(() => grid.Occupy(GridWidth, 0));
        Assert.False(grid.CanSelect(GridWidth, 0));
        Assert.Null(grid.TryGetCell(0, -1));

        CampaignCell camp = EnemyCampCell(grid);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => grid.NodeInfo(camp.X, camp.Y));
        Assert.Contains(CampaignCellKind.EnemyCamp.ToString(), ex.Message);
    }

    [Fact] // illegal — 负奖励单价确实被拒
    public void Constructor_rejects_negative_reward_per_win_wave()
    {
        Assert.Throws<ArgumentException>(
            () => new CampaignGrid(
                7, 7, new SimRng(1), 1L, FreshPool(),
                new CampaignGridOptions(0.25, 0.45, RewardResource: Wood, RewardPerWinWave: -1.0)));
    }

    /// <summary>A level cell NOT 4-adjacent to any occupied cell (grid interior beyond the home ring).</summary>
    private static CampaignCell FarLevelCell(CampaignGrid grid)
    {
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                if (!grid.CanSelect(x, y) && grid.TryGetCell(x, y) is { Kind: CampaignCellKind.Level })
                {
                    return new CampaignCell(x, y, CampaignCellKind.Level);
                }
            }
        }

        throw new InvalidOperationException("Fixture seed produced no far level cell — adjust the seed.");
    }

    /// <summary>The first enemy-camp cell of the seeded layout.</summary>
    private static CampaignCell EnemyCampCell(CampaignGrid grid)
    {
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                if (grid.TryGetCell(x, y) is { Kind: CampaignCellKind.EnemyCamp } camp)
                {
                    return camp;
                }
            }
        }

        throw new InvalidOperationException("Fixture seed produced no enemy camp — adjust the seed.");
    }
}
