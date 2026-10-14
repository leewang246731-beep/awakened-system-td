using EraCore.Contracts.Foundation;
using EraCore.Sim;

namespace EraCore.Agent.Actions;

/// <summary>
/// One agent-owned task handle (T-068): alarms and scout patrols are the two
/// task families <see cref="SystemActionKind.StopTask"/> addresses through
/// one unified handle space.
/// </summary>
/// <param name="TaskId">Unified handle (<c>alarm-N</c> / <c>scout-N</c>), the StopTask key.</param>
/// <param name="Family">Which family the task belongs to.</param>
/// <param name="DueTick">Alarm due tick (30 Hz domain) — the host rings it at/before this tick.</param>
/// <param name="UntilTick">Patrol expiry tick — the scout stands down at this tick.</param>
/// <param name="Note">Player-visible note (filtered reason / lane id).</param>
public sealed record AgentTaskRecord(
    string TaskId,
    AgentTaskFamily Family,
    long DueTick,
    long UntilTick,
    string Note);

/// <summary>The two StopTask-addressable task families.</summary>
public enum AgentTaskFamily
{
    /// <summary>Scheduled alarm (SetAlarm).</summary>
    Alarm,

    /// <summary>Lane patrol (SpawnScout).</summary>
    Scout,
}

/// <summary>One activated scroll (T-068 bookkeeping tier).</summary>
/// <param name="ScrollId">Content id of the triggered scroll.</param>
/// <param name="AtTick">Tick the trigger was accepted.</param>
public sealed record ScrollActivation(string ScrollId, long AtTick);

/// <summary>
/// The agent-owned runtime state of the 13 T-068 handlers (C-13 full
/// pipeline): the governed store behind directives and tasks that have NO
/// simulation verb to ride on — rally stances, stockpile floors, the focus
/// directive, tower targeting modes, the task board, alarms, scout patrols
/// and the scroll cooldown/activation ledger. Every mutation happens only
/// through a registry handler (RL6 — the state is never handed out
/// writable), and every entry is deterministic in the K-1 tick domain (no
/// wall clock, CS-1.4).
/// <para>
/// Seam notes: the scroll RUNTIME (tick effects, priority, caps — C-18) is
/// T-071's parallel lane; this class owns only the K-3-level bookkeeping the
/// TriggerScroll verb needs to be honest (armed set + 3 s internal
/// cooldown + activation record). T-071 binds runtime effects to the same
/// records. PreAction/WaveEnd hooks that consume due alarms / expired
/// patrols land with T-070.
/// </para>
/// </summary>
public sealed class AgentActionState
{
    /// <summary>Internal scroll cooldown (G.2: 卷轴 3s 冷却, K-1 30 Hz domain).</summary>
    public const int ScrollCooldownTicks = 3 * Simulation.TicksPerSecond; // ci-allow: R7 T-068 附录 G.2 卷轴 3s 内部冷却（冻结域常量，30 tick/s 时域）

    private readonly Func<long> _tick;
    private readonly IReadOnlySet<string> _armedScrolls;
    private readonly Dictionary<string, string> _rallyStances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _stockpileFloors = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _towerModes = new();
    private readonly Dictionary<string, long> _scrollReadyAt = new(StringComparer.Ordinal);
    private readonly List<AgentTaskRecord> _tasks = new();
    private readonly List<ScrollActivation> _scrollActivations = new();
    private IReadOnlyList<string> _todos = Array.Empty<string>();
    private long? _focusTarget;
    private int _alarmSeq;
    private int _scoutSeq;

