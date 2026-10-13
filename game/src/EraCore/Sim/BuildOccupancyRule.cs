using EraCore.Contracts.Foundation;
using EraCore.Logistics;
using EraCore.Units;

namespace EraCore.Sim;

// T-056 (C-23) DEBT spike2-pit#6 清账（spike2-report §5 坑 6）：spike② 的
// 容忍语义是"落闸格站民夫容忍按旧折线穿行、计数封顶 ≤2"——产品化改为
// 占格建筑放置 × 单位站位的正式互斥/推移规则。本文件是纯规则体（无时钟/
// 无随机，CS-1.4），被两处共享：
//   1) Agent.Actions.SimulationActionView.IsBuildable——BuildPlan 草案/
//      拖放预览的格子校验（红格拒绝码从这里来）；
//   2) Simulation.ValidateBuildableCell——SimCommand 执行道的同款再校验。
// 拒绝码两侧同源同词表：同一咽喉（ActionRegistry→BuildPlan→SimCommand）
// 的两段用同一份判定，预览红格与执行拒绝不可能各说各话。

/// <summary>
/// spike2-pit#6 occupancy rule (T-056): how a placement cell and the units
/// currently standing on it interact. Three branches — a living enemy inside
/// the cell rejects the placement outright (红格); a porter in a service
/// window on that cell (Loading at home / Unloading at destination) rejects
/// it too; a moving porter is NOT a rejection — the placement succeeds and
/// the porter is pushed (rerouted around the new building) by the sim's
/// placement follow-up.
/// </summary>
public static class BuildOccupancyRule
{
    /// <summary>Rejection code when a living enemy stands inside the cell.</summary>
    public const string EnemyStandingRejection = "cell-enemy-occupied";

    /// <summary>Rejection code when a porter is loading/unloading on the cell.</summary>
    public const string CarrierServicingRejection = "cell-carrier-servicing";

    /// <summary>
    /// True when any living enemy's position lies inside <paramref name="cell"/>
    /// (unit positions are pixel-domain; one cell =
    /// <see cref="Simulation.CellSizePx"/> px). Dead units never block.
    /// </summary>
    public static bool EnemyStandingOn(CellCoord cell, IReadOnlyList<Unit> units)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(units);
        foreach (Unit unit in units)
        {
            if (!unit.Alive)
            {
                continue;
            }

            int unitCellX = (int)Math.Floor(unit.X / Simulation.CellSizePx);
            int unitCellY = (int)Math.Floor(unit.Y / Simulation.CellSizePx);
            if (unitCellX == cell.X && unitCellY == cell.Y)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when any porter is in a service window ON the cell — Loading at
    /// a home equal to it, or Unloading at a destination equal to it. A
    /// servicing porter holds cargo against its endpoints (待卸队列), so the
    /// building cannot take the cell until the window closes; a moving
    /// porter is NOT a rejection (推移, see
    /// <see cref="Simulation"/> placement follow-up).
    /// </summary>
    public static bool CarrierServicingOn(CellCoord cell, IReadOnlyList<Carrier> carriers)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(carriers);
        foreach (Carrier carrier in carriers)
        {
            if (carrier.Phase == CarrierPhase.Loading && carrier.Home.Equals(cell))
            {
                return true;
            }

            if (carrier.Phase == CarrierPhase.Unloading && carrier.Destination.Equals(cell))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The buildability verdict for the unit layer: null when the cell may
    /// take the building (moving porters included — they get pushed on
    /// placement), otherwise the shared rejection code.
    /// </summary>
    public static string? UnitLayerRejection(CellCoord cell, IReadOnlyList<Unit> units, IReadOnlyList<Carrier> carriers)
    {
        if (EnemyStandingOn(cell, units))
        {
            return EnemyStandingRejection;
        }

        return CarrierServicingOn(cell, carriers) ? CarrierServicingRejection : null;
    }
}
