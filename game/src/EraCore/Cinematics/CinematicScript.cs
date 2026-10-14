using System.Text.Json;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;

namespace EraCore.Cinematics;

/// <summary>
/// Schema violation of one cinematic script row (T-086, C-24): the parser
/// throws this with a field-pathed message — content defects surface as load
/// failures, never as silently-zeroed steps (same discipline as
/// <c>HandcraftedLevelSchemaException</c> / <c>EnemySpecCatalog.Parse</c>).
/// </summary>
public sealed class CinematicSchemaException : Exception
{
    public CinematicSchemaException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// What a camera/highlight step points at (T-086). <see cref="Cell"/> is the
/// K-4 cell domain anchor; <see cref="CarrierToCell"/> additionally asks the
/// host to FOLLOW the live carrier whose destination is that cell (the E1
/// aha 民夫送箭 follow shot — a read-only snapshot lookup, never a sim write).
/// </summary>
public enum CinematicTargetKind
{
    /// <summary>A fixed grid cell (tower head, gate line, base).</summary>
    Cell = 0,

    /// <summary>The live carrier currently delivering toward the cell (follow shot).</summary>
    CarrierToCell = 1,
}

/// <summary>One shot target of a camera/highlight step.</summary>
public sealed record CinematicTarget(CinematicTargetKind Kind, CellCoord Cell);

/// <summary>Highlight flavors the host renders (§10.4 ①: 闪红 → 恢复绿).</summary>
public enum CinematicHighlightFlavor
{
    /// <summary>塔头闪红 — the ammo-out alarm flash.</summary>
    RedFlash = 0,

    /// <summary>恢复绿 — the supply-restored confirmation tint.</summary>
    GreenRestore = 1,
}

/// <summary>
/// One declared world write of a cinematic (T-086 零污染纪律): the script may
/// ONLY change the world through these preset commands — the timeline hands
/// them to the host, the host routes them through the real K-6 queue / real
/// five-stage pipeline. The performance itself reads snapshots and emits
/// presentation effects; it never mutates simulation state directly.
/// </summary>
public abstract record CinematicWorldCommand
{
    /// <summary>
    /// Issues one military order through the REAL AgentPipeline
    /// (<c>Handle</c> + plan-card confirm when the gate says ask) — the E2
    /// awakening 三选一预填军令 path (§10.4 ②).
    /// </summary>
    public sealed record IssueOrder(string OrderText) : CinematicWorldCommand;

    /// <summary>
    /// Enqueues one preset <see cref="SimCommand"/> at
    /// <c>current tick + DelayTicks</c> — the raw channel for future scripts
    /// (victory handovers, T-087+); both repo scripts of T-086 use
    /// <see cref="IssueOrder"/> or none at all.
    /// </summary>
    public sealed record EnqueueSim(long DelayTicks, SimCommandKind Kind, string PayloadJson)
        : CinematicWorldCommand;
}

/// <summary>One pre-filled card of the 三选一 step: display label + its declared world commands.</summary>
public sealed record CinematicChoiceOption(
    string Label,
    IReadOnlyList<CinematicWorldCommand> OnConfirm);

/// <summary>
/// One step of a cinematic timeline (T-086, C-24). The closed kind set of the
/// task card: 镜头移动 / 高亮目标 / 等待事件 / 播放文本 / 触发音效 / 跳过检查点,
/// plus the 黑屏-面板-令簿 surface steps and the 三选一 interactive step the two
/// §10.4 scripts need.
/// </summary>
public abstract record CinematicStep
{
    /// <summary>Camera move (push / pan / follow) toward one target.</summary>
    public sealed record CameraMove(CinematicTarget Target, int DurationTicks) : CinematicStep;

    /// <summary>UI highlight of one target (red flash / green restore).</summary>
    public sealed record Highlight(CinematicTarget Target, CinematicHighlightFlavor Flavor, int DurationTicks)
        : CinematicStep;

    /// <summary>Blocks until the host signals the event key (world-observed fact).</summary>
    public sealed record WaitEvent(string EventKey) : CinematicStep;

    /// <summary>Plays one subtitle/bubble line for a fixed duration.</summary>
    public sealed record Speak(string Speaker, string Text, int DurationTicks) : CinematicStep;

