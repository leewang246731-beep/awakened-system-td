using System.Globalization;
using EraCore.Combat;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Agent;
using EraCore.Contracts.Foundation;
using EraCore.Sim;

namespace EraCore.Scrolls;

/// <summary>Outcome of one equip attempt (rule 3: 常驻上限拒绝).</summary>
public enum ScrollEquipResult
{
    /// <summary>Equipped.</summary>
    Ok,

    /// <summary>Same scroll id is already resident.</summary>
    RejectedDuplicate,

    /// <summary>Resident cap 6+2×E reached (F.1).</summary>
    RejectedCapExceeded,

    /// <summary>Scroll unlocks at a later era.</summary>
    RejectedEraLocked,
}

/// <summary>Outcome of one TriggerScroll-lane invocation (manual/system fire of an action card).</summary>
public enum ScrollInvokeStatus
{
    /// <summary>Fired.</summary>
    Ok,

    /// <summary>Scroll id is not resident.</summary>
    NotEquipped,

    /// <summary>Condition cards never fire manually — they are tick-evaluated.</summary>
    NotActionCard,

    /// <summary>Own data cooldown not yet over (e.g. SC-003 60 s).</summary>
    CooldownNotReady,

    /// <summary>Rule 1: this scroll already acted inside the current 1 s scroll tick.</summary>
    BudgetExhausted,

    /// <summary>Every addressed tower is locked away (system override / higher-priority scroll / per-tower limit).</summary>
    Suppressed,

    /// <summary>No lawful target exists right now (e.g. FocusFire with no living enemy in scope).</summary>
    NoTargets,
}

/// <summary>Observable outcome of one scroll fire attempt (auto or manual).</summary>
public enum ScrollFireOutcome
{
    /// <summary>Action executed (≥1 verb dispatched or effect applied).</summary>
    Executed,

    /// <summary>Rule 2/4: all addressed towers were taken (priority arbitration or system override).</summary>
    SuppressedAllTargets,

    /// <summary>Per-tower-per-wave cap reached (SC-004 每塔每波 1 次).</summary>
    PerTowerLimitReached,

    /// <summary>The scroll's own effect is still active — re-fire suppressed until it ends.</summary>
    AlreadyActive,

    /// <summary>No lawful target in scope.</summary>
    NoTargets,
}

/// <summary>One engine-tracked timed modifier (the combat/logistics layers read these; T-071 runtime state).</summary>
/// <param name="ScrollId">Scroll that owns the effect.</param>
/// <param name="Kind">Effect primitive (closed list).</param>
/// <param name="Magnitude">Fraction / multiplier / cells per kind.</param>
/// <param name="ZoneId">Zone of a zone-scoped effect; null otherwise.</param>
/// <param name="TowerClass">Class filter of a class-scoped effect; null otherwise.</param>
/// <param name="StartedTick">30 Hz tick the effect started.</param>
/// <param name="UntilTick">Tick the effect ends (exclusive); ignored while <paramref name="UntilWaveEnd"/> is set.</param>
/// <param name="UntilWaveEnd">Persists to the end of the current wave (SC-007).</param>
/// <param name="ClaimedTowerIds">Towers this effect holds in control (priority arbitration bookkeeping).</param>
public sealed record ActiveScrollEffect(
    string ScrollId,
    ScrollEffectKind Kind,
    double Magnitude,
    string? ZoneId,
    TowerClass? TowerClass,
    long StartedTick,
    long UntilTick,
    bool UntilWaveEnd,
    IReadOnlyList<long> ClaimedTowerIds);

/// <summary>One fire-attempt entry of the engine journal (observability; not hashed beyond the digest).</summary>
/// <param name="ScrollId">Scroll that attempted.</param>
/// <param name="Tick">30 Hz tick.</param>
/// <param name="Manual">True when fired through the TriggerScroll lane.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Verb">Verb dispatched; null when no verb step ran.</param>
/// <param name="VerbTargetId">Enemy id of a FocusFire dispatch; null otherwise.</param>
public sealed record ScrollFireRecord(
    string ScrollId,
    long Tick,
    bool Manual,
    ScrollFireOutcome Outcome,
    string? Verb,
    long? VerbTargetId);

/// <summary>Live budget probe of one scroll for rule-1 assertions (1 判定+1 动作 per 1 s tick).</summary>
/// <param name="Window">Scroll-tick window id (tick ÷ 30) the probe reflects.</param>
/// <param name="EvaluatedThisWindow">A condition evaluation already happened in this window.</param>
/// <param name="ActedThisWindow">An action already happened in this window.</param>
/// <param name="CooldownReadyTick">First tick the scroll may act again.</param>
public sealed record ScrollBudgetProbe(long Window, bool EvaluatedThisWindow, bool ActedThisWindow, long CooldownReadyTick);

/// <summary>Trigger context a satisfied condition carries (which zone/tower/enemy satisfied it).</summary>
/// <param name="ZoneId">Zone that satisfied a zone condition; null otherwise.</param>
/// <param name="TowerId">Tower that satisfied a tower condition; null otherwise.</param>
/// <param name="EnemyId">Enemy that satisfied an enemy condition; null otherwise.</param>
internal sealed record ScrollTriggerContext(string? ZoneId, long? TowerId, long? EnemyId);

