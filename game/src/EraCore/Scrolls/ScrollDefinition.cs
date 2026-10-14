using EraCore.Combat;

namespace EraCore.Scrolls;

/// <summary>
/// Card family of one guardian scroll (v3.2 appendix F.1 / SP-5): condition
/// cards evaluate their condition on the 1 s scroll cadence and fire on
/// satisfaction; action cards carry no condition and fire only through the
/// explicit TriggerScroll lane (manual/system invocation).
/// </summary>
public enum ScrollCardKind
{
    /// <summary>条件卡: auto-evaluated on the scroll tick, fires on satisfaction.</summary>
    ConditionCard,

    /// <summary>动作卡: no condition; fired through TriggerScroll (SP-5 动作卡 3 张).</summary>
    ActionCard,
}

/// <summary>
/// Closed condition vocabulary of the scroll DSL (T-071 / SP-5): the four
/// families the spec table names — enemy HP threshold, wave number,
/// resource threshold, position distance — plus the zone-count and
/// tower-HP predicates SP-5's seven scrolls actually need. Mods compose
/// these primitives from data; the vocabulary itself is closed (F.1 原语
/// DSL 约束 — verbs and predicates are never runtime-extensible).
/// </summary>
public enum ScrollConditionKind
{
    /// <summary>某分区敌数 ≥ count（SP-5 SC-001 警弦）；zone "*"=任一分区（取 id 序首个满足者）。</summary>
    ZoneEnemyCountAtLeast,

    /// <summary>分区敌数 ≤ count（含全图 scope：zone "*" 对全体存活敌计数）。</summary>
    EnemiesRemainingAtMost,

    /// <summary>有敌进入指定分区（SP-5 SC-002 瓮城反击）。</summary>
    EnemyInZone,

    /// <summary>任一存活敌人当前 HP &lt; 比例阈值（0..1，严格小于）。</summary>
    EnemyHpBelowPercent,

    /// <summary>任一存活敌人当前 HP &lt; 绝对值阈值（严格小于）。</summary>
    EnemyHpBelow,

    /// <summary>当前波次 == N。</summary>
    WaveNumberIs,

    /// <summary>当前波次 ≥ N。</summary>
    WaveNumberAtLeast,

    /// <summary>资源池存量 &gt; 阈值（严格大于）。</summary>
    ResourceAbove,

    /// <summary>任一存活敌人与格坐标距离 &lt; cells（欧氏，px/32 换算）。</summary>
    EnemyDistanceToCellBelow,

    /// <summary>任一塔 HP ≤ 比例阈值（SP-5 SC-004 断后，≤ 口径；取比例最低者，id 破平）。</summary>
    TowerHpBelowPercent,
}

/// <summary>One named primitive argument of a condition (data-driven: count / percent / zone id / resource id / cell).</summary>
/// <param name="Name">Argument name on the wire (count, percent, zone, resource, wave, x, y, cells, hp).</param>
/// <param name="Number">Numeric value (0 when the argument is textual).</param>
/// <param name="Text">Textual value (null when the argument is numeric).</param>
public sealed record ScrollConditionArg(string Name, double Number, string? Text);

/// <summary>
/// One scroll condition: a closed-vocabulary predicate plus its data
/// arguments. Pure value — evaluation lives in
/// <see cref="ScrollEngine"/> against <see cref="IScrollWorldView"/> facts.
/// </summary>
/// <param name="Kind">Predicate primitive (closed list, F.1).</param>
/// <param name="Args">Data arguments in file order (loader validates per kind).</param>
public sealed record ScrollCondition(ScrollConditionKind Kind, IReadOnlyList<ScrollConditionArg> Args)
{
    /// <summary>First numeric argument with the given name; throws when absent or textual (loader guarantees typing).</summary>
    public double Number(string name) =>
        Args.FirstOrDefault(a => a.Name == name && a.Text is null)?.Number
        ?? throw new InvalidOperationException($"condition:{Kind} lacks numeric arg '{name}'");

    /// <summary>First textual argument with the given name; throws when absent or numeric (loader guarantees typing).</summary>
    public string Text(string name) =>
        Args.FirstOrDefault(a => a.Name == name && a.Text is not null)?.Text
        ?? throw new InvalidOperationException($"condition:{Kind} lacks text arg '{name}'");
}

/// <summary>
/// Closed effect vocabulary of the scroll runtime (T-071 / SP-5 action
/// halves). Effects are scroll payload data tracked by the engine as timed
/// modifiers the combat/logistics layers read; they are NOT agent verbs —
/// verb-shaped scroll actions dispatch through the shared K-3 action
/// registry (<see cref="ScrollAction.Verb"/>, 动词不可扩展 per F.1).
/// </summary>
public enum ScrollEffectKind
{
    /// <summary>分区反击伤害 +magnitude（SC-002 瓮城反击：+50% 10 s，与 SP-1b 瓮城 +20% 同源数值）。</summary>
    ZoneDamageBonus,

    /// <summary>某类塔攻速 +magnitude（SC-003 备刃：穿刺塔 +25% 15 s）。</summary>
    TowerClassAttackSpeed,

    /// <summary>回复触发塔 20% 最大 HP（SC-004 断后：瞬时，经 IScrollEffectSink 落地）。</summary>
    TowerHealPercent,

    /// <summary>支援类光环范围 +magnitude 格（SC-005 烽火：+2 格 20 s）。</summary>
    SupportAuraRangeBonus,

