namespace EraCore.CampaignLoop;

/// <summary>
/// One station of the single-level game flow (F-001, v3.2 §3 单局循环):
/// the campaign grid screen (thin E1 form of <see cref="GameFlowState.Menu"/>
/// — 选图), the running level, the pause overlay, and the two terminal
/// outcomes. Failure consequences on <see cref="GameFlowState.Defeat"/> are
/// delegated to <c>Checkpoint.FailureTiers</c> (T-057 普通关/终局关分档);
/// this machine only owns the flow legality.
/// </summary>
public enum GameFlowState
{
    /// <summary>Campaign-grid screen (选图薄版) — between levels.</summary>
    Menu = 0,

    /// <summary>Level running: the simulation is allowed to step.</summary>
    InLevel = 1,

    /// <summary>Pause overlay: simulation frozen, exit-to-menu becomes legal.</summary>
    Paused = 2,

    /// <summary>Level won: 胜波达成且场清, settlement banking done.</summary>
    Victory = 3,

    /// <summary>Level lost: failure-tier consequences pending acknowledgement.</summary>
    Defeat = 4,
}

/// <summary>
/// One legal state transition of <see cref="GameStateMachine"/> (F-001) —
/// the audit trail the full-loop assertions walk (T-059 E2E 全链状态迁移).
/// </summary>
/// <param name="Kind">Which transition fired.</param>
/// <param name="From">State before the transition.</param>
/// <param name="To">State after the transition.</param>
public sealed record GameFlowTransition(GameFlowTransitionKind Kind, GameFlowState From, GameFlowState To);

/// <summary>
/// The legal-transition table of one game loop (F-001, T-059):
/// Menu→InLevel→{Paused↔InLevel, Victory, Defeat}, Paused→Menu
/// (中途暂停→退出, the mid-level save seam), Victory→Menu
/// (结算入库后返回选图), Defeat→Menu (failure tiers acknowledged).
/// Every other edge throws <see cref="InvalidOperationException"/> — the
/// machine is the single legality authority, callers never set state
/// directly. Transitions append to an in-order <see cref="History"/> so a
/// resumed session can be compared against its pre-exit machine
/// field-for-field (中途存读档无损断言之一).
/// </summary>
public sealed class GameStateMachine
{
    private readonly List<GameFlowTransition> _history = new();

    /// <summary>Initial state of every fresh machine (the grid screen).</summary>
    public GameFlowState State { get; private set; } = GameFlowState.Menu;

    /// <summary>Every transition fired so far, in firing order.</summary>
    public IReadOnlyList<GameFlowTransition> History => _history;

    /// <summary>Number of transitions fired (0 = untouched machine).</summary>
    public long TransitionCount => _history.Count;

    /// <summary>Menu→InLevel: the selected level starts stepping.</summary>
    /// <exception cref="InvalidOperationException">not at Menu.</exception>
    public void StartLevel()
    {
        Require(GameFlowState.Menu, GameFlowTransitionKind.StartLevel);
        Move(GameFlowTransitionKind.StartLevel, GameFlowState.InLevel);
    }

    /// <summary>InLevel→Paused: freeze the simulation.</summary>
    /// <exception cref="InvalidOperationException">not InLevel.</exception>
    public void Pause()
    {
        Require(GameFlowState.InLevel, GameFlowTransitionKind.Pause);
        Move(GameFlowTransitionKind.Pause, GameFlowState.Paused);
    }

    /// <summary>Paused→InLevel: unfreeze (also the post-resume re-entry).</summary>
    /// <exception cref="InvalidOperationException">not Paused.</exception>
    public void Resume()
    {
        Require(GameFlowState.Paused, GameFlowTransitionKind.Resume);
        Move(GameFlowTransitionKind.Resume, GameFlowState.InLevel);
    }

    /// <summary>
    /// Paused→Menu: the 中途暂停→退出 edge — legal ONLY from the pause
    /// overlay (never straight out of a running level), which is what makes
    /// "pause, save, exit" one atomic host flow.
    /// </summary>
    /// <exception cref="InvalidOperationException">not Paused.</exception>
    public void SaveAndExit()
    {
        Require(GameFlowState.Paused, GameFlowTransitionKind.SaveAndExit);
        Move(GameFlowTransitionKind.SaveAndExit, GameFlowState.Menu);
    }

    /// <summary>
    /// InLevel→Victory: 胜波数 WinWaves(t) 达成且场清 (fired by the session's
    /// victory predicate, never by the host directly).
    /// </summary>
    /// <exception cref="InvalidOperationException">not InLevel.</exception>
    public void Win()
    {
        Require(GameFlowState.InLevel, GameFlowTransitionKind.Win);
        Move(GameFlowTransitionKind.Win, GameFlowState.Victory);
    }

    /// <summary>InLevel→Defeat: level lost, tier consequences pending.</summary>
    /// <exception cref="InvalidOperationException">not InLevel.</exception>
    public void Lose()
    {
        Require(GameFlowState.InLevel, GameFlowTransitionKind.Lose);
        Move(GameFlowTransitionKind.Lose, GameFlowState.Defeat);
    }

    /// <summary>Victory→Menu: settlement banked, back on the grid (选下一张图).</summary>
    /// <exception cref="InvalidOperationException">not Victory.</exception>
    public void CompleteLevel()
    {
        Require(GameFlowState.Victory, GameFlowTransitionKind.CompleteLevel);
        Move(GameFlowTransitionKind.CompleteLevel, GameFlowState.Menu);
    }

    /// <summary>
    /// Defeat→Menu: the FailureTiers outcome has been applied/acknowledged
    /// (普通关=检查点重开或终局关=清本关存档, T-057).
    /// </summary>
    /// <exception cref="InvalidOperationException">not Defeat.</exception>
    public void AcknowledgeDefeat()
    {
        Require(GameFlowState.Defeat, GameFlowTransitionKind.AcknowledgeDefeat);
        Move(GameFlowTransitionKind.AcknowledgeDefeat, GameFlowState.Menu);
    }

    private void Require(GameFlowState expected, GameFlowTransitionKind kind)
    {
        if (State != expected)
        {
            throw new InvalidOperationException(
                $"Transition {kind} requires state {expected} but the machine is in {State}.");
        }
    }

    private void Move(GameFlowTransitionKind kind, GameFlowState to)
    {
        _history.Add(new GameFlowTransition(kind, State, to));
        State = to;
    }
}

/// <summary>The eight legal transitions of <see cref="GameStateMachine"/> (F-001).</summary>
public enum GameFlowTransitionKind
{
    /// <summary>Menu→InLevel.</summary>
    StartLevel = 0,

    /// <summary>InLevel→Paused.</summary>
    Pause = 1,

    /// <summary>Paused→InLevel.</summary>
    Resume = 2,

    /// <summary>Paused→Menu (中途暂停→退出).</summary>
    SaveAndExit = 3,

    /// <summary>InLevel→Victory.</summary>
    Win = 4,

    /// <summary>InLevel→Defeat.</summary>
    Lose = 5,

    /// <summary>Victory→Menu (结算入库后返回选图).</summary>
    CompleteLevel = 6,

    /// <summary>Defeat→Menu.</summary>
    AcknowledgeDefeat = 7,
}
