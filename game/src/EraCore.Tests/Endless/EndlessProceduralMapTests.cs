using EraCore.Contracts.Foundation;
using EraCore.Endless;
using EraCore.Procedural;
using EraCore.Units;
using EraCore.Tests.World;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelContent;

namespace EraCore.Tests.Endless;

// [C-19][F-007] T-140 无尽可选「程序图」开关（Endless 只读消费）：开关冻结入
// 配置+身份哈希；波循环零接触（名册/波形与开关正交——宿主侧安放程序图，
// 同 T-143 奇观带入「引擎不复制效果面」先例）；缺省 false 存量行为零漂移。
public class EndlessProceduralMapTests
{
    private const long Seed = 20261081;

    private static IReadOnlyList<EnemySpec> Rows(int era) =>
        Enemies($"era{era}").Values.OrderBy(spec => spec.Id, StringComparer.Ordinal).ToList();

    private static EndlessEraContent EraContent(int era) => new(
        era, Rows(era), 6, 1, 3, 1.0);

    private static EndlessEntryRequest Request(bool proceduralMap) => new(
        Seed,
        StartEraIndex: 1,
        Participation: EndlessParticipationMode.Manual,
        ThreatLevel: 0.9,
        EraContent: new[] { EraContent(1), EraContent(2), EraContent(3) },
        WonderIds: Array.Empty<string>(),
        ProceduralMap: proceduralMap);

    [Fact]
    public void Default_is_off_and_the_flag_freezes_into_the_config()
    {
        // legal — 缺省关（存量调用零影响）；显式开=配置冻结真。
        var off = EndlessEntry.Begin(Request(proceduralMap: false), new LcgRng(Seed));
        Assert.False(off.Run.Config.ProceduralMap);

        var on = EndlessEntry.Begin(Request(proceduralMap: true), new LcgRng(Seed));
        Assert.True(on.Run.Config.ProceduralMap);
    }

    [Fact]
    public void Entry_request_defaults_the_switch_off()
    {
        // boundary — 请求缺省位=false（新参数不改变既有构造面）。
        Assert.False(new EndlessEntryRequest(
            Seed, 1, EndlessParticipationMode.Manual, 0.9,
            new[] { EraContent(1) }, Array.Empty<string>()).ProceduralMap);
    }

    [Fact]
    public void The_flag_joins_the_identity_hash()
    {
        // legal — 开关入身份哈希：同 seed 同 rng 同波流，开关不同 ⇒ 哈希不同；
        // 开关相同 ⇒ 哈希全等（台账面：程序图局与固定图局不同身）。
        long withOff = RunHashes(Request(proceduralMap: false));
        long withOn = RunHashes(Request(proceduralMap: true));
        Assert.NotEqual(withOff, withOn);
        Assert.Equal(withOn, RunHashes(Request(proceduralMap: true)));
    }

    [Fact]
    public void Wave_planning_is_orthogonal_to_the_flag()
    {
        // legal — 只读消费：同 rng 下开关开/关的名册与波形逐波一致
        //（Endless 波循环无地图域——程序图安放归宿主，引擎面零接触）。
        var off = EndlessEntry.Begin(Request(proceduralMap: false), new LcgRng(Seed));
        var on = EndlessEntry.Begin(Request(proceduralMap: true), new LcgRng(Seed));
        for (int wave = 1; wave <= 25; wave++)
        {
            EndlessWavePlan left = EndlessRules.PlanWave(wave, off.Run.Config, new LcgRng(Seed + wave));
            EndlessWavePlan right = EndlessRules.PlanWave(wave, on.Run.Config, new LcgRng(Seed + wave));
            Assert.Equal(left.RegularCount, right.RegularCount);
            Assert.Equal(left.IntervalSeconds, right.IntervalSeconds, 5);
            Assert.Equal(
                left.Roster.Select(spawn => (spawn.EnemyId, spawn.EraIndex, spawn.Role)),
                right.Roster.Select(spawn => (spawn.EnemyId, spawn.EraIndex, spawn.Role)));
        }
    }

    [Fact]
    public void Host_side_map_seed_derives_from_the_run_seed_deterministically()
    {
        // legal — 宿主安放面：run seed → 程序图 seed 派生（ProceduralSeed 域
        // 标签隔离），确定性双调全等；与无尽主随机流正交（派生不吃 IRng）。
        Assert.Equal(
            ProceduralSeed.Derive(Seed, "endless-map", 0),
            ProceduralSeed.Derive(Seed, "endless-map", 0));
        Assert.NotEqual(
            ProceduralSeed.Derive(Seed, "endless-map", 0),
            ProceduralSeed.Derive(Seed, "endless-map", 1));
        Assert.NotEqual(
            ProceduralSeed.Derive(Seed, "endless-map", 0),
            ProceduralSeed.Derive(Seed, "terrain", 0));
    }

    private static long RunHashes(EndlessEntryRequest request)
    {
        var session = EndlessEntry.Begin(request, new LcgRng(Seed));
        for (int wave = 1; wave <= 5; wave++)
        {
            session.Run.BeginWave();
            session.Run.CompleteWave();
        }

        return session.Run.ComputeStateHash();
    }
}
