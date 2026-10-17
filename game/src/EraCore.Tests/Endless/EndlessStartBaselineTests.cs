using System.Text.Json;
using EraCore.Endless;
using EraCore.Tests.World;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Endless;

// [F-006][T-143] 任选时代深化：时代枚举从装载内容集动态读取（不硬编码
// 时代数——era5 合成集即可开局，T-134 落地零冲突）；起始时代影响初始资源/
// 解锁面（EndlessStartEraBaseline 冻结于配置）；真内容四时代基准（era.json
// 塔面 + levels/eN.json 首关 initial_stock）。
public class EndlessStartBaselineTests
{
    private const long Seed = 20261433;

    private static EnemySpec Enemy(string id, int hp, bool isBoss = false) =>
        new(id, $"sp-{id}", $"名-{id}", isBoss, hp, 0, 1.0, 0, isBoss ? 96 : 32);

    private static EndlessEraContent Content(
        int era,
        IReadOnlyList<EndlessResourceStock>? resources = null,
        IReadOnlyList<string>? towers = null) => new(
        era,
        new[] { Enemy($"era{era}:light", 100), Enemy($"era{era}:heavy", 900), Enemy($"era{era}:boss", 5000, true) },
        6, 1, 3, 1.0,
        resources,
        towers);

    // ------------------------------------------------ 基准记录守卫 -----

