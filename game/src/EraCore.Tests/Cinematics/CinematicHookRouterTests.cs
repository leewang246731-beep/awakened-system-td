using EraCore.Cinematics;
using Xunit;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-113] [F-114] T-086 触发钩子：真实关卡文件钩子行解析（e1.json
// 第 2 波啊哈位+胜利交底位；e2.json level_start 觉醒位）、beat 路由（波次
// 精确匹配/会话单发不重复）、接线失败即拒绝（未知钩子/重复认领）、
// 钩子 schema 违规（未登记 beat/wave_start 缺 wave/重复 id）。
public class CinematicHookRouterTests
{
    private static IReadOnlyList<CinematicHookRow> RepoHookRows()
    {
        var rows = new List<CinematicHookRow>(CinematicHookRouter.ParseLevelHooks(
            CinematicContent.LevelJson("e1"), "levels/e1.json"));
        rows.AddRange(CinematicHookRouter.ParseLevelHooks(
            CinematicContent.LevelJson("e2"), "levels/e2.json"));
        return rows;
    }

    private static CinematicHookRouter RepoRouter() =>
        new(RepoHookRows(), new[] { CinematicContent.E1Aha(), CinematicContent.E2Awaken() });

    // ---------------------------------------------------------------- legal

    [Fact] // legal — e1.json 钩子行：L1-1 两行（第 2 波啊哈 + 胜利交底），其余关不带钩子
    public void E1_level_file_parses_two_hook_rows_from_the_first_level_only()
    {
        IReadOnlyList<CinematicHookRow> rows = CinematicHookRouter.ParseLevelHooks(
            CinematicContent.LevelJson("e1"), "levels/e1.json");

        Assert.Equal(2, rows.Count);
        CinematicHookRow aha = Assert.Single(rows, row => row.Beat == CinematicBeat.WaveStart);
        Assert.Equal("campaign:hook-e1-ahwa-arrow-break", aha.HookId);
        Assert.Equal(2, aha.Wave); // §10.4 ① 第 2~3 波触发位
        CinematicHookRow handover = Assert.Single(rows, row => row.Beat == CinematicBeat.Victory);
        Assert.Equal("campaign:hook-e1-awakening-handover", handover.HookId);
        Assert.Null(handover.Wave);
    }

    [Fact] // legal — e2.json 钩子行：L2-1 一行 level_start（§10.4 ② 觉醒位）
    public void E2_level_file_parses_the_level_start_awakening_row()
    {
        IReadOnlyList<CinematicHookRow> rows = CinematicHookRouter.ParseLevelHooks(
            CinematicContent.LevelJson("e2"), "levels/e2.json");

        CinematicHookRow awaken = Assert.Single(rows);
        Assert.Equal(CinematicBeat.LevelStart, awaken.Beat);
        Assert.Equal("campaign:hook-e2-awakening-awaken", awaken.HookId);
    }

    [Fact] // legal — beat 路由：wave_start(2)→E1 啊哈；level_start→E2 觉醒；victory 无脚本→空
    public void Beats_route_to_the_bound_scripts()
    {
        CinematicHookRouter router = RepoRouter();

        Assert.Equal(new[] { "cinematic:e1-aha" }, router.OnWaveStart(2));
        Assert.Equal(new[] { "cinematic:e2-awaken" }, router.OnLevelStart());
        Assert.Empty(router.OnVictory()); // 胜利交底脚本属 T-089 叙事——钩子已就位，脚本未落
    }

    [Fact] // legal — 波次精确匹配：第 3 波（啊哈带外）与第 1 波都不触发
    public void Wave_matching_is_exact()
    {
        CinematicHookRouter router = RepoRouter();

        Assert.Empty(router.OnWaveStart(1));
        Assert.Empty(router.OnWaveStart(3));
        Assert.Empty(router.OnWaveStart(0));
    }

