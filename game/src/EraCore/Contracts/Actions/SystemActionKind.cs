// T2: pure contract — K-3 system action schema, T-021, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Actions;

/// <summary>
/// The closed verb list of system actions (K-3): exactly 19 actions in five
/// groups (recon / construction / logistics / combat / management). The
/// per-verb constraint summaries below are the binding digest of the
/// recovered v2.1 §11.3 field table with all v3.2 overwrites applied
/// (docs/engineering/10-recovered-v21-architecture.md §三; permission tiers
/// per correction #20/A12, unlock eras per #10/N9). The machine-readable
/// JSON Schema built on this list is the C-03 deliverable (single schema
/// source, dual projection — 07 D-14). Mods may extend parameter spaces via
/// <c>ContentType.ActionParamSpace</c> but never this verb list.
/// Point-cost shorthand below: recon 0, construction 2, logistics 1, combat 1,
/// patrol 1 (full pricing metadata lives in the C-03 registry, §11.4).
/// </summary>
public enum SystemActionKind
{
    // ------------------------------------------------------- recon (0 pt, allow)

    /// <summary>
    /// LookAround — recon, 0 pt, allow, era E1+. No fields: returns the
    /// situation overview (answers the elder's question, drives alarms).
    /// </summary>
    LookAround,

    /// <summary>
    /// InspectTile — recon, 0 pt, allow, E1+. Fields: <c>cell:(x,y)</c>;
    /// <c>aspect: stats|threat|supply</c>.
    /// </summary>
    InspectTile,

    /// <summary>
    /// QueryWorld — recon, 0 pt, allow, E1+. Fields:
    /// <c>key: wave|resource|threat|paths</c>; <c>scope</c>;
    /// <c>head_limit</c> ≤ 20 rows.
    /// </summary>
    QueryWorld,

    // ------------------------------------------ construction (2 pt, ask, era E2+)

    /// <summary>
    /// BuildPlan — construction, 2 pt, ask, E2+. Fields: <c>tower_id</c>
    /// (unlocked towers only); <c>cells[]</c> count ≤ 4 + 2 × era index
    /// (E1=1 … finale=6); explicit <c>confirm_overwrite=true</c> required to
    /// demolish existing buildings. Player drag-build shares this same path
    /// and the same cell cap (D-25).
    /// </summary>
    BuildPlan,

    /// <summary>
    /// UpgradeTower — construction, 2 pt, ask, E2+. Fields:
    /// <c>target_id</c>; <c>to_tier</c> in 1..4.
    /// </summary>
    UpgradeTower,

    /// <summary>
    /// SellTower — construction, 2 pt, ask, E2+. Fields: <c>target_id</c>
    /// plus explicit danger bit <c>confirm_sell=true</c>. NEVER enters any
    /// auto-approve list (F.4).
    /// </summary>
    SellTower,

    /// <summary>
    /// SetRally — construction, 2 pt, ask, E2+. Fields: <c>lane</c>
    /// (language-neutral stable id with localized display mapping);
    /// <c>stance: hold|fall|push</c>.
    /// </summary>
    SetRally,

    // ------------------------------------------------- logistics (1 pt, ask, E2+)

    /// <summary>
    /// RerouteLogistics — logistics, 1 pt, ask, E2+ (same wave as the new
    /// logistics, §11.8 schedule). Fields: <c>from</c>, <c>to</c>,
    /// <c>item</c>, <c>rate</c> in [0.0, 1.0].
    /// </summary>
    RerouteLogistics,

    /// <summary>
    /// SetStockpile — logistics, 1 pt, ask, E2+. Fields: <c>item_id</c>,
    /// <c>min_amount</c> (resource units).
    /// </summary>
    SetStockpile,

    // ----------------------------------------------------- combat (1 pt, ask, E2+)

    /// <summary>
    /// FocusFire — combat, 1 pt, ask, E2+. Fields: <c>target_id</c>; effect
    /// persists until the target dies or a new order supersedes it.
    /// </summary>
    FocusFire,

    /// <summary>
    /// SetTowerMode — combat, 1 pt, ask, E2+. Fields: <c>target_id[]</c>
    /// count ≤ 6; <c>mode: first|last|strong|closest</c>.
    /// </summary>
    SetTowerMode,

    /// <summary>
    /// TriggerScroll — combat, 1 pt, ask, E2+ (requires an equipped scroll).
    /// Fields: <c>scroll_id</c> (equipped scrolls only). Costs 1 system point
    /// plus a 3 s per-scroll internal cooldown and does NOT count against
    /// the resident scroll cap (appendix F.1).
    /// </summary>
    TriggerScroll,

    /// <summary>
    /// HeroOrder — combat tier, ask (level-level auto-approve may be
    /// whitelisted, correction #20), unlocked with the hero (per-era 3rd
    /// level). Fields: <c>order: move|skill|retreat</c>, skills limited to
    /// unlocked ones.
    /// </summary>
    HeroOrder,

    // ------------------------------------------- management (allow; costs per registry)

    /// <summary>
    /// AskPlayer — management, allow, E1+ (Q&amp;A). Fields:
    /// <c>question</c>; <c>options</c> count 2..4 with label length ≤ 8
    /// chars; the system appends "其他"; <c>multi_select</c> required.
    /// </summary>
    AskPlayer,

    /// <summary>
    /// ReportToPlayer — management, allow, E1+ (alarms/reports). Fields:
    /// <c>summary</c> ≤ 200 chars; <c>findings</c> ≤ 8 items;
    /// <c>verdict: CONFIRMED|RUMOR</c>. Text-length and suspicious-link
    /// filtering is mandatory (anti-injection through the report channel).
    /// </summary>
    ReportToPlayer,

    /// <summary>
    /// TodoBoard — management, allow, E2+ (execution phase). Fields:
    /// <c>todos[]</c> — full rewrite of the board on every call.
    /// </summary>
    TodoBoard,

    /// <summary>
    /// SpawnScout — management (patrol), 1 pt, allow, E2+ (patrol tier,
    /// N9/A2). Fields: <c>lane</c>; <c>duration</c> clamped to [15, 120] s.
    /// Calling it in E1 returns <c>ErrCapabilityLocked</c> plus clarifying
    /// copy.
    /// </summary>
    SpawnScout,

    /// <summary>
    /// SetAlarm — management, allow, E1+ (alarms). Fields:
    /// <c>after_seconds</c> clamped to [30, 600] s; <c>reason</c> required.
    /// </summary>
    SetAlarm,

    /// <summary>
    /// StopTask — management, allow, E2+ (execution phase). Fields:
    /// <c>task_id</c> — the unified task handle issued by the pipeline.
    /// </summary>
    StopTask,
}