/// <summary>
/// The C-18 scroll runtime (T-071, F-037): resident-scroll bookkeeping and
/// the four frozen rules of v3.2 appendix F.1, enforced in one place —
/// <list type="number">
/// <item>tick budget — per scroll at most 1 condition evaluation + 1 action
/// per 1 s scroll tick (cadence = every 30 sim ticks at 30 Hz; the budget
/// guard also covers the manual TriggerScroll lane);</item>
/// <item>per-tower priority arbitration — one tower is controlled by at
/// most one resident scroll: higher priority preempts, EQUAL priority
/// never displaces the incumbent (同优先级后到者不生效), lower is
/// suppressed while the incumbent holds;</item>
/// <item>resident cap — at most 6 + 2 × era equipped scrolls; the (n+1)-th
/// equip is refused (不占常驻：the TriggerScroll lane itself lives in the
/// agent handler, not in the resident roster);</item>
/// <item>system arbitration — an explicit system order on a tower
/// (<see cref="NotifySystemTowerOrder"/>) suspends scroll control of that
/// tower until the end of the current wave (系统显式指令&gt;卷轴，至本波结束).</item>
/// </list>
/// Verb-shaped scroll actions dispatch through the SHARED K-3 action
/// registry (<see cref="IActionExecutor"/> — the same 19-verb door as the
/// system; the scroll lane invents no verbs), while timed modifiers stay
/// engine-internal state the host layers read via
/// <see cref="ActiveEffects"/>. Deterministic by construction: fixed
/// iteration orders, no dictionary iteration in
/// <see cref="ComputeStateHash"/>, no randomness, no wall clock. The host
/// folds <see cref="ComputeStateHash"/> into its replay hash (T-067
/// aggregation point — Sim itself stays untouched per the T-071 seam).
/// </summary>
public sealed class ScrollEngine
{
    /// <summary>Resident cap base: 6 + 2 × era (F.1; E2=10 … finale=18).</summary>
    public const int ResidentCapBase = 6; // ci-allow: R7 T-071 附录 F.1 常驻上限 6+2×E（规则公式常量，非内容平衡数值）

    /// <summary>Resident cap slope: 6 + 2 × era (F.1).</summary>
    public const int ResidentCapPerEra = 2; // ci-allow: R7 T-071 附录 F.1 常驻上限 6+2×E（规则公式常量，非内容平衡数值）

    /// <summary>Scroll cadence: one scroll tick per 30 sim ticks = 1 s (F.1 每 tick（1s）).</summary>
    public const int TicksPerScrollTick = Simulation.TicksPerSecond;

    /// <summary>Control hold of instant actions (verb dispatch / heal): one scroll tick.</summary>
    public const int InstantControlHoldTicks = 30; // ci-allow: R7 T-071 瞬时动作控制窗=1 个卷轴 tick（仲裁窗量纲，[NEW-原型校准]）

    /// <summary>SetTowerMode target_id[] schema cap (K-3 ≤6 per chunk; deterministic chunking).</summary>
    public const int SetTowerModeChunkCap = 6; // ci-allow: R7 T-071 K-3 SetTowerMode target_id[]≤6（v2.1 §11.3 冻结域常量）

    /// <summary>Verbs the scroll lane may dispatch (read-only/mode class only, task T-071; no new verbs — F.1).</summary>
    public static readonly IReadOnlyList<string> DispatchableVerbs = new[]
    {
        nameof(SystemActionKind.LookAround),
        nameof(SystemActionKind.FocusFire),
        nameof(SystemActionKind.SetTowerMode),
    };

    private const int MinEra = 1;
    private const int MaxEra = 6;
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private sealed class ScrollRuntime
    {
        public long CooldownReadyTick;
        public long LastEvaluationWindow = long.MinValue;
        public long LastActionWindow = long.MinValue;
    }

    private sealed record TowerControl(string ScrollId, int Priority, long UntilTick, bool UntilWaveEnd);

    private readonly IActionExecutor? _executor;
    private readonly IScrollEffectSink? _effectSink;
    private readonly List<ScrollDefinition> _equipped = new();
    private readonly Dictionary<string, ScrollRuntime> _runtime = new(StringComparer.Ordinal);
    private readonly List<ActiveScrollEffect> _activeEffects = new();
    private readonly SortedDictionary<long, TowerControl> _towerControl = new();
    private readonly HashSet<long> _systemOverride = new();
    private readonly SortedDictionary<long, (int Wave, int Uses)> _perTowerWaveUse = new();
    private readonly List<ScrollFireRecord> _fireLog = new();
    private long _lastTick = -1;
    private int _lastWave;
    private int _systemOverrideWave = -1;

    /// <param name="eraIndex">Era ordinal 1..6 (drives the resident cap and equip gating).</param>
    /// <param name="executor">
    /// Shared K-3 action registry the verb steps dispatch through (same
    /// door as the system, RL6). Null = no verb lane wired; verb steps then
    /// record an honest NoTargets refusal instead of pretending dispatch.
    /// </param>
    /// <param name="effectSink">Instant-effect sink (heals). Null = heals are refused, not faked.</param>
    public ScrollEngine(int eraIndex, IActionExecutor? executor = null, IScrollEffectSink? effectSink = null)
    {
        EraIndex = Guard.Clamp(eraIndex, MinEra, MaxEra);
        _executor = executor;
        _effectSink = effectSink;
    }