    /// <summary>Triggers one sound cue (closed cue set, host maps to ding tiers).</summary>
    public sealed record Sound(string Cue) : CinematicStep;

    /// <summary>Full-screen flavor (black_fade_in 黑屏 / fade_out 收幕).</summary>
    public sealed record Screen(string Flavor, int DurationTicks) : CinematicStep;

    /// <summary>
    /// A UI surface beat: <c>holo_panel_expand</c> (§10.4 ② 全息面板展开) or
    /// <c>ledger_reveal</c> (系统交底"令簿" — the 12-intent list rows ride the step).
    /// </summary>
    public sealed record Ui(string Surface, IReadOnlyList<string> Ledger, int DurationTicks) : CinematicStep;

    /// <summary>The 三选一 pre-filled order card: blocks until the player picks (default on skip).</summary>
    public sealed record Choice(
        string Prompt,
        IReadOnlyList<CinematicChoiceOption> Options,
        int DefaultIndex) : CinematicStep;

    /// <summary>跳过检查点 — a skip-safety marker the host observes (可跳过位).</summary>
    public sealed record SkipPoint(string Id) : CinematicStep;

    /// <summary>
    /// 判分结果展示 (T-087 结算演出): the triple-objective checklist plays
    /// out row by row — the rows ride the BOUND decided settlement
    /// (<see cref="CinematicTimeline"/> ctor), never the script (RL12).
    /// </summary>
    public sealed record VerdictReveal(int DurationTicks) : CinematicStep;

    /// <summary>档位揭示 — the tier card (青铜/白银/黄金/黄金+额外) of the decided settlement.</summary>
    public sealed record TierReveal(int DurationTicks) : CinematicStep;

    /// <summary>
    /// 幸运转盘（结算）— the wheel performance. Deceleration is SCRIPTED
    /// (减速停顿帧数脚本化, RL7): the tail dwell frame counts live in
    /// content; the landing segment comes from the bound decided settlement
    /// (演出倒推，见 <see cref="WheelDynamics"/>).
    /// </summary>
    /// <param name="DurationTicks">Total spin duration.</param>
    /// <param name="Segments">Wheel layout size (must equal the decided layout's size).</param>
    /// <param name="DecelTicks">Tail dwell frame counts, each ≥1, non-decreasing (减速=停顿不减).</param>
    public sealed record WheelSpin(int DurationTicks, int Segments, IReadOnlyList<int> DecelTicks)
        : CinematicStep;

    /// <summary>宝箱开箱 — the tiered chest opening; light layers come from the decided tier (光效分层).</summary>
    public sealed record ChestOpen(int DurationTicks) : CinematicStep;

