namespace EraCore.Cinematics;

using EraCore.Progression;

/// <summary>Lifecycle phase of one timeline run (T-086, C-24).</summary>
public enum CinematicPhase
{
    /// <summary>Constructed, <see cref="CinematicTimeline.Start"/> not called yet.</summary>
    NotStarted = 0,

    /// <summary>Advancing steps on <see cref="CinematicTimeline.Tick"/> calls.</summary>
    Playing = 1,

    /// <summary>Parked on a <see cref="CinematicStep.Choice"/> step until the host resolves it.</summary>
    AwaitingChoice = 2,

    /// <summary>Terminal: every remaining beat was skipped or played out.</summary>
    Completed = 3,
}

/// <summary>
/// One observable presentation beat emitted by the timeline (T-086). Effects
/// are pure data — the host adapter (Godot scene / test harness) renders them;
/// the timeline itself performs no I/O (CS-1.5 同步单线程 discipline: effects
/// of one <see cref="CinematicTimeline.Tick"/> return in that call).
/// </summary>
public abstract record CinematicEffect
{
    /// <summary>Camera move began toward the target (runs for the step duration).</summary>
    public sealed record CameraMove(CinematicTarget Target, int DurationTicks) : CinematicEffect;

    /// <summary>Target highlight began (flavor decides the host rendering).</summary>
    public sealed record Highlight(CinematicTarget Target, CinematicHighlightFlavor Flavor) : CinematicEffect;

    /// <summary>Subtitle/bubble line began (runs for the step duration).</summary>
    public sealed record Speak(string Speaker, string Text, int DurationTicks) : CinematicEffect;

    /// <summary>Sound cue fired (closed cue set of <see cref="CinematicScript.SoundCues"/>).</summary>
    public sealed record SoundCue(string Cue) : CinematicEffect;

    /// <summary>Screen flavor began (black_fade_in / fade_out).</summary>
    public sealed record Screen(string Flavor, int DurationTicks) : CinematicEffect;

    /// <summary>UI surface began (holo_panel_expand / ledger_reveal with its rows).</summary>
    public sealed record UiSurface(string Surface, IReadOnlyList<string> Ledger) : CinematicEffect;

    /// <summary>The choice card went out (prompt + option labels — the host renders the buttons).</summary>
    public sealed record ChoicePrompted(string Prompt, IReadOnlyList<string> OptionLabels) : CinematicEffect;

    /// <summary>One option was resolved (player pick, or the declared default on skip).</summary>
    public sealed record ChoiceResolved(int Index) : CinematicEffect;

    /// <summary>A skip checkpoint was reached (跳过安全位 marker).</summary>
    public sealed record SkipPointReached(string Id) : CinematicEffect;

    /// <summary>The run was skipped at the given step index; <see cref="Completed"/> follows.</summary>
    public sealed record Skipped(int AtStep) : CinematicEffect;

    /// <summary>判分结果展示 began — the three-objective checklist rows of the DECIDED settlement (T-087, RL12).</summary>
    public sealed record SettlementVerdict(IReadOnlyList<VictoryVerdictRow> Rows) : CinematicEffect;

    /// <summary>档位揭示 began — decided tier + its chest id + completed count.</summary>
    public sealed record SettlementTierRevealed(ObjectiveTier Tier, string ChestTierId, int CompletedCount)
        : CinematicEffect;

    /// <summary>幸运转盘 spin began — decided stop index + scripted deceleration profile (减速停顿帧数).</summary>
    public sealed record WheelSpinStarted(int Segments, int StopIndex, IReadOnlyList<int> DecelTicks, int DurationTicks)
        : CinematicEffect;

    /// <summary>The wheel expired ON its decided segment — the landing equals the pre-decided result (演出倒推).</summary>
    public sealed record WheelLanded(int StopIndex, string ItemId, RewardRarity Rarity) : CinematicEffect;

    /// <summary>宝箱开箱 began — decided items + light layers of the tier (光效分层：黄金多一层).</summary>
    public sealed record SettlementChestOpened(IReadOnlyList<GrantedChestItem> Items, int LightLayers, string ChestTierId)
        : CinematicEffect;

    /// <summary>系统一句话点评 — the decided persona×trust-badge line (with the T-075 cross-tier echo when any).</summary>
    public sealed record SettlementCommentarySpoken(DecidedCommentary Commentary) : CinematicEffect;

    /// <summary>Terminal state reached (natural end or skip) — world commands are due in the host.</summary>
    public sealed record Completed() : CinematicEffect;
}

