using EraCore.CampaignLoop;
using EraCore.Cinematics;
using EraCore.Tests.Cinematics;
using EraCore.Tests.Levels;
using Xunit;
using static EraCore.Tests.Levels.HandcraftedLevelAssemblyTests;

namespace EraCore.Tests.Cinematics;

// [C-24][F-113][F-114][F-107][F-108] T-160c 战役泳道剧情钩子路由（断点二②）：
// CinematicHookRouter 的钩子行此前绑死夹具 e1+e2 整文件混装（CinematicDirector
// 装载集）——本链证「按当前关行构造路由」的内容侧事实：①行源=HandcraftedLevel.
// CinematicHooks（逐关数据位）：e1-01 的断弹波始拍只在 e1-01 路由（e1-03 零行
// 零发射——不串关）；e2-01 的觉醒开局拍同理；②e6-final 胜利拍行=finale-zero
// 认领（FinalePlayback 冻结常量互证——战役宿主在 e6-final 胜利拍按系统开关
// 二选一显式发射，路由侧不抢拍）；③收束段三件（e3/e4/e5-finale）无钩子键=
// 显式发射位（宿主在终局关胜利拍按「收束→跃迁」序入队），且与跃迁过场五件
// 同为可解析脚本（宿主 Launch 通道的装载面）。
public class CampaignLaneHookRoutingTests
{
    /// <summary>当前关的钩子行→路由（宿主 BuildHookRouter 同律：行=该关数据位+封闭 beat 表）.</summary>
    private static CinematicHookRouter RouterOf(
        HandcraftedLevel level, params CinematicScript[] scripts)
    {
        string source = $"levels/{level.Era}.json:{level.Id}";
        var rows = new List<CinematicHookRow>();
        foreach (CinematicHook hook in level.CinematicHooks)
        {
            rows.Add(new CinematicHookRow(
                hook.Id, CinematicHookRouter.ParseBeat(hook.Beat, source, hook.Id), hook.Wave));
        }

        var routable = new List<CinematicScript>();
        foreach (CinematicScript script in scripts)
        {
            if (script.HookId is not null && rows.Any(row => row.HookId == script.HookId))
            {
                routable.Add(script);
            }
        }

        return new CinematicHookRouter(rows, routable);
    }

    private static HandcraftedLevel LevelOf(string file, string id) =>
        HandcraftedLevelData.Load(file).TryGet(id)
        ?? throw new InvalidOperationException($"缺关卡行 {id}");

    [Fact]
    public void E1_01_lane_fires_the_aha_script_exactly_at_wave_2_and_only_there()
    {
        HandcraftedLevel e101 = LevelOf("e1.json", "campaign:level-e1-01");
        CinematicHookRouter router = RouterOf(e101, CinematicContent.E1Aha(), CinematicContent.E2Awaken());

        // 开局拍/第 1 波：e1-01 无此行——零发射。
        Assert.Empty(router.OnLevelStart());
        Assert.Empty(router.OnWaveStart(1));

        // 第 2 波断弹位（§10.4 ①）：恰一场 e1-aha，会话内单发（重复拍静默）。
        Assert.Equal(new[] { "cinematic:e1-aha" }, router.OnWaveStart(2));
        Assert.Empty(router.OnWaveStart(2));
        Assert.Empty(router.OnWaveStart(3));

        // 胜利拍：e1-01 的交底行无脚本认领（T-089 域）——路由零发射（终局关/
        // 胜利拍演出=结算+收束+跃迁的显式发射位，不归剧情钩子）。
        Assert.Empty(router.OnVictory());
    }

