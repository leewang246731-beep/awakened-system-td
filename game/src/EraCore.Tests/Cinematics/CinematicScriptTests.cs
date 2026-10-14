using EraCore.Cinematics;
using EraCore.Contracts.Intents;
using Xunit;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-113] [F-114] T-086 演出脚本装载与 schema 校验：两份仓内内容脚本
// 真实装载（步骤数/钩子/终态口径）、令簿 12 行与 K-4 冻结枚举对齐、
// 三选一 3 张卡、封闭表（音效/屏幕/表面/动词）与结构规则（choice 与
// terminal_commands 互斥、单 choice、默认位、检查点去重、计时字段必填）
// 逐项拒绝非法输入。
// 注：JSON 样例全部收在类级 const（方法体/属性行禁花括号——红线扫描器的
// 方法体计数按行扫花括号）。
public class CinematicScriptTests
{
    private const string LegalShell =
        """{"$schema":"urn:eratd:content:cinematics:1","content_type":"Cinematic","id":"cinematic:x","title_zh":"样例","steps":[""";

    private const string StepSoundInfo = """{"kind":"sound","cue":"ding_info"}""";

    private const string StepKindDance = """{"kind":"dance"}""";

    private const string StepCueEpic = """{"kind":"sound","cue":"ding_epic"}""";

    private const string StepScreenSepia = """{"kind":"screen","flavor":"sepia"}""";

    private const string StepUiDisco = """{"kind":"ui","surface":"hologram_disco"}""";

    private const string StepHighlightBlue =
        """{"kind":"highlight","target":{"type":"cell","cell":{"x":1,"y":2}},"flavor":"blue_beep"}""";

    private const string StepCameraMoon =
        """{"kind":"camera_move","target":{"type":"moon","cell":{"x":1,"y":2}}}""";

    private const string StepWaitBlankEvent = """{"kind":"wait_event","event":" "}""";

    private const string StepSpeakBlankText = """{"kind":"speak","speaker":"s","text":" "}""";

    private const string StepCheckpointBlankId = """{"kind":"checkpoint","id":" "}""";

    private const string StepSpeakZeroDuration =
        """{"kind":"speak","speaker":"s","text":"t","duration_ticks":0}""";

    private const string StepCameraNegativeDuration =
        """{"kind":"camera_move","target":{"type":"cell","cell":{"x":1,"y":2}},"duration_ticks":-1}""";

    private const string StepScreenNegativeDuration =
        """{"kind":"screen","flavor":"fade_out","duration_ticks":-5}""";

    private const string StepCameraMissingDuration =
        """{"kind":"camera_move","target":{"type":"cell","cell":{"x":1,"y":2}}}""";

    private const string FiveOptionChoice =
        """{"kind":"choice","prompt":"p","options":[{"label":"o0"},{"label":"o1"},{"label":"o2"},{"label":"o3"},{"label":"o4"}]}""";

    private const string OneOptionChoice =
        """{"kind":"choice","prompt":"p","options":[{"label":"a"}]}""";

    private const string DefaultOutOfRangeChoice =
        """{"kind":"choice","prompt":"p","default":2,"options":[{"label":"a"},{"label":"b"}]}""";

    private const string TwoOptionChoice =
        """{"kind":"choice","prompt":"p","options":[{"label":"a"},{"label":"b"}]}""";

    private const string DuplicateCheckpoints =
        """{"kind":"checkpoint","id":"a"},{"kind":"checkpoint","id":"a"}""";

    private const string LedgerOnForeignSurface =
        """{"kind":"ui","surface":"holo_panel_expand","ledger":["守住X"],"duration_ticks":10}""";

    private const string CommandTeleport = """{"kind":"teleport","order":"守住东门"}""";

    private const string CommandBlankOrder = """{"kind":"issue_order","order":" "}""";

    private const string CommandNegativeDelay =
        """{"kind":"enqueue_sim","delay_ticks":-1,"sim_kind":"PlayerBuild","payload":"{}"}""";

    private const string CommandUnknownSimKind =
        """{"kind":"enqueue_sim","sim_kind":"Teleport","payload":"{}"}""";

