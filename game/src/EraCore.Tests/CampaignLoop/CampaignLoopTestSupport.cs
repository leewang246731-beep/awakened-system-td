using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Economy;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.CampaignLoop;

// T-059 (C-12) 测试基建：战役环共享夹具——威胁驱动的 E1 关卡配置（薄敌量
// 长地平线口径，同 CheckpointFixture.LongHorizonConfig 先例）与大陆网格
// 构造参数。全部数值来自 content JSON、已冻结公式或本夹具显式登记的结构
// 旋钮（CS-2.3/CS-4.4：威胁度→胜波/波间隔链全部经 Balance 公式调用推导，
// 禁手填波数/间隔值）。

/// <summary>
/// Shared fixture of the campaign-loop suite: threat-parameterized E1 level
/// configs (everything derived from the frozen <see cref="EraCore.Balance.Campaign"/>
/// formulas and the era1 content the Sim fixture loads) plus continent-grid
/// knobs. Wave-count/interval numbers are ALWAYS formula calls.
/// </summary>
internal static class CampaignLoopFixture
{
    public const int GridWidth = 7;
    public const int GridHeight = 7;
    public const long WorldSeed = 20261013; // same seed family as the sim/economy fixtures

    /// <summary>
    /// Level seed of every loop fixture: the Sim E1 fixture seed — the
    /// milestone placement coordinates are tuned against THIS terrain draw
    /// (water 0, forest layout), so the full command chain applies.
    /// </summary>
    public const long LevelSeed = SimE1Fixture.Seed;
    public const string LoopSlotId = "level.e1.loop01";

    /// <summary>Occupy-reward policy of the suite: 10 wood per win wave (fixture knob, not a v3.2 number).</summary>
    public const double RewardPerWinWave = 10.0;

    public static readonly ResourceKey Wood = SimE1Fixture.Wood;

    /// <summary>
    /// E1 level config at node threat <paramref name="threat"/>: same map,
    /// roster, command plan and roster base as the Sim E1 fixture
    /// (threat 0.3's count multiplier is 0.5 — thinning the base below 10
    /// would floor whole waves to zero enemies and starve the victory
    /// predicate), BatchesPerWave 1 (the whole wave enters at its start
    /// tick — spike1-report §2 precedent — so the win wave's "fully
    /// entered" tick is exact).
    /// </summary>
    public static SimulationConfig LevelConfig(double threat) => SimE1Fixture.Config() with
    {
        Waves = SimE1Fixture.Config().Waves with
        {
            ThreatLevel = threat,
            BatchesPerWave = 1,
        },
    };

    /// <summary>
    /// Wave interval in ticks at <paramref name="threat"/> (K-1 30 Hz ×
    /// <see cref="EraCore.Balance.Campaign.WaveIntervalSeconds"/>).
    /// </summary>
    public static long IntervalTicks(double threat) => (long)Math.Round(
        EraCore.Balance.Campaign.WaveIntervalSeconds(threat) * Simulation.TicksPerSecond,
        MidpointRounding.AwayFromZero);

    /// <summary>First-wave grace ticks at <paramref name="threat"/> (×1.5 default, F-034 band lower edge).</summary>
    public static long GraceTicks(double threat) => (long)Math.Round(
        IntervalTicks(threat) * 1.5, MidpointRounding.AwayFromZero);

    /// <summary>Tick the win wave starts at: grace + (winWaves−1)×interval.</summary>
    public static long WinWaveStartTick(double threat) =>
        GraceTicks(threat) + ((EraCore.Balance.Campaign.WinWaves(threat) - 1) * IntervalTicks(threat));

    /// <summary>
    /// Victory horizon at <paramref name="threat"/>: win-wave start plus one
    /// full interval of engagement slack (the thinned wave dies long before
    /// the interval elapses; the slack only bounds the loop, victory fires
    /// on the predicate).
    /// </summary>
    public static long VictoryHorizonTicks(double threat) => WinWaveStartTick(threat) + IntervalTicks(threat);

    /// <summary>A campaign pool at E1 warehouse level 1 (fresh stand).</summary>
    public static ResourceStock FreshPool() => new(new EraCore.Sim.SimClock(), ResourceStock.E1WarehouseLevel);

    /// <summary>Campaign state over the fixture grid knobs.</summary>
    public static CampaignState NewCampaign(double campRatio = 0.25, double levelRatio = 0.45) => new(
        WorldSeed,
        new SimRng(WorldSeed),
        GridWidth,
        GridHeight,
        new CampaignGridOptions(campRatio, levelRatio, RewardResource: Wood, RewardPerWinWave: RewardPerWinWave));

    /// <summary>
    /// The selectable node with the fewest adjacent camps (fastest win
    /// horizon); ties break row-major — deterministic for the seed.
    /// </summary>
    public static LevelNodeInfo CheapestSelectableNode(CampaignGrid grid)
    {
        Assert.NotEmpty(grid.SelectableCells());
        CampaignCell best = grid.SelectableCells()
            .OrderBy(node => grid.NodeInfo(node.X, node.Y).AdjacentEnemyCamps)
            .ThenBy(node => node.Y)
            .ThenBy(node => node.X)
            .First();
        return grid.NodeInfo(best.X, best.Y);
    }
}