    /// <summary>Current era ordinal (1..6).</summary>
    public int EraIndex { get; private set; }

    /// <summary>Resident activation cap = 6 + 2 × era (F.1; E2=10).</summary>
    public int ResidentCap => ResidentCapBase + (ResidentCapPerEra * EraIndex);

    /// <summary>Equipped scroll ids in equip order (deterministic).</summary>
    public IReadOnlyList<string> EquippedScrollIds => _equipped.Select(static s => s.Id).ToList();

    /// <summary>Engine-tracked timed modifiers, application order (defensive copy — snapshot semantics).</summary>
    public IReadOnlyList<ActiveScrollEffect> ActiveEffects => _activeEffects.ToList();

    /// <summary>Fire-attempt journal (auto + manual), oldest first.</summary>
    public IReadOnlyList<ScrollFireRecord> FireLog => _fireLog;

    /// <summary>
    /// Era transition: re-anchors the cap. Equipped scrolls beyond the new
    /// cap stay resident (no confiscation — same semantics as the point
    /// ledger's SetEra) but nothing more may be equipped until space frees.
    /// </summary>
    public void SetEra(int eraIndex)
    {
        EraIndex = Guard.Clamp(eraIndex, MinEra, MaxEra);
    }

    /// <summary>
    /// Equips one resident scroll (rule 3). Refusals are typed, never
    /// silent: duplicate id / cap exceeded / era-locked.
    /// </summary>
    public ScrollEquipResult Equip(ScrollDefinition scroll)
    {
        ArgumentNullException.ThrowIfNull(scroll);
        if (_equipped.Any(s => s.Id == scroll.Id))
        {
            return ScrollEquipResult.RejectedDuplicate;
        }

        if (scroll.UnlockEra > EraIndex)
        {
            return ScrollEquipResult.RejectedEraLocked;
        }

        if (_equipped.Count >= ResidentCap)
        {
            return ScrollEquipResult.RejectedCapExceeded;
        }

        _equipped.Add(scroll);
        _runtime[scroll.Id] = new ScrollRuntime();
        return ScrollEquipResult.Ok;
    }

    /// <summary>
    /// Removes one resident scroll and releases everything it holds (its
    /// effects and tower-control claims) — the freed towers become
    /// contestable again.
    /// </summary>
    public bool Unequip(string scrollId)
    {
        ScrollDefinition? scroll = _equipped.FirstOrDefault(s => s.Id == scrollId);
        if (scroll is null)
        {
            return false;
        }

        _ = _equipped.Remove(scroll);
        _ = _runtime.Remove(scrollId);
        _activeEffects.RemoveAll(e => e.ScrollId == scrollId);
        List<long> released = _towerControl
            .Where(kv => kv.Value.ScrollId == scrollId)
            .Select(static kv => kv.Key)
            .ToList();
        foreach (long towerId in released)
        {
            _ = _towerControl.Remove(towerId);
        }

        return true;
    }

    /// <summary>
    /// The 30 Hz host entry: advances wave-scoped bookkeeping every call
    /// and auto-evaluates condition cards on the 1 s scroll boundary
    /// (every 30th tick). Ticks must be strictly monotonic.
    /// </summary>
    public void Step(long tick, IScrollWorldView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (tick <= _lastTick && _lastTick >= 0)
        {
            throw new ArgumentException($"Scroll ticks must strictly increase (last={_lastTick}, got={tick}).", nameof(tick));
        }

        _lastTick = tick;
        if (view.CurrentWave != _lastWave)
        {
            OnWaveLeft(_lastWave);
            _lastWave = view.CurrentWave;
        }

        ExpireState(tick);
        if (tick % TicksPerScrollTick != 0)
        {
            return; // off-cadence: rule 1's evaluation window is the 1 s scroll tick
        }

        long window = tick / TicksPerScrollTick;
        foreach (ScrollDefinition scroll in _equipped)
        {
            if (scroll.Kind != ScrollCardKind.ConditionCard)
            {
                continue;
            }

            ScrollRuntime runtime = _runtime[scroll.Id];

            // Cooldown gate first: a gated scroll spends no evaluation.
            if (runtime.CooldownReadyTick > tick)
            {
                continue;
            }

            // Rule 1: at most one evaluation per scroll per window.
            if (runtime.LastEvaluationWindow == window)
            {
                continue;
            }

            runtime.LastEvaluationWindow = window;
            ScrollTriggerContext? context = Evaluate(scroll.Condition!, view);
            if (context is null)
            {
                continue;
            }

            if (runtime.LastActionWindow == window)
            {
                continue; // absolute rule-1 guard: the window's action slot is spent
            }

            if (scroll.Action.Effect is not null && HasActiveEffect(scroll.Id))
            {
                // Re-fire suppression while the scroll's own effect runs (e.g. SC-007 至波结束).
                Record(scroll.Id, tick, manual: false, ScrollFireOutcome.AlreadyActive, null, null);
                continue;
            }

            _ = ExecuteAction(scroll, tick, window, view, context, manual: false);
        }
    }

