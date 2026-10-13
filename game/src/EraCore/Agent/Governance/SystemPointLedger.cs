using System.Text.Json;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Journal;

namespace EraCore.Agent.Governance;

/// <summary>
/// Audit payload of one budget-chokepoint event (T-050, C-14): every
/// point movement and every refusal the ledger emits lands in the K-8
/// JSONL stream as one serialized line of this shape (支出/回满全程可审计,
/// v3.2 §11.4). Serialized only through the source-generated
/// <see cref="GovernanceJsonContext"/> (D-05 AOT red line).
/// </summary>
/// <param name="Type">Event discriminator: <c>wave_refill</c> / <c>wave_refill_rejected</c> / <c>spend_ok</c> / <c>spend_denied</c> / <c>era_changed</c>.</param>
/// <param name="Era">Era index (1..6) at event time.</param>
/// <param name="Wave">Wave number the event belongs to (0 before the first BeginWave).</param>
/// <param name="Verb">Verb of a spend event; null on refill/era events.</param>
/// <param name="Amount">Points charged (spend_ok) or requested (spend_denied); refill target on wave_refill; 0 elsewhere.</param>
/// <param name="BalanceAfter">Point balance after the event (unchanged on refusals).</param>
/// <param name="WaveSpentAfter">Per-wave spend counter after the event.</param>
/// <param name="Reason">Machine-readable refusal cause on rejections; null otherwise.</param>
public sealed record SystemPointLedgerEvent(
    string Type,
    int Era,
    long Wave,
    SystemActionKind? Verb,
    int Amount,
    int BalanceAfter,
    int WaveSpentAfter,
    string? Reason);

/// <summary>
/// The system-point budget chokepoint (T-050, C-14 / feature F-064):
/// the ONLY component that prices, charges and refills system points —
/// per v3.2 §11.4 the two governance thresholds (波内点数支出 ≤上限×40%
/// and the plan-level 30% resource popup, the latter in
/// <see cref="PlanCostGate"/>) are enforced by this single C-14 budget
/// seam ("BudgetLedger 单一咽喉"). Frozen rules:
/// <list type="bullet">
/// <item>Ceiling = 1 + 2 × era index; E1 does not enable points at all
/// (开启=false → any priced call is refused; free verbs still run).</item>
/// <item>Every wave start refills up to the ceiling; a Boss wave refills
/// up to ⌊ceiling × 50%⌋ (向下取整) — refill fills, it never confiscates
/// points already held above the halved target.</item>
/// <item>Pricing comes only from <see cref="SystemActionTariffTable"/>
/// (建造 2/战斗 1/物流 1/巡逻 1/侦察 0) — callers cannot declare costs
/// (RL6: no lawful bypass). Boss waves double combat-class verbs only.</item>
/// <item>Per-wave spend cap = ⌊ceiling × 40%⌋ (= ⌊ceiling×2/5⌋):
/// once the wave's spend counter would cross the line the call is
/// refused with <see cref="ActionResultStatus.ErrBudgetDenied"/> and
/// nothing is billed (failed actions never bill).</item>
/// <item>Wave starts are monotonic: a forged/duplicate BeginWave (same or
/// lower wave number) is rejected and grants nothing.</item>
/// </list>
/// All movements and refusals are journaled as JSONL through the
/// optional K-8 <see cref="IJournalWriter"/> (Kind=Validation: budget
/// outcomes are state-validation-class records within the frozen K-8
/// event kinds; the C-13 pipeline re-binds Seq to the bus domain).
/// Deterministic and single-threaded (D-12): no locks — sequential
/// semantics are the contract.
/// </summary>
public sealed class SystemPointLedger
{
    /// <summary>Ceiling formula slope: cap = 1 + 2 × era index (§11.4: E2=5 … finale=13).</summary>
    public const int CeilingBase = 1;

    /// <summary>Ceiling formula multiplier: cap = 1 + 2 × era index (§11.4).</summary>
    public const int CeilingPerEra = 2;

    /// <summary>Boss-wave refill fraction numerator: ⌊cap × 50%⌋ = ⌊cap / 2⌋ (§11.4 向下取整).</summary>
    public const int BossRefillDivisor = 2;

    /// <summary>Per-wave spend cap numerator: ⌊cap × 40%⌋ = ⌊cap × 2 / 5⌋ (§11.4 40% 限).</summary>
    public const int WaveSpendCapNumerator = 2;

    /// <summary>Per-wave spend cap denominator: ⌊cap × 2 / 5⌋ (§11.4 40% 限).</summary>
    public const int WaveSpendCapDenominator = 5;

