using EraCore.Cinematics;
using Xunit;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-002] [F-104] T-087 结算演出脚本装载与 schema 校验：仓内
// victory.json 真实装载（保护线下限/五类结算步/转盘减速帧数/零终态命令/
// 无钩子=结算缝显式发射）、新动词合法形态、非法输入逐项拒绝（段数下限/
// 空减速表/递减减速/零帧/停顿超时长/保护线 0 拍/未登记动词进封闭表文案）。
// 注：JSON 样例全部收在类级 const（方法体/属性行禁花括号——红线扫描器的
// 方法体计数按行扫花括号）。
public class VictoryCinematicsScriptTests
{
    private const string LegalShell =
        """{"$schema":"urn:eratd:content:cinematics:1","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[""";

    private const string StepVerdict = """{"kind":"verdict_reveal","duration_ticks":60}""";

    private const string StepTier = """{"kind":"tier_reveal","duration_ticks":30}""";

    private const string StepWheel =
        """{"kind":"wheel_spin","duration_ticks":150,"segments":6,"decel_ticks":[2,3,4,6,9]}""";

    private const string StepChest = """{"kind":"chest_open","duration_ticks":75}""";

    private const string StepPersona = """{"kind":"persona_comment","duration_ticks":60}""";

    private const string StepWheelTwoSegments =
        """{"kind":"wheel_spin","duration_ticks":60,"segments":2,"decel_ticks":[2]}""";

    private const string StepWheelNoDecel =
        """{"kind":"wheel_spin","duration_ticks":60,"segments":6,"decel_ticks":[]}""";

    private const string StepWheelDecelShrinks =
        """{"kind":"wheel_spin","duration_ticks":60,"segments":6,"decel_ticks":[5,3]}""";

    private const string StepWheelZeroPause =
        """{"kind":"wheel_spin","duration_ticks":60,"segments":6,"decel_ticks":[4,0]}""";

    private const string StepWheelDecelOverflow =
        """{"kind":"wheel_spin","duration_ticks":10,"segments":6,"decel_ticks":[6,6]}""";

    private const string StepVerdictZeroDuration = """{"kind":"verdict_reveal","duration_ticks":0}""";

    private const string StepTierMissingDuration = """{"kind":"tier_reveal"}""";

    private const string StepKindLootRain = """{"kind":"loot_rain","duration_ticks":10}""";

    private static CinematicSchemaException ParseRejects(string json) =>
        Assert.Throws<CinematicSchemaException>(() => CinematicScript.Parse(json, "inline"));

    /// <summary>Assembles one full script JSON: shell + steps + closing brace.</summary>
    private static string ScriptWith(string stepsJson, string tailJson = "") =>
        LegalShell + stepsJson + "]" + tailJson + "}";

    // ---------------------------------------------------------------- legal

    [Fact] // legal — victory.json 装载：结算五步齐备+保护线 45 拍+零终态命令+无钩子
    public void The_victory_script_loads_with_the_settlement_beats_and_a_protected_floor()
    {
        CinematicScript script = CinematicContent.Victory();

        Assert.Equal("cinematic:victory-settlement", script.Id);
        Assert.Null(script.HookId); // 结算缝显式发射（victory 拍叙事钩子属 T-089）
        Assert.Equal(45, script.MinimumPlayTicks); // 1.5s@30Hz：至少播放 1.5s（§12 映射#10）
        Assert.Empty(script.TerminalCommands); // 零世界命令（入库已由 SettleVictory 先完成）

        CinematicStep.WheelSpin wheel = Assert.IsType<CinematicStep.WheelSpin>(
            Assert.Single(script.Steps, step => step is CinematicStep.WheelSpin));
        Assert.Equal(150, wheel.DurationTicks);
        Assert.Equal(6, wheel.Segments);
        Assert.Equal(new[] { 2, 3, 4, 6, 9 }, wheel.DecelTicks); // 减速停顿帧数脚本化

        Assert.IsType<CinematicStep.VerdictReveal>(
            Assert.Single(script.Steps, step => step is CinematicStep.VerdictReveal));
        Assert.IsType<CinematicStep.TierReveal>(
            Assert.Single(script.Steps, step => step is CinematicStep.TierReveal));
        Assert.IsType<CinematicStep.ChestOpen>(
            Assert.Single(script.Steps, step => step is CinematicStep.ChestOpen));
        Assert.IsType<CinematicStep.PersonaComment>(
            Assert.Single(script.Steps, step => step is CinematicStep.PersonaComment));

        // 事件序（判分→档位→转盘→宝箱→点评）在脚本行序上冻结
        int verdict = IndexOfKind(script, typeof(CinematicStep.VerdictReveal));
        int tier = IndexOfKind(script, typeof(CinematicStep.TierReveal));
        int spin = IndexOfKind(script, typeof(CinematicStep.WheelSpin));
        int chest = IndexOfKind(script, typeof(CinematicStep.ChestOpen));
        int comment = IndexOfKind(script, typeof(CinematicStep.PersonaComment));
        Assert.True(verdict < tier && tier < spin && spin < chest && chest < comment);
    }