    private const string CommandBlankPayload =
        """{"kind":"enqueue_sim","sim_kind":"PlayerBuild","payload":" "}""";

    private const string HoldEastGateCommand =
        """{"kind":"issue_order","order":"守住东门"}""";

    private const string MinimalSteps =
        """{"kind":"highlight","target":{"type":"cell","cell":{"x":1,"y":2}},"flavor":"red_flash"},{"kind":"camera_move","target":{"type":"carrier_to","cell":{"x":3,"y":4}},"duration_ticks":20},{"kind":"choice","prompt":"p","options":[{"label":"a"},{"label":"b"}]}""";

    private const string WrongContentTypeScript =
        """{"content_type":"Level","id":"cinematic:x","title_zh":"t","steps":[{"kind":"sound","cue":"ding_info"}]}""";

    private static CinematicSchemaException ParseRejects(string json) =>
        Assert.Throws<CinematicSchemaException>(() => CinematicScript.Parse(json, "inline"));

    /// <summary>Assembles one full script JSON: shell + steps + optional tail + closing brace.</summary>
    private static string ScriptWith(string stepsJson, string tailJson) =>
        LegalShell + stepsJson + "]" + tailJson + "}";

    /// <summary>Shell + steps + one annotation key + empty terminal commands (closes the root).</summary>
    private static string ScriptWithAnnotation(string stepsJson, string annotationKey) =>
        LegalShell + stepsJson + "],\"" + annotationKey + "\":\"任意注解\",\"terminal_commands\":[]}";

    // ---------------------------------------------------------------- legal

    [Fact] // legal — E1 啊哈脚本装载：11 步、零终态命令（纯演出零污染）
    public void E1_aha_script_loads_with_eleven_steps_and_zero_terminal_commands()
    {
        CinematicScript script = CinematicContent.E1Aha();

        Assert.Equal("cinematic:e1-aha", script.Id);
        Assert.Equal("campaign:hook-e1-ahwa-arrow-break", script.HookId);
        Assert.Equal(11, script.Steps.Count);
        Assert.Empty(script.TerminalCommands);
        Assert.Equal(2, script.Steps.Count(step => step is CinematicStep.Sound)); // 危机叮+奖励叮叮
        Assert.Equal(2, script.Steps.Count(step => step is CinematicStep.Highlight)); // 闪红+恢复绿
        Assert.Single(script.Steps, step => step is CinematicStep.WaitEvent wait && wait.EventKey == "ammo_restored");
        Assert.Single(script.Steps, step => step is CinematicStep.SkipPoint skip && skip.Id == "arrow-restored");

        // §10.4 ① 拍序断言：断弹（危机）在前，恢复（奖励）在后
        int crisis = IndexOfSound(script, "ding_crisis");
        int reward = IndexOfSound(script, "ding_reward");
        Assert.True(crisis < reward, "断弹危机叮必须先于恢复奖励叮叮");
    }