    /// <summary>物流运力 ×magnitude（SC-006 民夫征调：×1.5 30 s，不改档位上限）。</summary>
    LogisticsThroughputMultiplier,

    /// <summary>全塔能耗 ×magnitude 至波结束（SC-007 静默：×0.5）。</summary>
    TowerEnergyCostMultiplier,
}

/// <summary>Which towers a scroll action addresses / an effect covers.</summary>
public enum ScrollTargetScope
{
    /// <summary>无塔目标（全局效果：物流/能耗）。</summary>
    None,

    /// <summary>满足条件的那个分区（区内塔/区内效果）。</summary>
    TriggerZone,

    /// <summary>满足条件的那座塔（SC-004 回复该塔）。</summary>
    TriggerTower,

    /// <summary>动作塔类参数指定的全体塔（SC-003 穿刺/SC-005 支援）。</summary>
    TowerClass,
}

/// <summary>
/// How the enemy target of a verb dispatch is chosen (closed selector
/// vocabulary; SC-001 复用 FocusFire 语义选区内最强敌).
/// </summary>
public enum ScrollVerbTarget
{
    /// <summary>触发分区内当前 HP 最高的存活敌（HP 破平取 id 较小者——确定性）。</summary>
    StrongestInTriggerZone,

    /// <summary>全图当前 HP 最高的存活敌（HP 破平取 id 较小者——确定性）。</summary>
    StrongestAnywhere,
}

/// <summary>
/// The action half of one scroll (T-071): EITHER a verb dispatch through
/// the shared K-3 registry (closed 19-verb list — the scroll lane invents
/// no new verbs, F.1) OR an engine-tracked timed effect from the closed
/// effect list, or both (verb + effect is expressible; SP-5 uses either/or).
/// </summary>
/// <param name="Verb">Closed K-3 verb name (e.g. "FocusFire"); null = no verb dispatch.</param>
/// <param name="VerbMode">Mode value for SetTowerMode ("first|last|strong|closest"); null otherwise.</param>
/// <param name="VerbTarget">Enemy-target selector when <paramref name="Verb"/> needs one; null otherwise.</param>
/// <param name="Effect">Engine-tracked effect kind; null = no timed effect.</param>
/// <param name="EffectMagnitude">Effect magnitude (fraction / multiplier / cells per effect kind).</param>
/// <param name="EffectDurationTicks">Effect duration in 30 Hz ticks; 0 for instant effects.</param>
/// <param name="UntilWaveEnd">Effect persists to the end of the current wave (SC-007 至波结束).</param>
/// <param name="Scope">Which towers the action addresses.</param>
/// <param name="TowerClass">Tower class parameter for <see cref="ScrollTargetScope.TowerClass"/>; null otherwise.</param>
public sealed record ScrollAction(
    string? Verb,
    string? VerbMode,
    ScrollVerbTarget? VerbTarget,
    ScrollEffectKind? Effect,
    double EffectMagnitude,
    int EffectDurationTicks,
    bool UntilWaveEnd,
    ScrollTargetScope Scope,
    TowerClass? TowerClass);

/// <summary>
/// Acquisition channel of one scroll (SP-5 获取途径列，仅两类：研究节点/Boss
/// 掉落——全免费定位无商店). Pure data; the campaign layer resolves it.
/// </summary>
/// <param name="Kind">research | boss_drop | era_gift.</param>
/// <param name="Ref">Referenced node/enemy id (e.g. campaign:tech-sys-e2-codex-i, era2:enemy-boss).</param>
public sealed record ScrollAcquisition(string Kind, string Ref);

/// <summary>
/// One guardian scroll definition (C-18, T-071): the full data model the
/// JSON loader produces and <see cref="ScrollEngine"/> consumes. Every
/// gameplay number (priority, cooldown, thresholds, magnitudes, durations)
/// lives in this data — the engine carries rule constants only (CS-2.3).
/// Immutable value record.
/// </summary>
/// <param name="Id">Registry id (namespaced: era2:scroll-…).</param>
/// <param name="SpId">SP-5 row id (SC-001 … SC-007) for traceability.</param>
/// <param name="Kind">Card family (condition / action).</param>
/// <param name="NameZh">Chinese display name.</param>
/// <param name="NameEn">English display name.</param>
/// <param name="Aliases">Commander grammar aliases (K-2 same-source).</param>
/// <param name="Priority">Arbitration priority: higher wins a contested tower; same priority keeps the incumbent (F.1).</param>
/// <param name="CooldownTicks">Ticks after a fire before the scroll may act again (30 Hz ticks).</param>
/// <param name="UnlockEra">Era index from which the scroll may be equipped (E2=2).</param>
/// <param name="Condition">Condition (condition cards only; null on action cards).</param>
/// <param name="Action">Action half (verb dispatch and/or engine effect).</param>
/// <param name="PerTowerPerWaveLimit">Per-tower fire cap per wave (SC-004=1); 0 = unlimited.</param>
/// <param name="Acquisition">How the player obtains the scroll (SP-5 获取途径).</param>
public sealed record ScrollDefinition(
    string Id,
    string SpId,
    ScrollCardKind Kind,
    string NameZh,
    string NameEn,
    IReadOnlyList<string> Aliases,
    int Priority,
    int CooldownTicks,
    int UnlockEra,
    ScrollCondition? Condition,
    ScrollAction Action,
    int PerTowerPerWaveLimit,
    ScrollAcquisition Acquisition);