    [Fact] // legal — 五类结算动词合法形态：逐类装载
    public void The_five_settlement_step_kinds_parse()
    {
        CinematicScript script = CinematicScript.Parse(
            ScriptWith(
                StepVerdict + "," + StepTier + "," + StepWheel + "," + StepChest + "," + StepPersona,
                ",\"minimum_play_ticks\":45,\"terminal_commands\":[]"),
            "inline");

        Assert.Equal(5, script.Steps.Count);
        Assert.Equal(45, script.MinimumPlayTicks);
        Assert.Equal(new CinematicStep.VerdictReveal(60), script.Steps[0]);
        Assert.Equal(new CinematicStep.TierReveal(30), script.Steps[1]);
        Assert.Equal(new CinematicStep.ChestOpen(75), script.Steps[3]);
        Assert.Equal(new CinematicStep.PersonaComment(60), script.Steps[4]);
    }

    [Fact] // legal — 普通脚本无保护线：缺省 null（T-086 跳过口径不受影响）
    public void Ordinary_scripts_carry_no_protected_floor()
    {
        CinematicScript script = CinematicScript.Parse(
            ScriptWith("""{"kind":"sound","cue":"ding_info"}""", ",\"terminal_commands\":[]"), "inline");

        Assert.Null(script.MinimumPlayTicks);
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 保护线恰 1 拍（下界）合法；停顿恰填满时长（head=0）合法
    public void Floor_of_one_tick_and_a_full_tail_are_legal()
    {
        CinematicScript minFloor = CinematicScript.Parse(
            ScriptWith("""{"kind":"verdict_reveal","duration_ticks":5}""", ",\"minimum_play_ticks\":1"), "inline");
        CinematicScript fullTail = CinematicScript.Parse(
            ScriptWith("""{"kind":"wheel_spin","duration_ticks":9,"segments":3,"decel_ticks":[1,3,5]}"""), "inline");

        Assert.Equal(1, minFloor.MinimumPlayTicks);
        CinematicStep.WheelSpin wheel = Assert.IsType<CinematicStep.WheelSpin>(Assert.Single(fullTail.Steps));
        Assert.Equal(9, wheel.DecelTicks.Sum());
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — 转盘段数下限：2 段拒绝（不成为转盘）
    public void A_wheel_below_the_segment_floor_is_rejected()
    {
        CinematicSchemaException ex = ParseRejects(ScriptWith(StepWheelTwoSegments));

        Assert.Contains("segments", ex.Message);
    }

    [Fact] // illegal — 空减速表：拒绝（减速停顿帧数必须脚本化）
    public void An_empty_decel_profile_is_rejected()
    {
        CinematicSchemaException ex = ParseRejects(ScriptWith(StepWheelNoDecel));

        Assert.Contains("decel_ticks", ex.Message);
    }

    [Fact] // illegal — 减速表递减/零帧：拒绝（减速=停顿不减）
    public void Shrinking_or_zero_pauses_are_rejected()
    {
        Assert.Contains("非递减", ParseRejects(ScriptWith(StepWheelDecelShrinks)).Message);
        Assert.Contains("≥1", ParseRejects(ScriptWith(StepWheelZeroPause)).Message);
    }

    [Fact] // illegal — 停顿总和超时长：拒绝（快转段必须非负）
    public void A_tail_longer_than_the_spin_is_rejected()
    {
        Assert.Contains("不得超过", ParseRejects(ScriptWith(StepWheelDecelOverflow)).Message);
    }

    [Fact] // illegal — 结算步计时字段：零值/缺字段拒绝（RL7：节拍必填入数据）
    public void Settlement_durations_are_required()
    {
        Assert.Contains("duration_ticks", ParseRejects(ScriptWith(StepVerdictZeroDuration)).Message);
        Assert.Contains("duration_ticks", ParseRejects(ScriptWith(StepTierMissingDuration)).Message);
    }

    [Fact] // illegal — 保护线 0 拍：拒绝（至少 1 拍起）
    public void A_zero_protected_floor_is_rejected()
    {
        CinematicSchemaException ex = ParseRejects(
            ScriptWith("""{"kind":"sound","cue":"ding_info"}""", ",\"minimum_play_ticks\":0"));

        Assert.Contains("minimum_play_ticks", ex.Message);
    }

    [Fact] // illegal — 未登记结算动词：拒绝并进封闭表文案（封表面可检）
    public void An_unregistered_settlement_kind_is_rejected_with_the_closed_list()
    {
        CinematicSchemaException ex = ParseRejects(ScriptWith(StepKindLootRain));

        Assert.Contains("kind", ex.Message);
        Assert.Contains("wheel_spin", ex.Message);
        Assert.Contains("verdict_reveal", ex.Message);
    }

    private static int IndexOfKind(CinematicScript script, Type kind)
    {
        for (int i = 0; i < script.Steps.Count; i++)
        {
            if (kind.IsInstanceOfType(script.Steps[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