    /// <summary>
    /// The TriggerScroll lane (F.1 分工): fires one equipped ACTION card on
    /// demand. The agent-side handler (T-068/T-070) charges the 1 system
    /// point and enforces its own 3 s lane cooldown BEFORE calling this —
    /// this method enforces the scroll's own data cooldown and the rule-1
    /// per-window action budget. Does NOT occupy the resident cap (the
    /// scroll is already equipped; triggering is not equipping).
    /// </summary>
    public ScrollInvokeStatus TryInvokeActionCard(string scrollId, long tick, IScrollWorldView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        ScrollDefinition? scroll = _equipped.FirstOrDefault(s => s.Id == scrollId);
        if (scroll is null)
        {
            return ScrollInvokeStatus.NotEquipped;
        }

        if (scroll.Kind != ScrollCardKind.ActionCard)
        {
            return ScrollInvokeStatus.NotActionCard;
        }

        ScrollRuntime runtime = _runtime[scrollId];
        if (runtime.CooldownReadyTick > tick)
        {
            return ScrollInvokeStatus.CooldownNotReady;
        }

        long window = tick / TicksPerScrollTick;
        if (runtime.LastActionWindow == window)
        {
            return ScrollInvokeStatus.BudgetExhausted; // rule 1 covers the manual lane too
        }

        if (scroll.Action.Effect is not null && HasActiveEffect(scroll.Id))
        {
            Record(scrollId, tick, manual: true, ScrollFireOutcome.AlreadyActive, null, null);
            return ScrollInvokeStatus.Suppressed;
        }

        ScrollFireOutcome outcome = ExecuteAction(scroll, tick, window, view, new ScrollTriggerContext(null, null, null), manual: true);
        return outcome switch
        {
            ScrollFireOutcome.Executed => ScrollInvokeStatus.Ok,
            ScrollFireOutcome.NoTargets => ScrollInvokeStatus.NoTargets,
            _ => ScrollInvokeStatus.Suppressed,
        };
    }

    /// <summary>
    /// Rule 4: the host reports an explicit system order (FocusFire /
    /// SetTowerMode / … through the K-3 registry) that touched these
    /// towers. Scroll control of them is suspended until the current wave
    /// ends; other towers stay scroll-contestable.
    /// </summary>
    public void NotifySystemTowerOrder(IReadOnlyList<long> towerIds, long tick, int wave)
    {
        ArgumentNullException.ThrowIfNull(towerIds);
        if (wave != _systemOverrideWave)
        {
            _systemOverride.Clear();
            _systemOverrideWave = wave;
        }

        foreach (long towerId in towerIds)
        {
            _ = _systemOverride.Add(towerId);
        }
    }

    /// <summary>
    /// Manual wave-end entry (the auto path is wave-change detection in
    /// <see cref="Step"/>): releases wave-scoped state — system overrides,
    /// until-wave-end effects/claims, per-tower-per-wave counters.
    /// </summary>
    public void EndWave(int wave)
    {
        OnWaveLeft(wave);
    }

    /// <summary>Whether a system explicit order currently suspends scroll control of this tower (rule 4).</summary>
    public bool IsSystemOverridden(long towerId) => _systemOverride.Contains(towerId);

    /// <summary>The resident scroll currently controlling a tower, if any (rule 2 bookkeeping).</summary>
    public bool TryGetTowerController(long towerId, out string? scrollId)
    {
        if (_towerControl.TryGetValue(towerId, out TowerControl? control))
        {
            scrollId = control.ScrollId;
            return true;
        }

        scrollId = null;
        return false;
    }

    /// <summary>Rule-1 budget probe of one scroll (test/telemetry surface).</summary>
    public ScrollBudgetProbe GetBudget(string scrollId, long tick)
    {
        if (!_runtime.TryGetValue(scrollId, out ScrollRuntime? runtime))
        {
            throw new KeyNotFoundException($"Scroll '{scrollId}' is not resident.");
        }

        long window = tick / TicksPerScrollTick;
        return new ScrollBudgetProbe(
            window,
            runtime.LastEvaluationWindow == window,
            runtime.LastActionWindow == window,
            runtime.CooldownReadyTick);
    }