    /// <summary>系统一句话点评 — the persona×trust-badge line of the decided settlement.</summary>
    public sealed record PersonaComment(int DurationTicks) : CinematicStep;
}

/// <summary>
/// One data-driven cinematic (T-086, C-24): <c>game/content/cinematics/*.json</c>.
/// Pure content — every duration, line and world command lives here (RL7); the
/// engine (<see cref="CinematicTimeline"/>) carries no per-script values.
/// <para>
/// Terminal-command ownership (skip 等价的结构保证): a script EITHER carries
/// exactly one <see cref="CinematicStep.Choice"/> step (each option owns its
/// OnConfirm commands; the default option covers the skip-before-choice path)
/// OR declares script-level <see cref="TerminalCommands"/> that fire on
/// completion (natural or skipped). Declaring both is a schema error — the
/// terminal state must be one declared command set, never two competing ones.
/// </para>
/// <para>
/// Protected performances (T-087 加速不可全跳): a script declaring
/// <see cref="MinimumPlayTicks"/> is a settlement-style protected
/// performance — no full skip exists for it (长按只加速，见
/// <see cref="CinematicTimeline.SetBoost"/>) and its completion is held to
/// at least that many playback ticks (§12 映射#10 保护期待感).
/// </para>
/// </summary>
public sealed record CinematicScript(
    string Id,
    string? HookId,
    string TitleZh,
    IReadOnlyList<CinematicStep> Steps,
    IReadOnlyList<CinematicWorldCommand> TerminalCommands)
{
    /// <summary>Content schema URN of the cinematics registry (K-2 family).</summary>
    public const string SchemaUrn = "urn:eratd:content:cinematics:1";

    /// <summary>Content type marker the loader requires (<c>content_type</c> field).</summary>
    public const string ContentType = "Cinematic";

    /// <summary>
    /// 保护线下限（演出拍数，RL7：入内容数据；省略=未受保护的普通演出）：
    /// 声明它的脚本不可全跳，且完成不得早于该拍数（§12 映射#10 保护期待感）。
    /// </summary>
    public int? MinimumPlayTicks { get; init; }

    /// <summary>Closed sound-cue set (host maps each to a <c>DingBus</c> tier).</summary>
    public static readonly IReadOnlySet<string> SoundCues =
        new HashSet<string> { "ding_info", "ding_reward", "ding_crisis" };

    /// <summary>Closed screen-flavor set.</summary>
    public static readonly IReadOnlySet<string> ScreenFlavors =
        new HashSet<string> { "black_fade_in", "fade_out" };

    /// <summary>Closed UI-surface set.</summary>
    public static readonly IReadOnlySet<string> UiSurfaces =
        new HashSet<string> { "holo_panel_expand", "ledger_reveal" };

    /// <summary>
    /// Strict parser (field-pathed errors, missing fields throw). Keys
    /// starting with <c>_</c> are annotations and ignored (content convention).
    /// </summary>
    /// <param name="json">Raw script JSON text.</param>
    /// <param name="sourceName">Source label for error messages.</param>
    /// <exception cref="CinematicSchemaException">Any schema violation.</exception>
    public static CinematicScript Parse(string json, string sourceName = "cinematic script")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new CinematicSchemaException($"{sourceName}: 脚本为空");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CinematicSchemaException($"{sourceName}: JSON 解析失败（{ex.Message}）");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object, $"{sourceName}: 根节点须为对象");
            string contentType = String(root, "content_type", sourceName);
            Require(
                contentType == ContentType,
                $"{sourceName}: content_type 须为 {ContentType}（得到 {contentType}）");

            string id = String(root, "id", sourceName);
            Require(!id.Any(char.IsWhiteSpace), $"{sourceName}: id 含空白字符");
            string? hook = root.TryGetProperty("hook", out JsonElement hookEl)
                && hookEl.ValueKind == JsonValueKind.String
                ? hookEl.GetString()
                : null;
            if (hook is not null)
            {
                Require(!string.IsNullOrWhiteSpace(hook), $"{sourceName}: hook 为空白（不挂钩子请省略该键）");
            }

            string title = String(root, "title_zh", sourceName);
            Require(!string.IsNullOrWhiteSpace(title), $"{sourceName}: title_zh 为空白");

            int? minimumPlay = null; // 保护线下限（T-087 加速不可全跳；RL7：入内容数据）
            if (root.TryGetProperty("minimum_play_ticks", out JsonElement minimumEl)
                && minimumEl.ValueKind != JsonValueKind.Null)
            {
                Require(
                    minimumEl.ValueKind == JsonValueKind.Number && minimumEl.TryGetInt32(out _),
                    $"{sourceName}: minimum_play_ticks 须为整数");
                int minimum = minimumEl.GetInt32();
                Require(minimum >= 1, $"{sourceName}: minimum_play_ticks 须 ≥1（得到 {minimum}）");
                minimumPlay = minimum;
            }

            var steps = new List<CinematicStep>();
            JsonElement stepsEl = Array(root, "steps", sourceName);
            Require(stepsEl.GetArrayLength() >= 1, $"{sourceName}: steps 至少 1 步");
            var skipPointIds = new HashSet<string>();
            int choiceSteps = 0;
            foreach (JsonElement stepEl in stepsEl.EnumerateArray())
            {
                CinematicStep step = ParseStep(stepEl, sourceName, skipPointIds);
                if (step is CinematicStep.Choice)
                {
                    choiceSteps++;
                }

                steps.Add(step);
            }