    /// <summary>Boss-wave combat-class price surge (§11.4 战斗类 ×2).</summary>
    public const int BossCombatSurge = 2;

    /// <summary>Lowest era index (E1 = points not enabled).</summary>
    public const int MinEra = 1;

    /// <summary>Highest era index (finale E6).</summary>
    public const int MaxEra = 6;

    private readonly IJournalWriter? _journal;
    private readonly IClock? _clock;
    private long _seq;
    private long _wave;

    /// <summary>
    /// Creates a ledger for one run at the given era.
    /// </summary>
    /// <param name="eraIndex">Era index 1..6; 1 leaves points disabled (§11.4 E1 不开启).</param>
    /// <param name="journal">Optional K-8 sink for the JSONL audit trail (null = no journaling).</param>
    /// <param name="clock">Optional K-1 clock stamping journal ticks (null = tick -1, outside a run).</param>
    /// <param name="startSeq">First journal sequence number (bus-domain rebinding lands with C-13).</param>
    public SystemPointLedger(
        int eraIndex,
        IJournalWriter? journal = null,
        IClock? clock = null,
        long startSeq = 0)
    {
        EraIndex = Guard.EnsureInRange(eraIndex, MinEra, MaxEra, nameof(eraIndex));
        _journal = journal;
        _clock = clock;
        _seq = startSeq;
        _wave = 0;
        Points = 0;
        WaveSpent = 0;
        InBossWave = false;
    }

    /// <summary>Current era index (1..6); change only via <see cref="SetEra"/>.</summary>
    public int EraIndex { get; private set; }

    /// <summary>Whether the point economy is on: era ≥ 2 (§11.4 E1 不开启).</summary>
    public bool Enabled => EraIndex >= 2;

    /// <summary>Point ceiling = 1 + 2 × era index (E2=5, E3=7, E4=9, E5=11, E6=13).</summary>
    public int Ceiling => CeilingBase + CeilingPerEra * EraIndex;

    /// <summary>Current point balance; moves only through BeginWave/TrySpend.</summary>
    public int Points { get; private set; }

    /// <summary>Points already spent in the current wave (40% line accumulates here).</summary>
    public int WaveSpent { get; private set; }

    /// <summary>Per-wave spend cap = ⌊ceiling × 40%⌋ (E2=2, E3=2, E4=3, E5=4, E6=5).</summary>
    public int WaveSpendCap => Ceiling * WaveSpendCapNumerator / WaveSpendCapDenominator;

    /// <summary>Wave number of the current wave (0 before the first BeginWave).</summary>
    public long Wave => _wave;

    /// <summary>Whether the current wave is a Boss wave (surge pricing active).</summary>
    public bool InBossWave { get; private set; }

    /// <summary>
    /// Price of one verb in the CURRENT wave context (Boss surge applied).
    /// Exposed for plan cards; charging still only happens via
    /// <see cref="TrySpend"/> — the card may quote, the ledger bills.
    /// </summary>
    public int PriceOf(SystemActionKind verb)
    {
        var tariff = SystemActionTariffTable.Get(verb);
        return tariff.DoubledOnBossWave && InBossWave
            ? tariff.PointCost * BossCombatSurge
            : tariff.PointCost;
    }

    /// <summary>
    /// Starts a wave: refill to the ceiling (normal) or to
    /// ⌊ceiling × 50%⌋ (Boss wave) and reset the per-wave spend counter.
    /// Refusal semantics (anti-forgery): wave numbers must strictly
    /// increase — a duplicate or regressed BeginWave is rejected, changes
    /// nothing and journals <c>wave_refill_rejected</c>.
    /// </summary>
    /// <param name="waveNumber">1-based wave number, strictly greater than the previous.</param>
    /// <param name="isBossWave">Boss waves refill to the halved target and surge combat prices.</param>
    /// <returns>true when the wave started; false when the call was a forged/duplicate refill.</returns>
    public bool BeginWave(long waveNumber, bool isBossWave)
    {
        if (waveNumber <= _wave)
        {
            Emit(new SystemPointLedgerEvent(
                "wave_refill_rejected", EraIndex, _wave, Verb: null, Amount: 0,
                BalanceAfter: Points, WaveSpentAfter: WaveSpent,
                Reason: $"non_monotonic_wave:{waveNumber}<=last:{_wave}"));
            return false;
        }

        _wave = waveNumber;
        InBossWave = isBossWave;
        WaveSpent = 0;

        int target = Enabled
            ? (isBossWave ? Ceiling / BossRefillDivisor : Ceiling)
            : 0;
        int before = Points;
        Points = Math.Max(Points, target);

        Emit(new SystemPointLedgerEvent(
            "wave_refill", EraIndex, _wave, Verb: null, Amount: target,
            BalanceAfter: Points, WaveSpentAfter: WaveSpent,
            Reason: isBossWave ? "boss_half_refill" : (before < target ? "refill_to_ceiling" : "already_at_or_above_target")));
        return true;
    }