    [Fact] // legal — 会话单发：同一 beat 二次询问静默（逐拍观察不会双开演出）
    public void A_script_fires_once_per_session()
    {
        CinematicHookRouter router = RepoRouter();

        Assert.Single(router.OnWaveStart(2));
        Assert.Empty(router.OnWaveStart(2)); // 已发——不重复播放（会话面；跨运行面在 ProgressStore）
    }

    [Fact] // legal — MarkFired：显式回看后，同会话的钩子位保持静默（不双开）
    public void Explicit_launch_marks_the_script_fired_for_the_session()
    {
        CinematicHookRouter router = RepoRouter();
        router.MarkFired("cinematic:e1-aha");

        Assert.Empty(router.OnWaveStart(2));
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 无钩子行的关卡文件段：返回空集（合法缺省）
    public void A_level_without_hook_rows_yields_an_empty_set()
    {
        string json = """
            {"items":[{"id":"campaign:level-x","cinematic_hooks":[]}]}
            """;

        Assert.Empty(CinematicHookRouter.ParseLevelHooks(json, "inline"));
    }

    [Fact] // boundary — victory 钩子允许不带 wave（胜利与波号无关）
    public void Victory_rows_may_omit_the_wave_anchor()
    {
        string json = """
            {"items":[{"cinematic_hooks":[{"id":"h1","trigger":{"beat":"victory"}}]}]}
            """;

        CinematicHookRow row = Assert.Single(CinematicHookRouter.ParseLevelHooks(json, "inline"));
        Assert.Equal(CinematicBeat.Victory, row.Beat);
        Assert.Null(row.Wave);
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 脚本引用关卡未声明的钩子：接线失败即拒绝
    public void A_script_referencing_an_unknown_hook_is_rejected()
    {
        var hooks = new[]
        {
            new CinematicHookRow("campaign:hook-e1-ahwa-arrow-break", CinematicBeat.WaveStart, 2),
        };
        CinematicScript orphan = CinematicContent.E2Awaken(); // hook-e2-… 不在上表

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => new CinematicHookRouter(hooks, new[] { CinematicContent.E1Aha(), orphan }));
        Assert.Contains("hook-e2-awakening-awaken", ex.Message);
    }

    [Fact] // illegal — 两个脚本认领同一钩子：拒绝（一个接入点一场演出）
    public void Two_scripts_claiming_one_hook_are_rejected()
    {
        var hooks = new[]
        {
            new CinematicHookRow("campaign:hook-e1-ahwa-arrow-break", CinematicBeat.WaveStart, 2),
        };
        CinematicScript script = CinematicContent.E1Aha();
        CinematicScript twin = script with { Id = "cinematic:e1-aha-twin" };

        Assert.Throws<ArgumentException>(() => new CinematicHookRouter(hooks, new[] { script, twin }));
    }

    [Theory] // illegal — 钩子 schema 违规：未登记 beat / wave_start 缺 wave / wave<1 / 重复 id
    [InlineData("""{"items":[{"cinematic_hooks":[{"id":"h1","trigger":{"beat":"boss_die"}}]}]}""", "beat")]
    [InlineData("""{"items":[{"cinematic_hooks":[{"id":"h1","trigger":{"beat":"wave_start"}}]}]}""", "wave")]
    [InlineData("""{"items":[{"cinematic_hooks":[{"id":"h1","trigger":{"beat":"wave_start","wave":0}}]}]}""", "wave")]
    [InlineData("""{"items":[{"cinematic_hooks":[{"id":"h1","trigger":{"beat":"victory"}},{"id":"h1","trigger":{"beat":"victory"}}]}]}""", "重复")]
    [InlineData("""{"nope":1}""", "items")]
    public void Hook_schema_violations_are_rejected(string levelJson, string messageHint)
    {
        CinematicSchemaException ex = Assert.Throws<CinematicSchemaException>(
            () => CinematicHookRouter.ParseLevelHooks(levelJson, "inline"));

        Assert.Contains(messageHint, ex.Message);
    }
}