    [Fact]
    public void Levels_without_hook_rows_never_fire_and_hooks_do_not_leak_across_levels()
    {
        // e1-03 无钩子行：即使全套脚本在场，e1-01 的断弹行也不串关（逐关路由
        // ——夹具 e1+e2 整文件混装装载的收口事实）。
        HandcraftedLevel e103 = LevelOf("e1.json", "campaign:level-e1-03");
        CinematicHookRouter router = RouterOf(e103, CinematicContent.E1Aha(), CinematicContent.E2Awaken());
        Assert.Empty(router.OnLevelStart());
        Assert.Empty(router.OnWaveStart(2));
        Assert.Empty(router.OnVictory());

        // e2-02 同律（觉醒行只在 e2-01）。
        HandcraftedLevel e202 = LevelOf("e2.json", "campaign:level-e2-02");
        Assert.Empty(RouterOf(e202, CinematicContent.E2Awaken()).OnLevelStart());
    }

    [Fact]
    public void E2_01_lane_fires_the_awaken_script_on_level_start()
    {
        HandcraftedLevel e201 = LevelOf("e2.json", "campaign:level-e2-01");
        CinematicHookRouter router = RouterOf(e201, CinematicContent.E1Aha(), CinematicContent.E2Awaken());

        Assert.Equal(new[] { "cinematic:e2-awaken" }, router.OnLevelStart());
        Assert.Empty(router.OnLevelStart()); // 单发
        Assert.Empty(router.OnWaveStart(2)); // 无波始行
    }

    [Fact]
    public void E6_final_victory_row_is_claimed_by_the_finale_script_and_picked_by_switch()
    {
        HandcraftedLevel e6Final = LevelOf("e6.json", "campaign:level-e6-final");
        CinematicHookRow row = Assert.Single(e6Final.CinematicHooks.Select(
            hook => new CinematicHookRow(
                hook.Id, CinematicHookRouter.ParseBeat(hook.Beat, "e6", hook.Id), hook.Wave)));
        Assert.Equal(FinalePlayback.FinaleHookId, row.HookId); // 冻结常量互证（T-136）
        Assert.Equal(CinematicBeat.Victory, row.Beat);

        // 路由侧：on 版认领该行（off 版无钩子键——开关选择变体，不二次认领）。
        CinematicHookRouter router = RouterOf(e6Final, CinematicContent.Finale(), CinematicContent.FinaleOff());
        Assert.Equal(new[] { FinalePlayback.OnScriptId }, router.OnVictory());
        Assert.Null(CinematicContent.FinaleOff().HookId);

        // 宿主侧二选一（开关位驱动——T-160 既有接线不破）。
        Assert.Equal(FinalePlayback.OnScriptId, FinalePlayback.ScriptIdFor(systemEnabled: true));
        Assert.Equal(FinalePlayback.OffScriptId, FinalePlayback.ScriptIdFor(systemEnabled: false));
    }

    [Fact]
    public void Era_finale_scripts_are_hookless_explicit_launches_and_transitions_all_parse()
    {
        // 收束段三件：无钩子键（显式发射位——终局关胜利拍由战役宿主入队），
        // id=eX-finale 命名约定与内容文件一一对应。
        foreach ((int Era, string Id) in new[] { (3, "cinematic:e3-finale"), (4, "cinematic:e4-finale"), (5, "cinematic:e5-finale") })
        {
            CinematicScript finale = CinematicScript.Parse(
                CinematicsFileText($"e{Era}-finale"), $"cinematics/e{Era}-finale.json");
            Assert.Equal(Id, finale.Id);
            Assert.Null(finale.HookId);
        }

        // 跃迁过场五件全解析（宿主「收束→跃迁」发射序的跃迁半边装载面）。
        foreach ((string From, string To) in new[]
                 { ("e1", "e2"), ("e2", "e3"), ("e3", "e4"), ("e4", "e5"), ("e5", "e6") })
        {
            CinematicScript transition = CinematicContent.Transition(From, To);
            Assert.Equal($"cinematic:transition-{From}-{To}", transition.Id);
            Assert.Null(transition.HookId); // transition 拍=事务点显式发射（T-152）
        }
    }

    private static string CinematicsFileText(string fileName) =>
        File.ReadAllText(Path.Combine(ContentRoot(), "cinematics", $"{fileName}.json"));
}
