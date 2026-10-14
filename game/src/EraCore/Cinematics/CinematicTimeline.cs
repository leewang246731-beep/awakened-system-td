namespace EraCore.Cinematics;

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
/// Step rhythm (frozen): activating a step consumes one tick (its enter
/// effects fire on that tick); a timed step then runs
/// <c>DurationTicks</c> further countdown ticks; the successor activates on
/// the tick a step expires. Instantaneous steps (sound/highlight without
/// duration/checkpoint) complete on their activation tick.
/// </para>
/// </summary>
public sealed class CinematicTimeline
{
    private readonly CinematicScript _script;
    private readonly HashSet<string> _eventBuffer = new();
    private readonly List<CinematicWorldCommand> _pending = new();
    private CinematicStep? _active;
    private int _cursor;
    private int _remaining;
    private int? _selectedChoice;

    /// <exception cref="ArgumentNullException">script is null.</exception>
    public CinematicTimeline(CinematicScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _script = script;
    }

    /// <summary>The script this run performs.</summary>
    public CinematicScript Script => _script;

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
            _remaining--;
            if (_remaining > 0)
            {
                return effects;
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
    /// </summary>
    /// <exception cref="InvalidOperationException">Not started, or already completed.</exception>
    public IReadOnlyList<CinematicEffect> Skip()
    {
        if (Phase == CinematicPhase.NotStarted)
        {
            throw new InvalidOperationException("Skip before Start — nothing to skip.");
        }

        var effects = new List<CinematicEffect>();
        if (Phase == CinematicPhase.Completed)
        {
            return effects;
        }

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
                break;            case CinematicStep.Screen screen:
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
