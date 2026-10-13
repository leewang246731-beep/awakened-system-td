using EraCore.Checkpoint;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Checkpoint;

// [C-04][F-004] 演习模式（T-057）：敌人 ×0.5 标志位进 seed 参数——演习运行
// 用派生 seed（标志混入重放键），敌量乘子经波配置 ×0.5 落地。
public class ExerciseModeTests
{
    // ------------------------------------------------------------ legal

    [Fact] // legal — 派生确定性：同关卡 seed 必得同演习 seed，且 ≠ 关卡 seed
    public void Derived_run_seed_is_deterministic_and_never_the_level_seed()
    {
        ExerciseRunParameters first = ExerciseMode.DeriveRunParameters(SimE1Fixture.Seed);
        ExerciseRunParameters second = ExerciseMode.DeriveRunParameters(SimE1Fixture.Seed);

        Assert.Equal(SimE1Fixture.Seed, first.LevelSeed);
        Assert.Equal(first.RunSeed, second.RunSeed); // 确定性：无存档状态
        Assert.NotEqual(SimE1Fixture.Seed, first.RunSeed); // 标志位真的进了 seed 参数
        Assert.Equal(ExerciseMode.EnemyCountMultiplier, first.EnemyCountMultiplier);

        // 不同关卡派生不同演习 seed（无碰撞面）
        Assert.NotEqual(first.RunSeed, ExerciseMode.DeriveRunParameters(SimE1Fixture.Seed + 1).RunSeed);
    }

    [Fact] // legal — 敌量 ×0.5 经波配置乘子落地：Scaling 减半、威胁档不动
    public void EnemyHalved_scales_the_count_multiplier_only()
    {
        SimulationConfig original = SimE1Fixture.Config();
        SimulationConfig exercised = ExerciseMode.EnemyHalved(original);

        Assert.Equal(original.Waves.Scaling * ExerciseMode.EnemyCountMultiplier, exercised.Waves.Scaling, 6);
        Assert.Equal(original.Waves.ThreatLevel, exercised.Waves.ThreatLevel); // hp/间隔不属于演习折扣
        Assert.Equal(original.Width, exercised.Width);
        Assert.Equal(original.TowerCatalog.Count, exercised.TowerCatalog.Count);
    }

    [Fact] // legal — 标志位进重放键：演习局与真局整体 StateHash 分叉、演习局自身可重放
    public void Exercise_run_replay_key_differs_from_the_real_run()
    {
        ExerciseRunParameters parameters = ExerciseMode.DeriveRunParameters(SimE1Fixture.Seed);
        SimulationConfig exercised = ExerciseMode.EnemyHalved(SimE1Fixture.Config());
        const long ticks = 900;

        long realHash = HashAt(SimE1Fixture.Config(), SimE1Fixture.Seed, ticks);
        long exerciseHash = HashAt(exercised, parameters.RunSeed, ticks);
        long exerciseReplay = HashAt(exercised, parameters.RunSeed, ticks); // 独立重放

        Assert.NotEqual(realHash, exerciseHash); // 演习≠真局：seed 参数携带标志位
        Assert.Equal(exerciseHash, exerciseReplay); // 演习局自身确定性成立
    }

    // ----------------------------------------------------------- illegal

    [Fact] // illegal — 空配置拒绝
    public void Null_config_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ExerciseMode.EnemyHalved(null!));
    }

    // ------------------------------------------------------------ helpers

    private static long HashAt(SimulationConfig config, long seed, long ticks)
    {
        var simulation = new Simulation(config, new SimRng(seed), seed);
        simulation.Run(ticks);
        return simulation.ComputeStateHash();
    }
}
