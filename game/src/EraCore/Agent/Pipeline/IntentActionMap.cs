using EraCore.Contracts.Actions;
using EraCore.Contracts.Intents;

namespace EraCore.Agent.Pipeline;

/// <summary>
/// One row of the frozen 12-intent → 19-action mapping (T-068, C-13 full;
/// SP-2 对齐 — 11-spec-tables SP-2 「对应动作」column plus the T-053
/// HoldRegion goal template). Pure data: the planner's per-intent templates
/// and the mapping tests both read THIS table, so an intent can never gain
/// or lose an action step silently.
/// </summary>
/// <param name="Intent">One of the 12 frozen SP-2 intents.</param>
/// <param name="Actions">
/// The action sequence the intent expands to. Rows are honest about the
/// shipped shapes: the read-only intents keep their recon+report closure;
/// mutating intents add their verb between the free LookAround preamble
/// and the free ReportToPlayer epilogue (F-169 loop; SetAlarm keeps the
/// lean two-step template the T-053 card froze).
/// </param>
public sealed record IntentActionRow(IntentKind Intent, IReadOnlyList<SystemActionKind> Actions);

/// <summary>
/// The frozen 12-row mapping table (SP-2 对齐, T-068). Coverage note: 14 of
/// the 19 closed verbs are reachable through an intent row; the remaining
/// five — SetRally and SetStockpile (SP-2 #1/#7 halves kept out of the
/// shipped templates because the E2 40% wave cap fits exactly one priced
/// step per plan), AskPlayer and TodoBoard (agent-initiated verbs with no
/// military order behind them) and HeroOrder (no SP-2 intent row) — enter
/// the same five-stage pipeline through the host-driven single-action
/// entry (<see cref="AgentPipeline.HandleSystemAction"/>), which the C-22
/// panel and the T-081 全托管 lane use.
/// </summary>
public static class IntentActionMap
{
    /// <summary>All 12 rows in SP-2 order.</summary>
    public static readonly IReadOnlyList<IntentActionRow> Rows = new IntentActionRow[]
    {
        // SP-2 #1 守住X — goal template: recon → build the default defense
        // pair → report (T-053 shape, task-card example).
        new(IntentKind.HoldRegion, new[] { SystemActionKind.LookAround, SystemActionKind.BuildPlan, SystemActionKind.ReportToPlayer }),

        // SP-2 #2 建造.
        new(IntentKind.Build, new[] { SystemActionKind.LookAround, SystemActionKind.BuildPlan, SystemActionKind.ReportToPlayer }),

        // SP-2 #3 升级.
        new(IntentKind.Upgrade, new[] { SystemActionKind.LookAround, SystemActionKind.UpgradeTower, SystemActionKind.ReportToPlayer }),

        // SP-2 #4 出售 (danger bit confirm_sell).
        new(IntentKind.Sell, new[] { SystemActionKind.LookAround, SystemActionKind.SellTower, SystemActionKind.ReportToPlayer }),

        // SP-2 #5 集火.
        new(IntentKind.FocusFire, new[] { SystemActionKind.LookAround, SystemActionKind.FocusFire, SystemActionKind.ReportToPlayer }),

        // SP-2 #6 改集火模式 (1 pt — the wave-cap-friendly combat step).
        new(IntentKind.ChangeFocusMode, new[] { SystemActionKind.SetTowerMode, SystemActionKind.ReportToPlayer }),

        // SP-2 #7 物流改道 (蓄量下限 SetStockpile follows up as its own order).
        new(IntentKind.RerouteLogistics, new[] { SystemActionKind.RerouteLogistics, SystemActionKind.ReportToPlayer }),

        // SP-2 #8 储备查询 — QueryWorld{key:resource} feeds the report.
        new(IntentKind.QueryStockpile, new[] { SystemActionKind.LookAround, SystemActionKind.QueryWorld, SystemActionKind.ReportToPlayer }),

        // SP-2 #9 敌情查询 — the recon step is scope-dependent at plan time
        // (next_wave → QueryWorld{wave}; a known region → InspectTile{threat}
        // at the region anchor; else LookAround); the row lists the whole
        // candidate set.
        new(IntentKind.QueryThreat, new[]
        {
            SystemActionKind.LookAround, SystemActionKind.InspectTile, SystemActionKind.QueryWorld,
            SystemActionKind.ReportToPlayer,
        }),

        // SP-2 #10 设闹 (lean T-053 template).
        new(IntentKind.SetAlarm, new[] { SystemActionKind.SetAlarm, SystemActionKind.ReportToPlayer }),

        // SP-2 #11 巡逻 (E2 patrol tier; E1 order → ErrCapabilityLocked at the gate).
        new(IntentKind.Patrol, new[] { SystemActionKind.SpawnScout, SystemActionKind.ReportToPlayer }),

        // SP-2 #12 卷轴触发+停止 — A path scroll / B path task, disambiguated
        // by which slot the parse filled.
        new(IntentKind.ScrollControl, new[] { SystemActionKind.TriggerScroll, SystemActionKind.StopTask, SystemActionKind.ReportToPlayer }),
    };

