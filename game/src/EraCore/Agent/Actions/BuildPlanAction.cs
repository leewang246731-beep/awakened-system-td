using EraCore.Contracts.Foundation;

namespace EraCore.Agent.Actions;

/// <summary>
/// One placement step of a build-plan draft: the cell, its cost share,
/// whether it is buildable against the current world state, and — when not
/// buildable — the machine-readable rejection code.
/// </summary>
/// <param name="Cell">Target cell of this step.</param>
/// <param name="Cost">Cost share of this step (unit cost when buildable, 0 otherwise).</param>
/// <param name="Buildable">True when the cell passes terrain/occupancy checks now.</param>
/// <param name="Rejection">Rejection code for non-buildable steps; null otherwise.</param>
/// <param name="DemolishesExisting">True when this step tears down an existing building (explicit confirm_overwrite).</param>
public sealed record BuildPlanStep(
    CellCoord Cell,
    double Cost,
    bool Buildable,
    string? Rejection,
    bool DemolishesExisting);

/// <summary>
/// The whole build-plan draft (C-03 second real handler output, T-051).
/// A DRAFT, never an execution: the plan-step list, total cost and risk
/// highlights feed the approval gate and the plan card; actual placement
/// is the C-13 pipeline's job (T-053) through the SimCommand lane.
/// </summary>
/// <param name="TowerId">Content id of the tower this plan places.</param>
/// <param name="UnitCost">Build cost of one tower (content catalog price).</param>
/// <param name="TotalCost">Sum of buildable step costs.</param>
/// <param name="AvailableCurrency">Cost-currency amount available right now.</param>
/// <param name="Affordable">True when the pool covers the total cost.</param>
/// <param name="Steps">One step per requested cell, request order.</param>
/// <param name="Risks">Risk highlights shown on the plan card (demolitions, shortfalls, rejections).</param>
public sealed record BuildPlanDraft(
    string TowerId,
    double UnitCost,
    double TotalCost,
    double AvailableCurrency,
    bool Affordable,
    IReadOnlyList<BuildPlanStep> Steps,
    IReadOnlyList<string> Risks);

/// <summary>
/// BuildPlan handler (C-03, T-051): validates the target tower and every
/// requested cell — occupancy, terrain, duplicates, cost — and returns a
/// plan draft. It NEVER places anything and never mutates the world view;
/// execution belongs to the five-stage pipeline (T-053).
/// </summary>
public sealed class BuildPlanAction
{
    /// <summary>
    /// Produces the draft. Returns null when the tower id is unknown to
    /// the level catalog (the registry maps that to ErrTargetInvalid —
    /// schema validation cannot know the catalog).
    /// </summary>
    /// <param name="view">World state to validate against.</param>
    /// <param name="towerId">Target tower content id.</param>
    /// <param name="cells">Requested placement cells (schema: 1 … 4+2×era).</param>
    /// <param name="confirmOverwrite">Explicit overwrite bit for demolishing existing buildings.</param>
    public BuildPlanDraft? Draft(
        IActionWorldView view,
        string towerId,
        IReadOnlyList<CellCoord> cells,
        bool confirmOverwrite)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(towerId);
        ArgumentNullException.ThrowIfNull(cells);

        if (!view.TryGetTowerCost(towerId, out double unitCost))
        {
            return null;
        }

        var steps = new List<BuildPlanStep>(cells.Count);
        var risks = new List<string>();
        var seenCells = new HashSet<CellCoord>();
        double totalCost = 0;
        foreach (CellCoord cell in cells)
        {
            ArgumentNullException.ThrowIfNull(cell);

            if (!view.IsInsideGrid(cell))
            {
                steps.Add(new BuildPlanStep(cell, 0, Buildable: false, "cell-out-of-bounds", DemolishesExisting: false));
                risks.Add($"rejected ({cell.X},{cell.Y}): cell-out-of-bounds");
                continue;
            }

            if (!seenCells.Add(cell))
            {
                steps.Add(new BuildPlanStep(cell, 0, Buildable: false, "cell-duplicate", DemolishesExisting: false));
                risks.Add($"rejected ({cell.X},{cell.Y}): cell-duplicate");
                continue;
            }

            if (!view.IsBuildable(cell, out string? rejection))
            {
                if (rejection == "cell-occupied")
                {
                    if (confirmOverwrite)
                    {
                        steps.Add(new BuildPlanStep(cell, unitCost, Buildable: true, null, DemolishesExisting: true));
                        totalCost += unitCost;
                        risks.Add($"demolition ({cell.X},{cell.Y}): existing building will be torn down (confirm_overwrite)");
                    }
                    else
                    {
                        steps.Add(new BuildPlanStep(cell, 0, Buildable: false, "cell-occupied", DemolishesExisting: false));
                        risks.Add($"rejected ({cell.X},{cell.Y}): cell-occupied requires confirm_overwrite=true");
                    }
                }
                else
                {
                    steps.Add(new BuildPlanStep(cell, 0, Buildable: false, rejection, DemolishesExisting: false));
                    risks.Add($"rejected ({cell.X},{cell.Y}): {rejection}");
                }

                continue;
            }

            steps.Add(new BuildPlanStep(cell, unitCost, Buildable: true, null, DemolishesExisting: false));
            totalCost += unitCost;
        }

        double available = view.ResourceAmount(view.CostCurrency);
        bool affordable = available >= totalCost;
        if (!affordable)
        {
            risks.Add(
                $"insufficient-resources: need {totalCost.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}"
                + $" have {available.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (steps.Count > 0 && steps.TrueForAll(step => !step.Buildable))
        {
            risks.Add("no-buildable-cells: every requested cell was rejected");
        }

        return new BuildPlanDraft(towerId, unitCost, totalCost, available, affordable, steps, risks);
    }
}