    [Fact] // illegal：空白菜单/重复资源/负数量/空塔面/重复塔拒绝
    public void Baseline_guards_reject_bad_rows()
    {
        Assert.Throws<ArgumentException>(
            () => new EndlessStartEraBaseline(1, new[] { new EndlessResourceStock(" ", 10) }, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(
            () => new EndlessStartEraBaseline(1, new[] { new EndlessResourceStock("r", 10), new EndlessResourceStock("r", 5) }, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(
            () => new EndlessStartEraBaseline(1, new[] { new EndlessResourceStock("r", -1) }, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(
            () => new EndlessStartEraBaseline(1, Array.Empty<EndlessResourceStock>(), new[] { " " }));
        Assert.Throws<ArgumentException>(
            () => new EndlessStartEraBaseline(1, Array.Empty<EndlessResourceStock>(), new[] { "t", "t" }));
    }

    [Fact] // illegal：配置守卫——基准时代与开局时代不一致拒绝
    public void Config_rejects_a_baseline_from_another_era()
    {
        Assert.Throws<ArgumentException>(
            () => new EndlessRunConfig(
                Seed, 1, 0.9, 6, 1, 3, 1.0, 10, EndlessFixture.MixedPool(),
                EndlessAffixTuning.Default, EndlessScoreWeights.Default, Array.Empty<string>(),
                EndlessParticipationMode.Manual,
                startBaseline: new EndlessStartEraBaseline(2, Array.Empty<EndlessResourceStock>(), Array.Empty<string>())));
    }

    // ------------------------------------------------ 时代枚举（动态）-----

    [Fact] // legal：时代枚举=装载集时代升序去重（今天四时代内容）
    public void Available_eras_follow_the_loaded_content_sets()
    {
        Assert.Equal(
            new[] { 1, 2, 3, 4 },
            EndlessEntry.AvailableStartEras(new[] { Content(4), Content(2), Content(1), Content(3) }));
        Assert.Equal(
            new[] { 2, 4 },
            EndlessEntry.AvailableStartEras(new[] { Content(4), Content(2), Content(4) })); // 去重
        Assert.Empty(EndlessEntry.AvailableStartEras(Array.Empty<EndlessEraContent>()));
    }

    [Fact] // legal：era5 合成集即可开局——时代数不落码面（T-134 落地零冲突证明）
    public void A_fifth_era_content_set_opens_endless_today()
    {
        EndlessSession session = EndlessEntry.Begin(
            new EndlessEntryRequest(
                Seed, 5, EndlessParticipationMode.Manual, 0.9,
                new[] { Content(5) }, Array.Empty<string>()),
            new LcgRng(Seed));

        Assert.Equal(5, session.Run.Config.StartEraIndex);
        Assert.Equal(new[] { 5 }, session.Run.Config.Pool.EraIndexes);
        EndlessFixture.PlayWaves(session.Run, 3); // E5 开局可玩
    }

    // ------------------------------------------------ 基准冻结（入口）-----

    [Fact] // legal：开局时代基准冻结于配置——初始资源+解锁面随时代走
    public void Entry_freezes_the_start_era_baseline()
    {
        var era1Resources = new[] { new EndlessResourceStock("era1:res-wood", 240.0) };
        var era1Towers = new[] { "era1:tower-wood-arrow" };
        var era2Resources = new[] { new EndlessResourceStock("era2:res-copper", 300.0) };
        var era2Towers = new[] { "era2:tower-spear" };
        EndlessSession session = EndlessEntry.Begin(
            new EndlessEntryRequest(
                Seed, 2, EndlessParticipationMode.Manual, 0.9,
                new[]
                {
                    Content(1, era1Resources, era1Towers),
                    Content(2, era2Resources, era2Towers),
                },
                Array.Empty<string>()),
            new LcgRng(Seed));

        EndlessStartEraBaseline baseline = session.Run.Config.StartEraBaseline!;
        Assert.Equal(2, baseline.EraIndex);
        Assert.Equal(era2Resources, baseline.InitialResources); // 开局时代=2 → 基准走 E2 面
        Assert.Equal(era2Towers, baseline.UnlockedTowers);
    }

    [Fact] // legal：基准缺席（默认空）合法——宿主自持开局面
    public void Entry_without_baseline_rows_starts_bare()
    {
        EndlessSession session = EndlessEntry.Begin(
            new EndlessEntryRequest(
                Seed, 1, EndlessParticipationMode.Manual, 0.9,
                new[] { Content(1) }, Array.Empty<string>()),
            new LcgRng(Seed));

        Assert.Empty(session.Run.Config.StartEraBaseline!.InitialResources);
        Assert.Empty(session.Run.Config.StartEraBaseline!.UnlockedTowers);
    }

    [Fact] // legal：基准入身份哈希——不同初始资源=不同运行身份
    public void The_baseline_joins_the_run_identity()
    {
        EndlessRunConfig Config(IReadOnlyList<EndlessResourceStock> rows)
        {
            EndlessEraContent content = Content(1, rows);
            return new EndlessRunConfig(
                Seed, 1, 0.9, 6, 1, 3, 1.0, 10, EndlessFixture.MixedPool(),
                EndlessAffixTuning.Default, EndlessScoreWeights.Default, Array.Empty<string>(),
                EndlessParticipationMode.Manual,
                startBaseline: new EndlessStartEraBaseline(1, rows, Array.Empty<string>()));
        }

        var rich = new EndlessRun(Config(new[] { new EndlessResourceStock("era1:res-wood", 240.0) }), new LcgRng(Seed));
        var bare = new EndlessRun(Config(Array.Empty<EndlessResourceStock>()), new LcgRng(Seed));
        EndlessFixture.PlayWaves(rich, 4);
        EndlessFixture.PlayWaves(bare, 4);

        Assert.NotEqual(rich.ComputeStateHash(), bare.ComputeStateHash());
    }

    // ------------------------------------------------ 真内容基准（era1~era4）-----

    /// <summary>真 era 塔面（game/content/era{N}/era.json towers 行，仓库相对读取 RL9）.</summary>
    private static IReadOnlyList<string> EraTowers(string eraDir)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", eraDir);
            if (Directory.Exists(probe))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(probe, "era.json")));
                return doc.RootElement.GetProperty("items").EnumerateArray().Single()
                    .GetProperty("towers").EnumerateArray()
                    .Select(row => row.GetString()!).ToList();
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/{eraDir}（测试须在仓库内运行）");
    }

    [Theory] // legal：四时代真内容开局——基准塔面=era.json towers 行（起始时代影响解锁面）
    [InlineData("era1", 1)]
    [InlineData("era2", 2)]
    [InlineData("era3", 3)]
    [InlineData("era4", 4)]
    public void Real_content_opens_each_era_with_its_true_tower_surface(string eraDir, int era)
    {
        IReadOnlyList<string> towers = EraTowers(eraDir);
        Assert.NotEmpty(towers);

        EndlessSession session = EndlessEntry.Begin(
            new EndlessEntryRequest(
                Seed, era, EndlessParticipationMode.Manual, 0.9,
                new[] { Content(era, towers: towers) }, Array.Empty<string>()),
            new LcgRng(Seed));

        Assert.Equal(era, session.Run.Config.StartEraBaseline!.EraIndex);
        Assert.Equal(towers, session.Run.Config.StartEraBaseline!.UnlockedTowers);
        Assert.All(towers, tower => Assert.StartsWith($"era{era}:", tower)); // 每代塔面前缀对齐
    }
}