    /// <summary>
    /// Deterministic whole-state hash for replay aggregation (T-067
    /// extension point usage: the HOST folds this into its own hash — Sim
    /// itself is not modified, per the T-071 seam decision). Covers era,
    /// equip order, per-scroll budget/cooldown state, active effects with
    /// remaining ticks, tower control (sorted), system overrides (sorted),
    /// per-tower-per-wave counters (sorted) and a fire-journal digest.
    /// Dictionary iteration never enters the mix.
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)EraIndex);
        hash = Mix(hash, (ulong)_equipped.Count);
        foreach (ScrollDefinition scroll in _equipped)
        {
            hash = Mix(hash, (ulong)scroll.Priority);
            hash = Mix(hash, (ulong)_runtime[scroll.Id].CooldownReadyTick);
            hash = Mix(hash, (ulong)_runtime[scroll.Id].LastEvaluationWindow);
            hash = Mix(hash, (ulong)_runtime[scroll.Id].LastActionWindow);
            foreach (char c in scroll.Id)
            {
                hash = Mix(hash, c);
            }
        }

        hash = Mix(hash, (ulong)_activeEffects.Count);
        foreach (ActiveScrollEffect effect in _activeEffects)
        {
            hash = Mix(hash, (ulong)effect.StartedTick);
            hash = Mix(hash, (ulong)effect.UntilTick);
            hash = Mix(hash, effect.UntilWaveEnd ? 1UL : 0UL);
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(effect.Magnitude));
            hash = Mix(hash, (ulong)effect.ClaimedTowerIds.Count);
            foreach (long towerId in effect.ClaimedTowerIds)
            {
                hash = Mix(hash, (ulong)towerId);
            }

            foreach (char c in effect.ScrollId)
            {
                hash = Mix(hash, c);
            }
        }

        foreach (KeyValuePair<long, TowerControl> entry in _towerControl)
        {
            hash = Mix(hash, (ulong)entry.Key);
            hash = Mix(hash, (ulong)entry.Value.Priority);
            hash = Mix(hash, (ulong)entry.Value.UntilTick);
            hash = Mix(hash, entry.Value.UntilWaveEnd ? 1UL : 0UL);
            foreach (char c in entry.Value.ScrollId)
            {
                hash = Mix(hash, c);
            }
        }

        foreach (long towerId in _systemOverride)
        {
            hash = Mix(hash, (ulong)towerId);
        }

        foreach (KeyValuePair<long, (int Wave, int Uses)> entry in _perTowerWaveUse)
        {
            hash = Mix(hash, (ulong)entry.Key);
            hash = Mix(hash, (ulong)entry.Value.Wave);
            hash = Mix(hash, (ulong)entry.Value.Uses);
        }

        hash = Mix(hash, (ulong)_fireLog.Count);
        if (_fireLog.Count > 0)
        {
            ScrollFireRecord last = _fireLog[^1];
            hash = Mix(hash, (ulong)last.Tick);
            hash = Mix(hash, (ulong)last.Outcome);
            hash = Mix(hash, (ulong)(last.VerbTargetId ?? 0));
            foreach (char c in last.ScrollId)
            {
                hash = Mix(hash, c);
            }
        }

        return (long)hash;
    }

    // ------------------------------------------------------- internals

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        return (hash * FnvPrime) ^ (value >> 17);
    }

    private bool HasActiveEffect(string scrollId) => _activeEffects.Any(e => e.ScrollId == scrollId);

    private void Record(string scrollId, long tick, bool manual, ScrollFireOutcome outcome, string? verb, long? verbTargetId)
    {
        _fireLog.Add(new ScrollFireRecord(scrollId, tick, manual, outcome, verb, verbTargetId));
    }

    private void ExpireState(long tick)
    {
        _activeEffects.RemoveAll(e => !e.UntilWaveEnd && e.UntilTick <= tick);
        List<long> expiredControls = _towerControl
            .Where(kv => !kv.Value.UntilWaveEnd && kv.Value.UntilTick <= tick)
            .Select(static kv => kv.Key)
            .ToList();
        foreach (long towerId in expiredControls)
        {
            _ = _towerControl.Remove(towerId);
        }
    }

    private void OnWaveLeft(int wave)
    {
        // Rule 4 tail + wave-scoped effects/counters die with the wave.
        if (_systemOverrideWave == wave || _systemOverrideWave < 0)
        {
            _systemOverride.Clear();
            _systemOverrideWave = -1;
        }

        _activeEffects.RemoveAll(e => e.UntilWaveEnd);
        List<long> waveScoped = _towerControl
            .Where(kv => kv.Value.UntilWaveEnd)
            .Select(static kv => kv.Key)
            .ToList();
        foreach (long towerId in waveScoped)
        {
            _ = _towerControl.Remove(towerId);
        }

        List<long> stale = _perTowerWaveUse
            .Where(kv => kv.Value.Wave == wave)
            .Select(static kv => kv.Key)
            .ToList();
        foreach (long towerId in stale)
        {
            _ = _perTowerWaveUse.Remove(towerId);
        }
    }

    /// <summary>Evaluates one condition against the view; null = not satisfied.</summary>
    private static ScrollTriggerContext? Evaluate(ScrollCondition condition, IScrollWorldView view)
    {
        IReadOnlyList<ScrollEnemyFact> enemies = view.Enemies;
        switch (condition.Kind)
        {
            case ScrollConditionKind.ZoneEnemyCountAtLeast:
            {
                int count = (int)condition.Number("count");
                string zone = condition.Text("zone");
                if (zone == "*")
                {
                    foreach (ScrollZoneFact zoneFact in view.Zones)
                    {
                        if (enemies.Count(e => e.ZoneId == zoneFact.ZoneId) >= count)
                        {
                            return new ScrollTriggerContext(zoneFact.ZoneId, null, null);
                        }
                    }

                    return null;
                }

                return enemies.Count(e => e.ZoneId == zone) >= count
                    ? new ScrollTriggerContext(zone, null, null)
                    : null;
            }

            case ScrollConditionKind.EnemiesRemainingAtMost:
            {
                int count = (int)condition.Number("count");
                string zone = condition.Text("zone");
                bool inZone = zone != "*";
                int total = inZone
                    ? enemies.Count(e => e.ZoneId == zone)
                    : enemies.Count;
                return total <= count ? new ScrollTriggerContext(inZone ? zone : null, null, null) : null;
            }

            case ScrollConditionKind.EnemyInZone:
            {
                string zone = condition.Text("zone");
                return enemies.Any(e => e.ZoneId == zone)
                    ? new ScrollTriggerContext(zone, null, null)
                    : null;
            }

            case ScrollConditionKind.EnemyHpBelowPercent:
            {
                double percent = condition.Number("percent");
                ScrollEnemyFact? hit = enemies.FirstOrDefault(e => e.MaxHp > 0 && e.Hp < e.MaxHp * percent);
                return hit is null ? null : new ScrollTriggerContext(null, null, hit.Id);
            }

            case ScrollConditionKind.EnemyHpBelow:
            {
                double hp = condition.Number("hp");
                ScrollEnemyFact? hit = enemies.FirstOrDefault(e => e.Hp < hp);
                return hit is null ? null : new ScrollTriggerContext(null, null, hit.Id);
            }

            case ScrollConditionKind.WaveNumberIs:
            {
                int wave = (int)condition.Number("wave");
                return view.CurrentWave == wave ? new ScrollTriggerContext(null, null, null) : null;
            }

            case ScrollConditionKind.WaveNumberAtLeast:
            {
                int wave = (int)condition.Number("wave");
                return view.CurrentWave >= wave ? new ScrollTriggerContext(null, null, null) : null;
            }

            case ScrollConditionKind.ResourceAbove:
            {
                double amount = condition.Number("amount");
                string resource = condition.Text("resource");
                return view.ResourceAmount(new ResourceKey(resource)) > amount
                    ? new ScrollTriggerContext(null, null, null)
                    : null;
            }

            case ScrollConditionKind.EnemyDistanceToCellBelow:
            {
                int x = (int)condition.Number("x");
                int y = (int)condition.Number("y");
                double cells = condition.Number("cells");
                foreach (ScrollEnemyFact enemy in enemies)
                {
                    double dx = enemy.CellX - x;
                    double dy = enemy.CellY - y;
                    if (Math.Sqrt((dx * dx) + (dy * dy)) < cells)
                    {
                        return new ScrollTriggerContext(null, null, enemy.Id);
                    }
                }

                return null;
            }

            case ScrollConditionKind.TowerHpBelowPercent:
            {
                double percent = condition.Number("percent");
                ScrollTowerFact? worst = null;
                foreach (ScrollTowerFact tower in view.Towers)
                {
                    if (tower.MaxHp <= 0 || tower.Hp > tower.MaxHp * percent)
                    {
                        continue;
                    }

                    // Most urgent first (lowest ratio); id breaks ties — deterministic.
                    double ratio = (double)tower.Hp / tower.MaxHp;
                    double best = worst is null ? double.MaxValue : (double)worst.Hp / worst.MaxHp;
                    if (ratio < best || (ratio == best && worst!.Id > tower.Id))
                    {
                        worst = tower;
                    }
                }

                return worst is null ? null : new ScrollTriggerContext(null, worst.Id, null);
            }

            default:
                throw new InvalidOperationException($"Condition kind {condition.Kind} has no evaluation arm.");
        }
    }

    /// <summary>Runs one scroll action through arbitration into execution. Returns the outcome.</summary>
    private ScrollFireOutcome ExecuteAction(
        ScrollDefinition scroll,
        long tick,
        long window,
        IScrollWorldView view,
        ScrollTriggerContext context,
        bool manual)
    {
        ScrollRuntime runtime = _runtime[scroll.Id];
        ScrollAction action = scroll.Action;

        // ---- target resolution -------------------------------------------------
        List<ScrollTowerFact> targets = ResolveTargets(scroll, view, context);

        // Only tower-CONSUMING halves refuse on an empty tower set: SetTowerMode
        // and the tower-class buffs do nothing without towers. A zone damage
        // bonus lives on the zone itself (claims may be empty), and FocusFire
        // resolves enemies, not towers.
        bool needsTowers = action.Verb == nameof(SystemActionKind.SetTowerMode)
            || action.Effect is ScrollEffectKind.TowerClassAttackSpeed
            || action.Effect is ScrollEffectKind.SupportAuraRangeBonus;
        if (needsTowers && targets.Count == 0)
        {
            Record(scroll.Id, tick, manual, ScrollFireOutcome.NoTargets, null, null);
            return ScrollFireOutcome.NoTargets;
        }

        // ---- per-tower-per-wave cap (SC-004) ------------------------------------
        if (action.Scope == ScrollTargetScope.TriggerTower
            && context.TowerId is long limitedTower
            && scroll.PerTowerPerWaveLimit > 0
            && _perTowerWaveUse.TryGetValue(limitedTower, out (int Wave, int Uses) use)
            && use.Wave == view.CurrentWave
            && use.Uses >= scroll.PerTowerPerWaveLimit)
        {
            Record(scroll.Id, tick, manual, ScrollFireOutcome.PerTowerLimitReached, null, null);
            return ScrollFireOutcome.PerTowerLimitReached;
        }

        // ---- instant heals refuse honestly without a lawful sink / trigger tower --
        if (action.Effect == ScrollEffectKind.TowerHealPercent
            && (_effectSink is null || context.TowerId is null))
        {
            Record(scroll.Id, tick, manual, ScrollFireOutcome.NoTargets, null, null);
            return ScrollFireOutcome.NoTargets;
        }

        // ---- rule 4: system override filter ------------------------------------
        List<ScrollTowerFact> afterSystem = targets.Where(t => !IsSystemOverridden(t.Id)).ToList();
        if (afterSystem.Count == 0 && targets.Count > 0)
        {
            Record(scroll.Id, tick, manual, ScrollFireOutcome.SuppressedAllTargets, null, null);
            return ScrollFireOutcome.SuppressedAllTargets;
        }

        // ---- rule 2: priority arbitration filter --------------------------------
        List<ScrollTowerFact> allowed = afterSystem.Where(t => MayClaim(t.Id, scroll)).ToList();
        if (allowed.Count == 0 && targets.Count > 0)
        {
            Record(scroll.Id, tick, manual, ScrollFireOutcome.SuppressedAllTargets, null, null);
            return ScrollFireOutcome.SuppressedAllTargets;
        }

        // ---- verb step (shared K-3 registry — no new verbs) ----------------------
        string? verb = null;
        long? verbTarget = null;
        bool executedAny = false;
        if (action.Verb is not null)
        {
            if (_executor is null)
            {
                Record(scroll.Id, tick, manual, ScrollFireOutcome.NoTargets, action.Verb, null);
                return ScrollFireOutcome.NoTargets; // honest refusal: no lawful dispatch door wired
            }

            if (action.Verb == nameof(SystemActionKind.FocusFire))
            {
                ScrollEnemyFact? target = SelectEnemy(action.VerbTarget, view, context);
                if (target is null)
                {
                    Record(scroll.Id, tick, manual, ScrollFireOutcome.NoTargets, action.Verb, null);
                    return ScrollFireOutcome.NoTargets;
                }

                string json = "{\"action\":\"" + action.Verb
                    + "\",\"target_id\":" + target.Id.ToString(CultureInfo.InvariantCulture) + "}";
                _ = _executor.Execute(SystemActionKind.FocusFire, json);
                verb = action.Verb;
                verbTarget = target.Id;
                executedAny = true;
            }
            else if (action.Verb == nameof(SystemActionKind.SetTowerMode))
            {
                if (allowed.Count == 0 || action.VerbMode is null)
                {
                    Record(scroll.Id, tick, manual, ScrollFireOutcome.NoTargets, action.Verb, null);
                    return ScrollFireOutcome.NoTargets;
                }

                for (int offset = 0; offset < allowed.Count; offset += SetTowerModeChunkCap)
                {
                    var chunk = allowed.Skip(offset).Take(SetTowerModeChunkCap)
                        .Select(t => t.Id.ToString(CultureInfo.InvariantCulture));
                    string json = "{\"action\":\"" + action.Verb
                        + "\",\"target_id\":[" + string.Join(",", chunk)
                        + "],\"mode\":\"" + action.VerbMode + "\"}";
                    _ = _executor.Execute(SystemActionKind.SetTowerMode, json);
                }

                verb = action.Verb;
                executedAny = true;
            }
            else if (action.Verb == nameof(SystemActionKind.LookAround))
            {
                _ = _executor.Execute(SystemActionKind.LookAround, "{\"action\":\"" + action.Verb + "\"}");
                verb = action.Verb;
                executedAny = true;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Verb '{action.Verb}' is outside the scroll dispatchable set {string.Join('/', DispatchableVerbs)} (loader drifted).");
            }
        }

        // ---- effect step (engine-tracked modifier or instant heal) ----------------
        if (action.Effect is ScrollEffectKind effectKind)
        {
            ApplyEffect(scroll, action, effectKind, tick, view, context, allowed);
            executedAny = true;
        }

        if (!executedAny)
        {
            // No verb, no effect — loader forbids this shape; refuse honestly.
            Record(scroll.Id, tick, manual, ScrollFireOutcome.NoTargets, null, null);
            return ScrollFireOutcome.NoTargets;
        }

        // ---- bookkeeping: claims + cooldown + per-tower use ------------------------
        long holdUntil = InstantControlHoldTicks > 0 ? tick + InstantControlHoldTicks : tick;
        foreach (ScrollTowerFact tower in allowed)
        {
            // Never shorten the scroll's own longer claim (effect durations vs. instant holds).
            if (_towerControl.TryGetValue(tower.Id, out TowerControl? own)
                && own.ScrollId == scroll.Id
                && !own.UntilWaveEnd
                && own.UntilTick >= holdUntil)
            {
                continue;
            }

            _towerControl[tower.Id] = new TowerControl(scroll.Id, scroll.Priority, holdUntil, UntilWaveEnd: false);
        }

        if (action.Scope == ScrollTargetScope.TriggerTower && context.TowerId is long usedTower)
        {
            int wave = view.CurrentWave;
            int uses = 1;
            if (_perTowerWaveUse.TryGetValue(usedTower, out (int Wave, int Uses) existing) && existing.Wave == wave)
            {
                uses = existing.Uses + 1;
            }

            _perTowerWaveUse[usedTower] = (wave, uses);
        }

        runtime.LastActionWindow = window;
        runtime.CooldownReadyTick = tick + scroll.CooldownTicks;
        Record(scroll.Id, tick, manual, ScrollFireOutcome.Executed, verb, verbTarget);
        return ScrollFireOutcome.Executed;
    }

    private void ApplyEffect(
        ScrollDefinition scroll,
        ScrollAction action,
        ScrollEffectKind kind,
        long tick,
        IScrollWorldView view,
        ScrollTriggerContext context,
        List<ScrollTowerFact> allowedTowers)
    {
        if (kind == ScrollEffectKind.TowerHealPercent)
        {
            if (_effectSink is null || context.TowerId is not long towerId)
            {
                return; // heal refused honestly when no sink / no trigger tower
            }

            ScrollTowerFact? tower = view.Towers.FirstOrDefault(t => t.Id == towerId);
            int amount = tower is null ? 0 : (int)Math.Floor(tower.MaxHp * action.EffectMagnitude);
            if (amount > 0)
            {
                _effectSink.HealTower(towerId, amount);
            }

            return;
        }

        var claims = new List<long>();
        if (action.Scope == ScrollTargetScope.TriggerZone && context.ZoneId is not null)
        {
            claims = allowedTowers.Select(static t => t.Id).ToList();
        }
        else if (action.Scope == ScrollTargetScope.TowerClass || action.Scope == ScrollTargetScope.TriggerTower)
        {
            claims = allowedTowers.Select(static t => t.Id).ToList();
        }

        var effect = new ActiveScrollEffect(
            scroll.Id,
            kind,
            action.EffectMagnitude,
            action.Scope == ScrollTargetScope.TriggerZone ? context.ZoneId : null,
            action.Scope == ScrollTargetScope.TowerClass ? action.TowerClass : null,
            tick,
            tick + action.EffectDurationTicks,
            action.UntilWaveEnd,
            claims);
        _activeEffects.Add(effect);

        long until = action.UntilWaveEnd ? long.MaxValue : tick + action.EffectDurationTicks;
        foreach (long towerId in claims)
        {
            // Never shorten the scroll's own longer claim (re-application guard).
            if (_towerControl.TryGetValue(towerId, out TowerControl? own)
                && own.ScrollId == scroll.Id
                && (own.UntilWaveEnd || own.UntilTick >= until))
            {
                continue;
            }

            _towerControl[towerId] = new TowerControl(scroll.Id, scroll.Priority, until, action.UntilWaveEnd);
        }
    }

    private bool MayClaim(long towerId, ScrollDefinition scroll)
    {
        if (!_towerControl.TryGetValue(towerId, out TowerControl? incumbent))
        {
            return true;
        }

        if (incumbent.ScrollId == scroll.Id)
        {
            return true; // own claim refreshes
        }

        // Equal priority keeps the incumbent (F.1 同优先级后到者不生效); higher takes over; lower waits.
        return scroll.Priority > incumbent.Priority;
    }

    private List<ScrollTowerFact> ResolveTargets(ScrollDefinition scroll, IScrollWorldView view, ScrollTriggerContext context)
    {
        switch (scroll.Action.Scope)
        {
            case ScrollTargetScope.None:
                return new List<ScrollTowerFact>();
            case ScrollTargetScope.TriggerZone:
                return context.ZoneId is null
                    ? new List<ScrollTowerFact>()
                    : view.Towers.Where(t => t.ZoneId == context.ZoneId).ToList();
            case ScrollTargetScope.TriggerTower:
                return context.TowerId is null
                    ? new List<ScrollTowerFact>()
                    : view.Towers.Where(t => t.Id == context.TowerId).ToList();
            case ScrollTargetScope.TowerClass:
                return scroll.Action.TowerClass is { } towerClass
                    ? view.Towers.Where(t => t.TowerClass == towerClass).ToList()
                    : new List<ScrollTowerFact>();
            default:
                throw new InvalidOperationException($"Scope {scroll.Action.Scope} has no resolution arm.");
        }
    }

    private static ScrollEnemyFact? SelectEnemy(ScrollVerbTarget? selector, IScrollWorldView view, ScrollTriggerContext context)
    {
        if (selector is null)
        {
            return null;
        }

        IEnumerable<ScrollEnemyFact> pool = selector == ScrollVerbTarget.StrongestInTriggerZone
            ? view.Enemies.Where(e => context.ZoneId is not null && e.ZoneId == context.ZoneId)
            : view.Enemies;
        ScrollEnemyFact? best = null;
        foreach (ScrollEnemyFact enemy in pool)
        {
            if (best is null || enemy.Hp > best.Hp || (enemy.Hp == best.Hp && enemy.Id < best.Id))
            {
                best = enemy;
            }
        }

        return best;
    }
}