/// <summary>
/// The pure tick-driven cinematic runner (T-086, C-24, F-113/F-114): advances
/// a <see cref="CinematicScript"/> step by step on explicit
/// <see cref="Tick"/> calls, emits presentation effects as return values,
/// waits on host-signaled world events and parks on the interactive choice
/// step. Zero engine types, zero clock/rng reads (CS-1.4: time is the tick
/// count of the driving host) — determinism is total: same script + same
/// NotifyEvent/SelectChoice schedule ⇒ identical effect trace.
/// <para>
/// 零污染纪律 (the acceptance invariant): the timeline NEVER touches the
/// simulation. Its only world writes are the script-declared
/// <see cref="CinematicWorldCommand"/>s handed out through
/// <see cref="TakePendingCommands"/> — the host routes them through the real
/// K-6 command queue / five-stage pipeline. Skipping jumps to the same
/// terminal command set as completing (选项默认或脚本终态)，so the run's world
/// effect is independent of how much of the performance was watched.
/// </para>
/// <para>
/// 结算步与保护演出 (T-087, RL12 先定后演): settlement steps
/// (verdict_reveal/tier_reveal/wheel_spin/chest_open/persona_comment) read
/// the bound <see cref="DecidedVictoryPerformance"/> and nothing else — the
/// timeline draws NO randomness, so full play, acceleration
/// (<see cref="SetBoost"/>) and mid-performance abandonment all read the
/// same decided bytes. A script declaring <see cref="CinematicScript.MinimumPlayTicks"/>
/// is protected: no full skip exists (长按加速不可全跳，§12 映射#10) and
/// completion holds to the floor.
/// </para>
/// <para>
/// Step rhythm (frozen): activating a step consumes one tick (its enter
/// effects fire on that tick); a timed step then runs
/// <c>DurationTicks</c> further countdown ticks; the successor activates on
/// the tick a step expires. Instantaneous steps (sound/highlight without
/// duration/checkpoint) complete on their activation tick. Boost multiplies
/// the countdown consumption only — the rhythm contract is the boost-1 view.
/// </para>
/// </summary>
public sealed class CinematicTimeline
{
    /// <summary>Boost ceiling (§12 映射#10：长按加速 ≤4×——保护期待感).</summary>
    public const int MaxBoost = 4; // ci-allow: R7 T-087 长按加速倍速上限 4× 为 v3.2 §12 映射#10 冻结交互域常量（非内容平衡数值）

    private readonly CinematicScript _script;
    private readonly DecidedVictoryPerformance? _settlement;
    private readonly HashSet<string> _eventBuffer = new();
    private readonly List<CinematicWorldCommand> _pending = new();
    private CinematicStep? _active;
    private int _cursor;
    private int _remaining;
    private int _boost = 1;
    private int? _selectedChoice;

    /// <exception cref="ArgumentNullException">script is null.</exception>
    public CinematicTimeline(CinematicScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _script = script;
    }