    /// <param name="tick">Live tick source (K-1 domain); the registry wires the simulation view's tick.</param>
    /// <param name="armedScrolls">Equipped scroll ids (G.2 限已装备卷轴); empty = every trigger refuses honestly.</param>
    public AgentActionState(Func<long> tick, IReadOnlySet<string>? armedScrolls = null)
    {
        ArgumentNullException.ThrowIfNull(tick);
        _tick = tick;
        _armedScrolls = armedScrolls ?? new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Alarm/patrol tasks in creation order (stopped ones removed).</summary>
    public IReadOnlyList<AgentTaskRecord> Tasks => _tasks;

    /// <summary>Scroll activations in trigger order (T-071 runtime input).</summary>
    public IReadOnlyList<ScrollActivation> ScrollActivations => _scrollActivations;

    /// <summary>Current task board (TodoBoard verb rewrites the whole list, G.2 todos[] 全量重写).</summary>
    public IReadOnlyList<string> Todos => _todos;

    /// <summary>Focus-fire directive target (enemy entity id) or null when cleared.</summary>
    public long? FocusTarget => _focusTarget;

    /// <summary>Lane → rally stance directives (SetRally).</summary>
    public IReadOnlyDictionary<string, string> RallyStances => _rallyStances;

    /// <summary>Item id → stockpile floor (SetStockpile).</summary>
    public IReadOnlyDictionary<string, double> StockpileFloors => _stockpileFloors;

    /// <summary>Tower entity id → targeting mode (SetTowerMode).</summary>
    public IReadOnlyDictionary<long, string> TowerModes => _towerModes;

    /// <summary>Sets the rally stance of one lane (language-neutral id, G.2).</summary>
    public void SetRally(string lane, string stance)
    {
        ArgumentException.ThrowIfNullOrEmpty(lane);
        ArgumentException.ThrowIfNullOrEmpty(stance);
        _rallyStances[lane] = stance;
    }

    /// <summary>Sets the stockpile floor of one item.</summary>
    public void SetStockpileFloor(string itemId, double minAmount)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        if (minAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minAmount), minAmount, "Stockpile floor cannot be negative.");
        }

        _stockpileFloors[itemId] = minAmount;
    }

    /// <summary>Sets the focus-fire directive (enemy entity id).</summary>
    public void SetFocusTarget(long enemyId) => _focusTarget = enemyId;

    /// <summary>Sets the targeting mode of one tower.</summary>
    public void SetTowerMode(long towerId, string mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(mode);
        _towerModes[towerId] = mode;
    }

    /// <summary>Rewrites the whole task board.</summary>
    public void WriteTodos(IReadOnlyList<string> todos)
    {
        ArgumentNullException.ThrowIfNull(todos);
        _todos = new List<string>(todos);
    }

    /// <summary>
    /// Registers one alarm due <paramref name="afterSeconds"/> seconds from
    /// now (already schema-clamped to [30,600]).
    /// </summary>
    /// <returns>The unified task handle (<c>alarm-N</c>).</returns>
    public string AddAlarm(double afterSeconds, string reason)
    {
        long now = _tick();
        string taskId = "alarm-" + ++_alarmSeq;
        _tasks.Add(new AgentTaskRecord(taskId, AgentTaskFamily.Alarm, now + SecondsToTicks(afterSeconds), UntilTick: 0, reason));
        return taskId;
    }

    /// <summary>
    /// Registers one scout patrol on <paramref name="lane"/> lasting
    /// <paramref name="durationSeconds"/> seconds (already schema-clamped to
    /// [15,120]).
    /// </summary>
    /// <returns>The unified task handle (<c>scout-N</c>).</returns>
    public string AddPatrol(string lane, double durationSeconds)
    {
        long now = _tick();
        string taskId = "scout-" + ++_scoutSeq;
        _tasks.Add(new AgentTaskRecord(taskId, AgentTaskFamily.Scout, DueTick: 0, now + SecondsToTicks(durationSeconds), lane));
        return taskId;
    }

    /// <summary>
    /// Stops tasks by unified handle: <c>"all"</c> stops every task, an
    /// exact handle stops that one. Stopped tasks leave the roster.
    /// </summary>
    /// <returns>The stopped handles in roster order; empty when nothing matched.</returns>
    public IReadOnlyList<string> StopTasks(string taskId)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        if (taskId != "all" && _tasks.All(task => task.TaskId != taskId))
        {
            return Array.Empty<string>();
        }

        var stopped = new List<string>();
        for (int i = _tasks.Count - 1; i >= 0; i--)
        {
            if (taskId == "all" || _tasks[i].TaskId == taskId)
            {
                stopped.Insert(0, _tasks[i].TaskId);
                _tasks.RemoveAt(i);
            }
        }

        return stopped;
    }

    /// <summary>
    /// Triggers one scroll: the armed-set check and the 3 s internal
    /// cooldown both gate here (G.2). On success the cooldown clock starts
    /// and the activation is recorded (the runtime effect lands with T-071).
    /// </summary>
    /// <param name="scrollId">Content id of the scroll.</param>
    /// <param name="readyAgainTick">Out: tick the scroll becomes triggerable again.</param>
    /// <returns>Null on success, otherwise a machine-readable refusal code.</returns>
    public string? TryTriggerScroll(string scrollId, out long readyAgainTick)
    {
        ArgumentException.ThrowIfNullOrEmpty(scrollId);
        long now = _tick();
        readyAgainTick = 0;
        if (!_armedScrolls.Contains(scrollId))
        {
            return $"scroll-not-equipped:{scrollId}:only equipped scrolls may fire (G.2)";
        }

        if (_scrollReadyAt.TryGetValue(scrollId, out long readyAt) && now < readyAt)
        {
            return $"scroll-cooldown:{scrollId}:ready-at-tick:{readyAt}";
        }

        readyAgainTick = now + ScrollCooldownTicks;
        _scrollReadyAt[scrollId] = readyAgainTick;
        _scrollActivations.Add(new ScrollActivation(scrollId, now));
        return null;
    }

    /// <summary>Seconds → ticks in the K-1 30 Hz domain (deterministic rounding).</summary>
    public static long SecondsToTicks(double seconds) =>
        (long)Math.Round(seconds * Simulation.TicksPerSecond, MidpointRounding.AwayFromZero);
}
