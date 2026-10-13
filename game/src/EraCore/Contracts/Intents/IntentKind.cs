// T2: pure contract — K-4 intent layer, T-022, draft (12-intent enum frozen by 07 correction #19/A11; parser signature rolling with C-15).

namespace EraCore.Contracts.Intents;

/// <summary>
/// The 12 commander intents (K-4), enumerated by 07-decided-plan correction
/// table #19 (A11). The L1 grammar (C-15), the L3 cloud tier (C-16) and the
/// future L2 local tier (C-27) all parse military orders into this one
/// enumeration.
/// </summary>
public enum IntentKind
{
    /// <summary>守住X — hold a named region or target (target-level template, §11.5).</summary>
    HoldRegion,

    /// <summary>建造 — build towers/buildings; feeds BuildPlan.</summary>
    Build,

    /// <summary>升级 — upgrade an existing tower; feeds UpgradeTower.</summary>
    Upgrade,

    /// <summary>出售 — sell towers; feeds SellTower (danger bit).</summary>
    Sell,

    /// <summary>集火 — focus fire on a target; feeds FocusFire.</summary>
    FocusFire,

    /// <summary>改集火模式 — change tower targeting mode; feeds SetTowerMode.</summary>
    ChangeFocusMode,

    /// <summary>物流改道 — reroute logistics flows; feeds RerouteLogistics.</summary>
    RerouteLogistics,

    /// <summary>储备查询 — query stockpiles; read-only.</summary>
    QueryStockpile,

    /// <summary>敌情查询 — query the enemy situation; read-only.</summary>
    QueryThreat,

    /// <summary>设闹 — set a timed alarm; feeds SetAlarm.</summary>
    SetAlarm,

    /// <summary>巡逻 — patrol/scout a lane; feeds SpawnScout.</summary>
    Patrol,

    /// <summary>卷轴触发+停止 — trigger an equipped scroll and stop resident scrolls; feeds TriggerScroll / StopTask.</summary>
    ScrollControl,
}