    /// <summary>
    /// Settlement binding (RL12): the timeline that performs settlement
    /// steps reads the decided performance — decided BEFORE the performance
    /// exists — and never rolls anything itself.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public CinematicTimeline(CinematicScript script, DecidedVictoryPerformance settlement)
        : this(script)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        _settlement = settlement;
    }

    /// <summary>The script this run performs.</summary>
    public CinematicScript Script => _script;

    /// <summary>The bound decided settlement (null on non-settlement runs).</summary>
    public DecidedVictoryPerformance? Settlement => _settlement;

    /// <summary>Current lifecycle phase.</summary>
    public CinematicPhase Phase { get; private set; } = CinematicPhase.NotStarted;

    /// <summary>Index of the active (or last active) step; -1 before <see cref="Start"/>.</summary>
    public int CurrentStepIndex => _cursor - 1;

    /// <summary>The active step record (null while between steps / completed).</summary>
    public CinematicStep? CurrentStep => _active;

    /// <summary>Number of <see cref="Tick"/> calls consumed so far.</summary>
    public int ElapsedTicks { get; private set; }

    /// <summary>True when this run ended via <see cref="Skip"/> rather than playing out.</summary>
    public bool Skipped { get; private set; }

    /// <summary>Current long-press acceleration factor (1 = natural speed, ≤ <see cref="MaxBoost"/>).</summary>
    public int BoostFactor => _boost;

    /// <summary>Whether a full skip exists for this script (false = protected settlement performance, 不可全跳).</summary>
    public bool CanSkip => _script.MinimumPlayTicks is null;

    /// <summary>The resolved choice option index, once selected (null before the choice resolves).</summary>
    public int? SelectedChoice => _selectedChoice;

    /// <summary>
    /// The terminal command set THIS run will have applied by completion:
    /// the selected option's OnConfirm (or the declared default when the
    /// choice is skipped past), or the script-level terminal commands of a
    /// choice-less script. Read-only projection of declared data — the
    /// application channel is <see cref="TakePendingCommands"/>.
    /// </summary>
    public IReadOnlyList<CinematicWorldCommand> TerminalCommands
    {
        get
        {
            foreach (CinematicStep step in _script.Steps)
            {
                if (step is CinematicStep.Choice choice)
                {
                    int index = _selectedChoice ?? choice.DefaultIndex;
                    return choice.Options[index].OnConfirm;
                }
            }

            return _script.TerminalCommands;
        }
    }

    /// <summary>
    /// Begins the performance: activates step 0 and returns its enter
    /// effects. A script whose first step is a choice immediately parks in
    /// <see cref="CinematicPhase.AwaitingChoice"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public IReadOnlyList<CinematicEffect> Start()
    {
        if (Phase != CinematicPhase.NotStarted)
        {
            throw new InvalidOperationException($"Start called twice (phase={Phase}).");
        }

        Phase = CinematicPhase.Playing;
        var effects = new List<CinematicEffect>();
        Activate(effects);
        return effects;
    }

    /// <summary>
    /// Advances one tick: checks the pending wait-event, counts the active
    /// timed step down, and activates the next step on the tick the previous
    /// one expires. Returns the effects fired on this tick (empty while
    /// waiting or already completed).
    /// </summary>
    /// <exception cref="InvalidOperationException">Not started yet.</exception>
    public IReadOnlyList<CinematicEffect> Tick()
    {
        var effects = new List<CinematicEffect>();
        switch (Phase)
        {
            case CinematicPhase.NotStarted:
                throw new InvalidOperationException("Tick before Start — call Start first.");
            case CinematicPhase.AwaitingChoice:
            case CinematicPhase.Completed:
                return effects;
        }

        ElapsedTicks++;

        if (_active is CinematicStep.WaitEvent wait)
        {
            if (!_eventBuffer.Remove(wait.EventKey))
            {
                return effects; // still waiting on the world
            }

            _active = null; // satisfied this tick — the successor activates next tick
            return effects;
        }

        if (_active is not null)
        {
            // Timed countdown (activation tick itself does not count down).
            // Boost multiplies the consumption only (长按加速)：the boost-1
            // view of the rhythm is unchanged.
            _remaining -= _boost;
            if (_remaining > 0)
            {
                return effects;
            }

            if (_active is CinematicStep.WheelSpin)
            {
                // 转盘到停：落点=既定结果（演出倒推）——到停效果在到期拍发出。
                DecidedVictoryPerformance decided = RequiredSettlement();
                effects.Add(new CinematicEffect.WheelLanded(
                    decided.Wheel.StopIndex,
                    decided.Wheel.Granted.ItemId,
                    decided.Wheel.Granted.Rarity));
            }

            _active = null;
            return effects; // expires this tick — the successor activates next tick
        }

        Activate(effects);
        return effects;
    }

    /// <summary>
    /// Signals one world-observed event key (the host's read-only snapshot
    /// observation, e.g. the E1 carrier delivery that restores tower ammo).
    /// Buffered until the matching <see cref="CinematicStep.WaitEvent"/>
    /// step consumes it.
    /// </summary>
    /// <exception cref="ArgumentException">key null or blank.</exception>
    public void NotifyEvent(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Event key must not be blank.", nameof(key));
        }

        _eventBuffer.Add(key);
    }

    /// <summary>
    /// 长按加速 (T-087, §12 映射#10): multiplies the timed-step countdown
    /// consumption. Factor domain [1, <see cref="MaxBoost"/>] — 1 is natural
    /// speed (release). Acceleration changes pacing ONLY: decided bytes,
    /// effect ORDER and the landing invariant are boost-invariant
    /// (RL12 先定后演). Part of the determinism contract: same script +
    /// same NotifyEvent/SelectChoice/SetBoost schedule ⇒ identical trace.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">factor outside [1, MaxBoost].</exception>
    public void SetBoost(int factor)
    {
        if (factor < 1 || factor > MaxBoost)
        {
            throw new ArgumentOutOfRangeException(
                nameof(factor), factor, $"boost factor must be within [1,{MaxBoost}].");
        }

        _boost = factor;
    }

    /// <summary>
    /// Resolves the parked choice step with the picked option: queues the
    /// option's declared OnConfirm commands for the host and resumes
    /// playback.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not parked on a choice.</exception>
    /// <exception cref="ArgumentOutOfRangeException">index outside the option list.</exception>
    public IReadOnlyList<CinematicEffect> SelectChoice(int index)
    {
        if (Phase != CinematicPhase.AwaitingChoice || _active is not CinematicStep.Choice choice)
        {
            throw new InvalidOperationException($"SelectChoice called outside a parked choice (phase={Phase}).");
        }

        if (index < 0 || index >= choice.Options.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"Option index must be in [0,{choice.Options.Count}).");
        }

        _selectedChoice = index;
        _pending.AddRange(choice.Options[index].OnConfirm);
        _active = null;
        Phase = CinematicPhase.Playing;
        return new List<CinematicEffect> { new CinematicEffect.ChoiceResolved(index) };
    }

    /// <summary>
    /// Skips to the terminal state from ANY moment (演出可跳过): an unresolved
    /// choice resolves to its declared default — whether parked on it or
    /// jumped past it — (the default's commands are queued exactly as a pick
    /// would), a choice-less script queues its terminal commands, and the run
    /// lands completed. The world effect of the run is the declared terminal
    /// command set — identical to playing the same choice out.
    /// <para>
    /// PROTECTED performances refuse the skip outright (T-087 加速不可全跳):
    /// a script declaring <see cref="CinematicScript.MinimumPlayTicks"/> has
    /// no full-skip path — 长按加速 is the only fast-forward. The refusal is
    /// an explicit contract, not a silent no-op, so mis-wired hosts fail fast.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Not started; already completed (inert empty return); or the script is
    /// a protected settlement performance (no full skip exists).
    /// </exception>
    public IReadOnlyList<CinematicEffect> Skip()
    {
        if (Phase == CinematicPhase.NotStarted)
        {
            throw new InvalidOperationException("Skip before Start — nothing to skip.");
        }

        if (Phase == CinematicPhase.Completed)
        {
            return Array.Empty<CinematicEffect>();
        }

        if (_script.MinimumPlayTicks is not null)
        {
            throw new InvalidOperationException(
                $"结算演出受保护（{Script.Id}）：长按可加速至 {MaxBoost}×，无全跳（§12 映射#10/F-002）");
        }

        var effects = new List<CinematicEffect>();

        // An un-resolved choice (parked OR jumped past) resolves to the
        // declared default — the skip-before-choice path must land on the
        // same terminal command set as a full play of the default.
        if (_selectedChoice is null && ChoiceStep() is { } choice)
        {
            _selectedChoice = choice.DefaultIndex;
            _pending.AddRange(choice.Options[choice.DefaultIndex].OnConfirm);
            effects.Add(new CinematicEffect.ChoiceResolved(choice.DefaultIndex));
        }

        Skipped = true;
        effects.Add(new CinematicEffect.Skipped(Math.Max(0, _cursor - 1)));
        CompleteRun(effects);
        return effects;
    }

    /// <summary>
    /// Drains the world commands that have come due (choice resolution,
    /// completion of a choice-less script) — the host applies them through
    /// the real pipeline/queue immediately after draining.
    /// </summary>
    public IReadOnlyList<CinematicWorldCommand> TakePendingCommands()
    {
        if (_pending.Count == 0)
        {
            return Array.Empty<CinematicWorldCommand>();
        }

        CinematicWorldCommand[] drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }

    private void Activate(List<CinematicEffect> effects)
    {
        if (_cursor >= _script.Steps.Count)
        {
            if (_script.MinimumPlayTicks is int floor && ElapsedTicks < floor)
            {
                // 保护期待感（§12 映射#10）：加速不可把演出压到保护线下——
                // 尾步已尽则静默持有到保护线拍，再入终态（纯拍数规则，确定性）。
                return;
            }

            CompleteRun(effects);
            return;
        }

        CinematicStep step = _script.Steps[_cursor++];
        _active = step;
        switch (step)
        {
            case CinematicStep.CameraMove camera:
                effects.Add(new CinematicEffect.CameraMove(camera.Target, camera.DurationTicks));
                _remaining = camera.DurationTicks;
                break;
            case CinematicStep.Highlight highlight:
                effects.Add(new CinematicEffect.Highlight(highlight.Target, highlight.Flavor));
                _remaining = highlight.DurationTicks;
                break;
            case CinematicStep.WaitEvent:
                _remaining = 0;
                break;
            case CinematicStep.Speak speak:
                effects.Add(new CinematicEffect.Speak(speak.Speaker, speak.Text, speak.DurationTicks));
                _remaining = speak.DurationTicks;
                break;
            case CinematicStep.Sound sound:
                effects.Add(new CinematicEffect.SoundCue(sound.Cue));
                _remaining = 0; // instantaneous cue — completes on activation tick
                break;
            case CinematicStep.Screen screen:
                effects.Add(new CinematicEffect.Screen(screen.Flavor, screen.DurationTicks));
                _remaining = screen.DurationTicks;
                break;
            case CinematicStep.Ui ui:
                effects.Add(new CinematicEffect.UiSurface(ui.Surface, ui.Ledger));
                _remaining = ui.DurationTicks;
                break;
            case CinematicStep.Choice choice:
                effects.Add(new CinematicEffect.ChoicePrompted(
                    choice.Prompt, choice.Options.Select(option => option.Label).ToArray()));
                Phase = CinematicPhase.AwaitingChoice;
                _remaining = 0;
                break;
            case CinematicStep.SkipPoint skipPoint:
                effects.Add(new CinematicEffect.SkipPointReached(skipPoint.Id));
                _remaining = 0; // marker — completes on activation tick
                break;
            case CinematicStep.VerdictReveal verdict:
                effects.Add(new CinematicEffect.SettlementVerdict(RequiredSettlement().VerdictRows));
                _remaining = verdict.DurationTicks;
                break;
            case CinematicStep.TierReveal tier:
            {
                DecidedVictoryPerformance decided = RequiredSettlement();
                effects.Add(new CinematicEffect.SettlementTierRevealed(
                    decided.Tier, decided.ChestTierId, decided.VerdictRows.Count(row => row.Passed)));
                _remaining = tier.DurationTicks;
                break;
            }

            case CinematicStep.WheelSpin wheel:
            {
                DecidedVictoryPerformance decided = RequiredSettlement();
                if (decided.Wheel.Segments.Count != wheel.Segments)
                {
                    throw new InvalidOperationException(
                        $"转盘段数不一致：脚本 {wheel.Segments} 段 vs 既定 {decided.Wheel.Segments.Count} 段"
                        + "（演出倒推要求两者同源——先定布局，再演转盘）");
                }

                effects.Add(new CinematicEffect.WheelSpinStarted(
                    wheel.Segments, decided.Wheel.StopIndex, wheel.DecelTicks, wheel.DurationTicks));
                _remaining = wheel.DurationTicks;
                break;
            }

            case CinematicStep.ChestOpen chest:
            {
                DecidedVictoryPerformance decided = RequiredSettlement();
                effects.Add(new CinematicEffect.SettlementChestOpened(
                    decided.ChestItems, decided.ChestLightLayers, decided.ChestTierId));
                _remaining = chest.DurationTicks;
                break;
            }

            case CinematicStep.PersonaComment comment:
                effects.Add(new CinematicEffect.SettlementCommentarySpoken(RequiredSettlement().Commentary));
                _remaining = comment.DurationTicks;
                break;
        }

        // Instantaneous steps (sound / zero-duration highlight / checkpoint)
        // complete on their activation tick — the successor activates next
        // tick. WaitEvent and Choice stay active (they block instead).
        if (_remaining == 0 && step is not (CinematicStep.WaitEvent or CinematicStep.Choice))
        {
            _active = null;
        }
    }

    /// <summary>
    /// The script's single choice step, when it has one (schema caps scripts
    /// at one interactive decision).
    /// </summary>
    private CinematicStep.Choice? ChoiceStep()
    {
        foreach (CinematicStep step in _script.Steps)
        {
            if (step is CinematicStep.Choice choice)
            {
                return choice;
            }
        }

        return null;
    }

    /// <summary>
    /// The bound decided settlement — settlement steps read it and nothing
    /// else (RL12 先定后演)：未绑定即结算步激活失败（演出不得自行随机、
    /// 也不得凭空编造判分结果）。
    /// </summary>
    /// <exception cref="InvalidOperationException">The run carries no settlement binding.</exception>
    private DecidedVictoryPerformance RequiredSettlement() => _settlement
        ?? throw new InvalidOperationException(
            "结算演出步需要既定结算数据（RL12 先定后演）：请用 CinematicTimeline(script, decided) 构造——演出只回放，不自定结果");

    private void CompleteRun(List<CinematicEffect> effects)
    {
        // Terminal command ownership: a choice script's writes are the option
        // commands (queued at resolution/default); a choice-less script's
        // writes are its declared terminal commands, queued here.
        bool hasChoice = _script.Steps.Any(step => step is CinematicStep.Choice);
        if (!hasChoice)
        {
            _pending.AddRange(_script.TerminalCommands);
        }

        _active = null;
        Phase = CinematicPhase.Completed;
        effects.Add(new CinematicEffect.Completed());
    }
}
