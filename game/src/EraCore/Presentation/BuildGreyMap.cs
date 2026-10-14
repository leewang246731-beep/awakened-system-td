using System.Globalization;
using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;

namespace EraCore.Presentation;

// T-076 (C-23 全量建造交互, F-119 两级灰度)：设计 v3.2 §10.7（G.3 条 3
// 「建造栏两级灰度：按动词分类、两级灰度、逐项造价缺口」）的纯映射——
//   一级 黄灰 = 缺资源：格/塔本身可放置，但造价超过库存（reason 报缺
//               什么资源、需多少、现有多少、差多少）；
//   二级 红   = 不可放置：地形/出界、占用（建筑/敌/民夫/重复）、时代锁
//               （reason 归类 + 保留 BuildPlan 草案的结构化拒绝码）。
// 判定输入完全来自 BuildDragController 的预览帧（合法性=BuildPlan 草案
// 逐步输出，与军师同源）与 K-3 冻结表的解锁时代列——本类不自持数值
// （CS-2.3），只做「结构化原因 → 两级灰度 + 人读 tooltip」的纯函数映射。

/// <summary>Two-tier grey scale plus the healthy green (§10.7, F-119).</summary>
public enum BuildTintTier
{
    /// <summary>Placeable and affordable — build away.</summary>
    Green = 0,

    /// <summary>Tier 1 grey-yellow: placeable but the stock cannot cover it (缺资源).</summary>
    YellowGrey = 1,

    /// <summary>Tier 2 red: not placeable — terrain/occupancy/era lock (不可放置).</summary>
    Red = 2,
}

/// <summary>One grey-map verdict: the tier plus the human-readable reason (tooltip copy).</summary>
/// <param name="Tier">Which of the three tints paints this cell/button.</param>
/// <param name="Reason">Tooltip/status copy; null on green (nothing to explain).</param>
public sealed record BuildTintVerdict(BuildTintTier Tier, string? Reason);

/// <summary>
/// The two-tier grey mapper (T-076, F-119): maps BuildPlan draft output
/// (per-cell rejection codes) and affordability arithmetic onto the
/// green / yellow-grey / red scale with structured reasons — one pure
/// function per surface (drag-preview cell, build-bar entry, cap notice).
/// </summary>
public static class BuildGreyMap
{
    /// <summary>
    /// Drag-preview cell tint. The affordability check is cumulative in
    /// plan order: the caller adds the unit cost of each buildable cell
    /// (including the one being judged) before calling, so the first
    /// buildable cells stay green until the stock runs out and the rest
    /// turn yellow-grey — exactly which towers the released batch would
    /// fail to pay for.
    /// </summary>
    /// <param name="cell">One preview cell (BuildPlan draft verdict).</param>
    /// <param name="cumulativeCost">Cost through this cell inclusive (buildable cells only).</param>
    /// <param name="available">Cost-currency amount available right now.</param>
    /// <param name="costCurrency">Cost currency id (tooltip names the missing resource).</param>
    public static BuildTintVerdict Cell(BuildDragPreviewCell cell, double cumulativeCost, double available, string costCurrency)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (!cell.Buildable)
        {
            return new BuildTintVerdict(BuildTintTier.Red, RedReason(cell.Rejection));
        }

        return cumulativeCost > available
            ? new BuildTintVerdict(BuildTintTier.YellowGrey, MissingResourceText(cumulativeCost, available, costCurrency))
            : new BuildTintVerdict(BuildTintTier.Green, null);
    }

    /// <summary>
    /// Build-bar entry tint (§10.7 first surface): era-locked verb = red
    /// with the era reason; affordable = green; otherwise yellow-grey
    /// naming the shortfall. The unlock era is read off the K-3 frozen
    /// catalog, never a local copy.
    /// </summary>
    /// <param name="currentEra">Era ordinal the interaction runs in.</param>
    /// <param name="unitCost">One tower's cost.</param>
    /// <param name="available">Cost-currency amount available right now.</param>
    /// <param name="costCurrency">Cost currency id.</param>
    public static BuildTintVerdict BarEntry(int currentEra, double unitCost, double available, string costCurrency)
    {
        int unlockEra = ActionCatalog.Descriptor(SystemActionKind.BuildPlan).UnlockEra;
        if (unlockEra > currentEra)
        {
            return new BuildTintVerdict(
                BuildTintTier.Red, $"不可放置：时代锁（BuildPlan 解锁于 E{unlockEra}，当前 E{currentEra}）");
        }

        return unitCost > available
            ? new BuildTintVerdict(BuildTintTier.YellowGrey, MissingResourceText(unitCost, available, costCurrency))
            : new BuildTintVerdict(BuildTintTier.Green, null);
    }

    /// <summary>
    /// The 缺资源 tooltip line: names the resource, the need, the stock and
    /// the shortfall (逐项造价缺口, §10.7). Invariant number formatting —
    /// smoke output and journals must not drift with the host locale.
    /// </summary>
    public static string MissingResourceText(double needed, double available, string currency)
    {
        string need = needed.ToString("0.#", CultureInfo.InvariantCulture);
        string have = available.ToString("0.#", CultureInfo.InvariantCulture);
        string shortfall = (needed - available).ToString("0.#", CultureInfo.InvariantCulture);
        return $"缺资源：{currency} 需 {need}、现有 {have}（差 {shortfall}）";
    }

    /// <summary>
    /// The batch-cap truncation notice (批量同限 UX): the shared
    /// cells≤4+2×E limit truncated the rectangle and the advisor is bound
    /// by the very same cap — 玩家与系统公平同规（v3.2 §6 L82）.
    /// </summary>
    /// <param name="keptCells">Cells the cap kept (row-major prefix).</param>
    /// <param name="eraOrdinal">Era ordinal the cap reads.</param>
    public static string CapTruncationNote(int keptCells, int eraOrdinal)
    {
        int cap = PlayerBuildLane.MaxBuildCells(eraOrdinal);
        return $"军师与你同限：矩形已截断至前 {keptCells} 格（本时代上限 cells≤4+2×E={cap}）";
    }

    /// <summary>
    /// Classifies a BuildPlan draft rejection code into the red-tier
    /// reason (§10.7: 不可放置原因=地形/占用/时代锁), keeping the
    /// structured code verbatim so the copy stays auditable against the
    /// registry's own vocabulary.
    /// </summary>
    private static string RedReason(string? rejection) => rejection switch
    {
        "cell-not-buildable" or "cell-out-of-bounds" => $"不可放置：地形（{rejection}）",
        "cell-occupied" or "cell-enemy-occupied" or "cell-carrier-servicing" or "cell-duplicate"
            => $"不可放置：占用（{rejection}）",
        "tower-unknown" => $"不可放置：塔不在目录（{rejection}）",
        not null when rejection.Contains("capability-locked", StringComparison.Ordinal)
            || rejection.Contains("era-locked", StringComparison.Ordinal)
            => $"不可放置：时代锁（{rejection}）",
        not null => $"不可放置：{rejection}",
        _ => "不可放置：未明拒绝（无拒绝码）",
    };
}