    /// <summary>
    /// Era transition (时代序号注入): re-anchors ceiling/enabled and clamps
    /// the balance into the new ceiling. Points keep their value — the
    /// next <see cref="BeginWave"/> performs the refill.
    /// </summary>
    /// <param name="eraIndex">New era index 1..6.</param>
    public void SetEra(int eraIndex)
    {
        EraIndex = Guard.EnsureInRange(eraIndex, MinEra, MaxEra, nameof(eraIndex));
        Points = Math.Min(Points, Ceiling);
        Emit(new SystemPointLedgerEvent(
            "era_changed", EraIndex, _wave, Verb: null, Amount: 0,
            BalanceAfter: Points, WaveSpentAfter: WaveSpent, Reason: null));
    }

    /// <summary>
    /// Charges one verb per the frozen tariff. Order of refusal checks:
    /// E1-disabled → insufficient balance → per-wave 40% cap. Refused
    /// calls bill nothing (failed actions never bill) and return
    /// <see cref="ActionResultStatus.ErrBudgetDenied"/> with a
    /// machine-readable cause. Free verbs (0 pt) always succeed, including
    /// while E1 has points disabled.
    /// </summary>
    /// <param name="verb">One of the 19 closed verbs; its price comes from the tariff table only.</param>
    public ActionResult TrySpend(SystemActionKind verb)
    {
        var tariff = SystemActionTariffTable.Get(verb);
        int cost = PriceOf(verb);

        if (cost == 0)
        {
            Emit(new SystemPointLedgerEvent(
                "spend_ok", EraIndex, _wave, verb, Amount: 0,
                BalanceAfter: Points, WaveSpentAfter: WaveSpent, Reason: "free_verb"));
            return new ActionResult(ActionResultStatus.Ok);
        }

        if (!Enabled)
        {
            return Deny(verb, cost, "points_not_enabled_e1");
        }

        if (cost > Points)
        {
            return Deny(verb, cost, $"insufficient_balance:{Points}<{cost}");
        }

        if (WaveSpent + cost > WaveSpendCap)
        {
            return Deny(verb, cost, $"wave_cap_exceeded:{WaveSpent}+{cost}>{WaveSpendCap}");
        }

        Points -= cost;
        WaveSpent += cost;
        Emit(new SystemPointLedgerEvent(
            "spend_ok", EraIndex, _wave, verb, Amount: cost,
            BalanceAfter: Points, WaveSpentAfter: WaveSpent,
            Reason: tariff.DoubledOnBossWave && InBossWave ? "boss_surge_priced" : null));
        return new ActionResult(ActionResultStatus.Ok, PointsSpent: cost);
    }

    /// <summary>
    /// Atomic plan pre-check (K-5 plan contract: 预算不足整案拒绝): whether
    /// the ledger could bill a whole plan of
    /// <paramref name="estimatedPointCost"/> points in the current wave —
    /// balance AND the remaining 40% headroom must both cover it. Pure
    /// query: nothing is charged.
    /// </summary>
    public bool CanCoverPlan(int estimatedPointCost)
    {
        if (estimatedPointCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedPointCost), estimatedPointCost, "Plan cost cannot be negative.");
        }

        if (estimatedPointCost == 0)
        {
            return true; // a free plan bills nothing — coverable even while E1 points are off
        }

        return Enabled
            && estimatedPointCost <= Points
            && WaveSpent + estimatedPointCost <= WaveSpendCap;
    }

    private ActionResult Deny(SystemActionKind verb, int cost, string reason)
    {
        Emit(new SystemPointLedgerEvent(
            "spend_denied", EraIndex, _wave, verb, Amount: cost,
            BalanceAfter: Points, WaveSpentAfter: WaveSpent, Reason: reason));
        return new ActionResult(
            ActionResultStatus.ErrBudgetDenied, ErrorMessage: reason);
    }

    private void Emit(SystemPointLedgerEvent payload)
    {
        if (_journal is null)
        {
            return;
        }

        _seq++;
        long tick = _clock?.Tick ?? -1;
        string json = JsonSerializer.Serialize(payload, GovernanceJsonContext.Default.SystemPointLedgerEvent);
        _journal.Append(new JournalEvent(JournalEventKind.Validation, tick, _seq, json));
    }
}