    private static readonly IReadOnlyDictionary<IntentKind, IntentActionRow> ByIntent =
        Rows.ToDictionary(row => row.Intent);

    /// <summary>The row of one intent (every SP-2 intent has exactly one).</summary>
    public static IntentActionRow Row(IntentKind intent) =>
        ByIntent.TryGetValue(intent, out IntentActionRow? row)
            ? row
            : throw new ArgumentOutOfRangeException(nameof(intent), intent, "Intent outside the SP-2 frozen table.");

    /// <summary>Every closed verb reachable through some intent row (14 of 19 — see class remarks for the host-driven five).</summary>
    public static IReadOnlySet<SystemActionKind> IntentReachableVerbs => new HashSet<SystemActionKind>(
        Rows.SelectMany(row => row.Actions));

    /// <summary>
    /// Display-only attribution of the three host-driven verbs (AskPlayer/
    /// TodoBoard/SetRally and the rest without a natural intent) to the SP-2
    /// intent their plan cards quote — the synthesized intent feeds journal
    /// identity and report copy only, never routing (SP-2 #1 for SetRally;
    /// the read-only intents for the reporting verbs).
    /// </summary>
    public static IntentKind AttributionIntent(SystemActionKind verb) => verb switch
    {
        SystemActionKind.SetRally => IntentKind.HoldRegion, // SP-2 #1 stance half
        SystemActionKind.HeroOrder => IntentKind.HoldRegion, // hero defends the held lane
        SystemActionKind.AskPlayer => IntentKind.QueryThreat, // clarification-domain attribution
        SystemActionKind.TodoBoard => IntentKind.QueryThreat, // board = findings summary domain
        SystemActionKind.ReportToPlayer => IntentKind.QueryThreat,
        SystemActionKind.LookAround or SystemActionKind.InspectTile or SystemActionKind.QueryWorld
            => IntentKind.QueryThreat,
        SystemActionKind.BuildPlan => IntentKind.Build,
        SystemActionKind.UpgradeTower => IntentKind.Upgrade,
        SystemActionKind.SellTower => IntentKind.Sell,
        SystemActionKind.FocusFire => IntentKind.FocusFire,
        SystemActionKind.SetTowerMode => IntentKind.ChangeFocusMode,
        SystemActionKind.RerouteLogistics or SystemActionKind.SetStockpile => IntentKind.RerouteLogistics,
        SystemActionKind.SetAlarm => IntentKind.SetAlarm,
        SystemActionKind.SpawnScout => IntentKind.Patrol,
        SystemActionKind.TriggerScroll or SystemActionKind.StopTask => IntentKind.ScrollControl,
        _ => IntentKind.QueryThreat,
    };
}