            var terminal = new List<CinematicWorldCommand>();
            if (root.TryGetProperty("terminal_commands", out JsonElement terminalEl)
                && terminalEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement commandEl in terminalEl.EnumerateArray())
                {
                    terminal.Add(ParseCommand(commandEl, sourceName));
                }
            }

            Require(
                choiceSteps <= 1,
                $"{sourceName}: 三选一步骤至多 1 个（得到 {choiceSteps}——终态命令集必须唯一）");
            Require(
                choiceSteps == 0 || terminal.Count == 0,
                $"{sourceName}: 携带 choice 步骤的脚本不得再声明 terminal_commands（终态命令集唯一：选项自带 on_confirm）");

            return new CinematicScript(id, hook, title, steps, terminal) { MinimumPlayTicks = minimumPlay };
        }
    }

    private static CinematicStep ParseStep(JsonElement el, string source, HashSet<string> skipPointIds)
    {
        Require(el.ValueKind == JsonValueKind.Object, $"{source}: step 须为对象");
        string kind = String(el, "kind", source);
        switch (kind)
        {
            case "camera_move":
            {
                CinematicTarget target = ParseTarget(Object(el, "target", source), source);
                int duration = Int(el, "duration_ticks", source); // RL7：演出节奏数值全部入脚本数据
                RequireDuration(duration, min: 1, source, "camera_move");
                return new CinematicStep.CameraMove(target, duration);
            }

            case "highlight":
            {
                CinematicTarget target = ParseTarget(Object(el, "target", source), source);
                string flavor = String(el, "flavor", source);
                Require(
                    Enum.TryParse<CinematicHighlightFlavor>(ToPascal(flavor), out CinematicHighlightFlavor parsed),
                    $"{source}: highlight.flavor 未登记（{flavor}）");
                int duration = OptionalInt(el, "duration_ticks", 0);
                RequireDuration(duration, min: 0, source, "highlight");
                return new CinematicStep.Highlight(target, parsed, duration);
            }

            case "wait_event":
            {
                string key = String(el, "event", source);
                Require(!string.IsNullOrWhiteSpace(key), $"{source}: wait_event.event 为空白");
                return new CinematicStep.WaitEvent(key);
            }

            case "speak":
            {
                string speaker = String(el, "speaker", source);
                string text = String(el, "text", source);
                Require(!string.IsNullOrWhiteSpace(text), $"{source}: speak.text 为空白");
                int duration = Int(el, "duration_ticks", source); // RL7：台词时长入脚本数据
                RequireDuration(duration, min: 1, source, "speak");
                return new CinematicStep.Speak(speaker, text, duration);
            }

            case "sound":
            {
                string cue = String(el, "cue", source);
                Require(
                    SoundCues.Contains(cue),
                    $"{source}: sound.cue 未登记（{cue}，封闭表 {string.Join("/", SoundCues)}）");
                return new CinematicStep.Sound(cue);
            }

            case "screen":
            {
                string flavor = String(el, "flavor", source);
                Require(
                    ScreenFlavors.Contains(flavor),
                    $"{source}: screen.flavor 未登记（{flavor}，封闭表 {string.Join("/", ScreenFlavors)}）");
                int duration = Int(el, "duration_ticks", source); // RL7：黑屏节拍入脚本数据
                RequireDuration(duration, min: 1, source, "screen");
                return new CinematicStep.Screen(flavor, duration);
            }

            case "ui":
            {
                string surface = String(el, "surface", source);
                Require(
                    UiSurfaces.Contains(surface),
                    $"{source}: ui.surface 未登记（{surface}，封闭表 {string.Join("/", UiSurfaces)}）");
                var ledger = new List<string>();
                if (el.TryGetProperty("ledger", out JsonElement ledgerEl))
                {
                    Require(
                        ledgerEl.ValueKind == JsonValueKind.Array,
                        $"{source}: ui.ledger 须为数组（令簿 12 意图清单行）");
                    Require(
                        surface == "ledger_reveal",
                        $"{source}: ui.ledger 只属于 ledger_reveal 表面（{surface} 不携带）");
                    foreach (JsonElement row in ledgerEl.EnumerateArray())
                    {
                        Require(row.ValueKind == JsonValueKind.String, $"{source}: ui.ledger 行须为字符串");
                        string line = row.GetString()!;
                        Require(!string.IsNullOrWhiteSpace(line), $"{source}: ui.ledger 含空白行");
                        ledger.Add(line);
                    }

                    Require(ledger.Count >= 1, $"{source}: ui.ledger 至少 1 行");
                }

                int duration = Int(el, "duration_ticks", source); // RL7：表面节拍入脚本数据
                RequireDuration(duration, min: 1, source, "ui");
                return new CinematicStep.Ui(surface, ledger, duration);
            }

            case "choice":
            {
                string prompt = String(el, "prompt", source);
                Require(!string.IsNullOrWhiteSpace(prompt), $"{source}: choice.prompt 为空白");
                JsonElement optionsEl = Array(el, "options", source);
                int count = optionsEl.GetArrayLength();
                // §11.3 AskPlayer 约束同源：选项 2~4 张卡。
                Require(
                    count is >= 2 and <= 4,
                    $"{source}: choice.options 须 2~4 张（得到 {count}）");
                var options = new List<CinematicChoiceOption>();
                foreach (JsonElement optionEl in optionsEl.EnumerateArray())
                {
                    string label = String(optionEl, "label", source);
                    Require(!string.IsNullOrWhiteSpace(label), $"{source}: choice.option.label 为空白");
                    var commands = new List<CinematicWorldCommand>();
                    if (optionEl.TryGetProperty("on_confirm", out JsonElement confirmEl))
                    {
                        Require(
                            confirmEl.ValueKind == JsonValueKind.Array,
                            $"{source}: choice.option.on_confirm 须为数组");
                        foreach (JsonElement commandEl in confirmEl.EnumerateArray())
                        {
                            commands.Add(ParseCommand(commandEl, source));
                        }
                    }

                    options.Add(new CinematicChoiceOption(label, commands));
                }

                int defaultIndex = OptionalInt(el, "default", 0);
                Require(
                    defaultIndex >= 0 && defaultIndex < count,
                    $"{source}: choice.default 越界（{defaultIndex}/0..{count - 1}）");
                return new CinematicStep.Choice(prompt, options, defaultIndex);
            }

            case "checkpoint":
            {
                string id = String(el, "id", source);
                Require(!string.IsNullOrWhiteSpace(id), $"{source}: checkpoint.id 为空白");
                Require(skipPointIds.Add(id), $"{source}: checkpoint.id 重复（{id}）");
                return new CinematicStep.SkipPoint(id);
            }

            case "verdict_reveal":
            {
                int duration = Int(el, "duration_ticks", source); // RL7：判分展示节拍入脚本数据
                RequireDuration(duration, min: 1, source, "verdict_reveal");
                return new CinematicStep.VerdictReveal(duration);
            }

            case "tier_reveal":
            {
                int duration = Int(el, "duration_ticks", source); // RL7：档位揭示节拍入脚本数据
                RequireDuration(duration, min: 1, source, "tier_reveal");
                return new CinematicStep.TierReveal(duration);
            }

            case "wheel_spin":
            {
                int duration = Int(el, "duration_ticks", source); // RL7：转盘总时长入脚本数据
                RequireDuration(duration, min: 1, source, "wheel_spin");
                int segments = Int(el, "segments", source); // RL7：转盘段数入脚本数据
                Require(
                    segments >= VictorySettlement.MinWheelSegments,
                    $"{source}: wheel_spin.segments 须 ≥{VictorySettlement.MinWheelSegments}（得到 {segments}）");
                JsonElement decelEl = Array(el, "decel_ticks", source);
                Require(decelEl.GetArrayLength() >= 1, $"{source}: wheel_spin.decel_ticks 至少 1 组（减速停顿帧数脚本化）");
                var decel = new List<int>();
                foreach (JsonElement pauseEl in decelEl.EnumerateArray())
                {
                    Require(
                        pauseEl.ValueKind == JsonValueKind.Number && pauseEl.TryGetInt32(out _),
                        $"{source}: wheel_spin.decel_ticks 每组须为整数（得到 {pauseEl}）");
                    int pause = pauseEl.GetInt32();
                    Require(pause >= 1, $"{source}: wheel_spin.decel_ticks 每组须 ≥1（得到 {pause}）");
                    decel.Add(pause);
                }

                for (int i = 1; i < decel.Count; i++)
                {
                    Require(
                        decel[i] >= decel[i - 1],
                        $"{source}: wheel_spin.decel_ticks 须非递减（减速=停顿不减，得到 {decel[i - 1]}→{decel[i]}）");
                }

                Require(
                    decel.Sum() <= duration,
                    $"{source}: wheel_spin.decel_ticks 总和（{decel.Sum()}）不得超过 duration_ticks（{duration}）");
                return new CinematicStep.WheelSpin(duration, segments, decel);
            }

            case "chest_open":
            {
                int duration = Int(el, "duration_ticks", source); // RL7：开箱节拍入脚本数据
                RequireDuration(duration, min: 1, source, "chest_open");
                return new CinematicStep.ChestOpen(duration);
            }

            case "persona_comment":
            {
                int duration = Int(el, "duration_ticks", source); // RL7：点评节拍入脚本数据
                RequireDuration(duration, min: 1, source, "persona_comment");
                return new CinematicStep.PersonaComment(duration);
            }

            default:
                throw new CinematicSchemaException(
                    $"{source}: 未登记步骤 kind（{kind}，封闭动词表 camera_move/highlight/wait_event/speak/sound/screen/ui/choice/checkpoint/verdict_reveal/tier_reveal/wheel_spin/chest_open/persona_comment）");
        }
    }

    private static CinematicWorldCommand ParseCommand(JsonElement el, string source)
    {
        Require(el.ValueKind == JsonValueKind.Object, $"{source}: terminal 命令须为对象");
        string kind = String(el, "kind", source);
        switch (kind)
        {
            case "issue_order":
            {
                string order = String(el, "order", source);
                Require(!string.IsNullOrWhiteSpace(order), $"{source}: issue_order.order 为空白");
                return new CinematicWorldCommand.IssueOrder(order);
            }

            case "enqueue_sim":
            {
                long delay = OptionalLong(el, "delay_ticks", 0);
                Require(delay >= 0, $"{source}: enqueue_sim.delay_ticks 须 ≥0（得到 {delay}）");
                string kindName = String(el, "sim_kind", source);
                Require(
                    Enum.TryParse<SimCommandKind>(kindName, out SimCommandKind simKind),
                    $"{source}: enqueue_sim.sim_kind 未登记（{kindName}）");
                string payload = String(el, "payload", source);
                Require(!string.IsNullOrWhiteSpace(payload), $"{source}: enqueue_sim.payload 为空白");
                return new CinematicWorldCommand.EnqueueSim(delay, simKind, payload);
            }

            default:
                throw new CinematicSchemaException(
                    $"{source}: 未登记终态命令 kind（{kind}，封闭表 issue_order/enqueue_sim）");
        }
    }

    private static CinematicTarget ParseTarget(JsonElement el, string source)
    {
        string type = String(el, "type", source);
        JsonElement cell = Object(el, "cell", source);
        int x = Int(cell, "x", source);
        int y = Int(cell, "y", source);
        return type switch
        {
            "cell" => new CinematicTarget(CinematicTargetKind.Cell, new CellCoord(x, y)),
            "carrier_to" => new CinematicTarget(CinematicTargetKind.CarrierToCell, new CellCoord(x, y)),
            _ => throw new CinematicSchemaException(
                $"{source}: 未登记目标 type（{type}，封闭表 cell/carrier_to）"),
        };
    }

    private static void RequireDuration(int value, int min, string source, string kind) =>
        Require(value >= min, $"{source}: {kind}.duration_ticks 须 ≥{min}（得到 {value}）");

    private static string ToPascal(string snake) => string.Concat(
        snake.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant()));

    // ---- JSON helpers（与 HandcraftedLevelData 同约定，缺字段即异常） ----------

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new CinematicSchemaException(message);
        }
    }

    private static string String(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw new CinematicSchemaException($"{source}: 缺字符串字段 {name}");
        }

        return value.GetString()!;
    }

    private static int Int(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int parsed))
        {
            throw new CinematicSchemaException($"{source}: 缺整数字段 {name}");
        }

        return parsed;
    }

    private static int OptionalInt(JsonElement parent, string name, int fallback)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsed))
        {
            throw new CinematicSchemaException($"字段 {name} 须为整数");
        }

        return parsed;
    }

    private static long OptionalLong(JsonElement parent, string name, long fallback)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long parsed))
        {
            throw new CinematicSchemaException($"字段 {name} 须为长整数");
        }

        return parsed;
    }

    private static JsonElement Object(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new CinematicSchemaException($"{source}: 缺对象字段 {name}");
        }

        return value;
    }

    private static JsonElement Array(JsonElement parent, string name, string source)
    {
        Require(parent.ValueKind == JsonValueKind.Object, $"{source}: {name} 所在节点须为对象");
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new CinematicSchemaException($"{source}: 缺数组字段 {name}");
        }

        return value;
    }
}