    private static int IndexOfSound(CinematicScript script, string cue)
    {
        for (int i = 0; i < script.Steps.Count; i++)
        {
            if (script.Steps[i] is CinematicStep.Sound sound && sound.Cue == cue)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact] // legal — E2 觉醒脚本装载：13 步、三选一 3 张卡、默认 0、令簿 12 行、黑屏开场
    public void E2_awaken_script_loads_with_thirteen_steps_three_choices_and_a_twelve_row_ledger()
    {
        CinematicScript script = CinematicContent.E2Awaken();

        Assert.Equal("cinematic:e2-awaken", script.Id);
        Assert.Equal("campaign:hook-e2-awakening-awaken", script.HookId);
        Assert.Equal(13, script.Steps.Count);
        Assert.IsType<CinematicStep.Screen>(script.Steps[0]); // 黑屏开场
        Assert.Equal("black_fade_in", ((CinematicStep.Screen)script.Steps[0]).Flavor);

        CinematicStep.Choice choice = script.Steps.OfType<CinematicStep.Choice>().Single();
        Assert.Equal(3, choice.Options.Count);
        Assert.Equal(0, choice.DefaultIndex);
        Assert.Equal(new[] { "守住南门", "在东门建两座木箭塔", "敌情怎么样" },
            choice.Options.Select(option => option.Label).ToArray());
        Assert.All(choice.Options, option =>
            Assert.IsType<CinematicWorldCommand.IssueOrder>(Assert.Single(option.OnConfirm)));

        CinematicStep.Ui ledger = script.Steps
            .OfType<CinematicStep.Ui>()
            .Single(ui => ui.Surface == "ledger_reveal");
        Assert.Equal(Enum.GetValues<IntentKind>().Length, ledger.Ledger.Count); // 令簿=K-4 冻结 12 意图
        Assert.Equal(12, ledger.Ledger.Count);
    }

    [Fact] // legal — 终态命令集：觉醒脚本的选择默认路径=选项 0 的 on_confirm（跳过等价数据面）
    public void Awaken_default_terminal_set_is_the_default_options_commands()
    {
        CinematicScript script = CinematicContent.E2Awaken();
        CinematicStep.Choice choice = script.Steps.OfType<CinematicStep.Choice>().Single();

        var timeline = new CinematicTimeline(script);
        Assert.Equal(choice.Options[choice.DefaultIndex].OnConfirm, timeline.TerminalCommands);
    }

    [Fact] // legal — 计时字段必填入数据（RL7）：只有 highlight 缺省 0（瞬发）
    public void Minimal_legal_script_parses_with_the_highlight_default()
    {
        CinematicScript script = CinematicScript.Parse(
            ScriptWith(MinimalSteps, tailJson: string.Empty), "inline");

        var highlight = (CinematicStep.Highlight)script.Steps[0];
        Assert.Equal(0, highlight.DurationTicks); // 瞬发高亮缺省
        var camera = (CinematicStep.CameraMove)script.Steps[1];
        Assert.Equal(20, camera.DurationTicks); // 计时字段来自脚本数据
        Assert.Equal(CinematicTargetKind.CarrierToCell, camera.Target.Kind);
        var choice = (CinematicStep.Choice)script.Steps[2];
        Assert.Equal(0, choice.DefaultIndex); // 默认位缺省 0
        Assert.Empty(choice.Options[0].OnConfirm); // 无命令选项合法（纯展示选择）
    }

    // -------------------------------------------------------------- boundary

    [Theory] // boundary — 注解键（_ 前缀）被装载器忽略：内容文件约定
    [InlineData("_annotation_convention")]
    [InlineData("_source")]
    [InlineData("_units")]
    public void Annotation_keys_are_ignored_by_the_parser(string annotationKey)
    {
        CinematicScript script = CinematicScript.Parse(
            ScriptWithAnnotation(StepSoundInfo, annotationKey), "inline");

        Assert.Single(script.Steps);
    }

    [Fact] // boundary — 步骤数组下限：空 steps 拒绝
    public void Empty_steps_array_is_rejected()
    {
        CinematicSchemaException ex = ParseRejects(ScriptWith(string.Empty, ",\"terminal_commands\":[]"));

        Assert.Contains("steps", ex.Message);
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — content_type 不是 Cinematic：拒绝（K-2 注册表口径）
    public void Wrong_content_type_is_rejected()
    {
        CinematicSchemaException ex = ParseRejects(WrongContentTypeScript);

        Assert.Contains("content_type", ex.Message);
    }

    [Fact] // illegal — 非 JSON 文本：拒绝
    public void Malformed_json_is_rejected()
    {
        Assert.Throws<CinematicSchemaException>(() => CinematicScript.Parse("{\"kind\":", "inline"));
    }

    [Theory] // illegal — 未登记步骤动词/枚举值：逐项拒绝（字段路径进消息）
    [InlineData(StepKindDance, "kind")]
    [InlineData(StepCueEpic, "cue")]
    [InlineData(StepScreenSepia, "flavor")]
    [InlineData(StepUiDisco, "surface")]
    [InlineData(StepHighlightBlue, "flavor")]
    [InlineData(StepCameraMoon, "type")]
    [InlineData(StepWaitBlankEvent, "event")]
    [InlineData(StepSpeakBlankText, "text")]
    [InlineData(StepCheckpointBlankId, "id")]
    public void Unknown_step_kinds_and_enum_values_are_rejected(string stepJson, string fieldHint)
    {
        CinematicSchemaException ex = ParseRejects(
            ScriptWith(stepJson, ",\"terminal_commands\":[]"));

        Assert.Contains(fieldHint, ex.Message);
    }

    [Theory] // illegal — 非法时长：零/负值/缺字段逐类拒绝（计时数值必填入数据）
    [InlineData(StepSpeakZeroDuration)]
    [InlineData(StepCameraNegativeDuration)]
    [InlineData(StepScreenNegativeDuration)]
    [InlineData(StepCameraMissingDuration)]
    public void Illegal_timed_durations_are_rejected(string stepJson)
    {
        CinematicSchemaException ex = ParseRejects(
            ScriptWith(stepJson, ",\"terminal_commands\":[]"));

        Assert.Contains("duration_ticks", ex.Message);
    }

    [Fact] // illegal — 三选一选项数越界（AskPlayer 2~4 同源）：5 张/1 张都拒绝
    public void Choice_option_count_outside_two_to_four_is_rejected()
    {
        CinematicSchemaException exFive = ParseRejects(
            ScriptWith(FiveOptionChoice, ",\"terminal_commands\":[]"));
        Assert.Contains("2~4", exFive.Message);

        CinematicSchemaException exOne = ParseRejects(
            ScriptWith(OneOptionChoice, ",\"terminal_commands\":[]"));
        Assert.Contains("2~4", exOne.Message);
    }

    [Fact] // illegal — 默认位越界：拒绝
    public void Choice_default_out_of_range_is_rejected()
    {
        CinematicSchemaException ex = ParseRejects(
            ScriptWith(DefaultOutOfRangeChoice, ",\"terminal_commands\":[]"));

        Assert.Contains("default", ex.Message);
    }

    [Fact] // illegal — choice 与 terminal_commands 并存（终态命令集必须唯一）：拒绝
    public void Choice_script_with_terminal_commands_is_rejected()
    {
        CinematicSchemaException ex = ParseRejects(
            ScriptWith(TwoOptionChoice, ",\"terminal_commands\":[" + HoldEastGateCommand + "]"));

        Assert.Contains("terminal_commands", ex.Message);
    }

    [Fact] // illegal — 两个 choice 步骤：拒绝（终态命令集必须唯一）
    public void Two_choice_steps_are_rejected()
    {
        CinematicSchemaException ex = ParseRejects(
            ScriptWith(TwoOptionChoice + "," + TwoOptionChoice, ",\"terminal_commands\":[]"));

        Assert.Contains("至多 1", ex.Message);
    }

    [Fact] // illegal — 检查点 id 重复：拒绝
    public void Duplicate_checkpoint_ids_are_rejected()
    {
        CinematicSchemaException ex = ParseRejects(
            ScriptWith(DuplicateCheckpoints, ",\"terminal_commands\":[]"));

        Assert.Contains("重复", ex.Message);
    }

    [Fact] // illegal — ledger 行挂在非 ledger_reveal 表面：拒绝
    public void Ledger_rows_on_a_foreign_surface_are_rejected()
    {
        CinematicSchemaException ex = ParseRejects(
            ScriptWith(LedgerOnForeignSurface, tailJson: string.Empty));

        Assert.Contains("ledger_reveal", ex.Message);
    }

    [Theory] // illegal — 终态命令：未登记动词/空军令/负延迟/未知 sim_kind 逐项拒绝
    [InlineData(CommandTeleport, "kind")]
    [InlineData(CommandBlankOrder, "order")]
    [InlineData(CommandNegativeDelay, "delay_ticks")]
    [InlineData(CommandUnknownSimKind, "sim_kind")]
    [InlineData(CommandBlankPayload, "payload")]
    public void Illegal_terminal_commands_are_rejected(string commandJson, string fieldHint)
    {
        CinematicSchemaException ex = ParseRejects(
            ScriptWith(StepSoundInfo, ",\"terminal_commands\":[" + commandJson + "]"));

        Assert.Contains(fieldHint, ex.Message);
    }
}
