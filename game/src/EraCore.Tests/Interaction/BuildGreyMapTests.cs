using EraCore.Agent.Actions;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Interaction;

// [C-23][F-119] T-076 两级灰度映射（BuildGreyMap，设计 v3.2 §10.7/G.3 条 3）：
// 一级 黄灰=缺资源（reason 报缺什么/需多少/现有多少/差多少）；二级 红=不可
// 放置（地形/占用/时代锁——保留 BuildPlan 草案结构化拒绝码）。三级=绿。
// 表驱动覆盖：预览格三级、建造栏两级（时代锁红/缺资源黄灰）、同限截断提示。
public class BuildGreyMapTests
{
    private const string Wood = "era1:res-wood";

    // ---- 预览格三级 ----

    [Theory] // legal：可放置且付得起 → 绿（无 reason）
    [InlineData(30, 100)]
    [InlineData(100, 100)] // 恰好付清（cumulative == available）不算缺
    public void Buildable_and_affordable_cell_is_green(double cumulative, double available) // [F-119]
    {
        BuildTintVerdict verdict = BuildGreyMap.Cell(
            new BuildDragPreviewCell(new CellCoord(11, 11), Buildable: true, Rejection: null),
            cumulative, available, Wood);
        Assert.Equal(BuildTintTier.Green, verdict.Tier);
        Assert.Null(verdict.Reason);
    }

    [Theory] // legal：一级灰度——可放置但库存不够 → 黄灰 + 缺资源 tooltip（缺什么/差额）
    [InlineData(100.5, 100, "100.5", "0.5")]
    [InlineData(240, 80, "240", "160")]
    public void Buildable_but_unaffordable_cell_is_yellow_grey_with_the_shortfall( // [F-119]
        double cumulative, double available, string needText, string gapText)
    {
        BuildTintVerdict verdict = BuildGreyMap.Cell(
            new BuildDragPreviewCell(new CellCoord(11, 11), Buildable: true, Rejection: null),
            cumulative, available, Wood);
        Assert.Equal(BuildTintTier.YellowGrey, verdict.Tier);
        Assert.Contains(Wood, verdict.Reason);
        Assert.Contains("缺资源", verdict.Reason);
        Assert.Contains($"需 {needText}", verdict.Reason);
        Assert.Contains($"现有 {available.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}", verdict.Reason);
        Assert.Contains($"差 {gapText}", verdict.Reason);
    }

    [Theory] // illegal：二级灰度——结构化拒绝码 → 红 + 归类原因（地形/占用/时代锁）
    [InlineData("cell-not-buildable", "地形")]
    [InlineData("cell-out-of-bounds", "地形")]
    [InlineData("cell-occupied", "占用")]
    [InlineData("cell-enemy-occupied", "占用")]
    [InlineData("cell-carrier-servicing", "占用")]
    [InlineData("cell-duplicate", "占用")]
    [InlineData("capability-locked:BuildPlan:unlocks-at-era:2", "时代锁")]
    [InlineData("action-era-locked unlock-era=2", "时代锁")]
    public void Rejected_cell_is_red_with_the_structured_reason_classified( // [F-119]
        string rejection, string expectedClass)
    {
        BuildTintVerdict verdict = BuildGreyMap.Cell(
            new BuildDragPreviewCell(new CellCoord(2, 14), Buildable: false, Rejection: rejection),
            cumulativeCost: 0, available: 1000, costCurrency: Wood);
        Assert.Equal(BuildTintTier.Red, verdict.Tier);
        Assert.Contains("不可放置", verdict.Reason);
        Assert.Contains(expectedClass, verdict.Reason);
        Assert.Contains(rejection, verdict.Reason); // 结构化拒绝码原样保留（可审计）
    }

    [Fact] // illegal：不可放置优先于缺资源——红格不降级为黄灰（两级可区分的关键序）
    public void Red_takes_precedence_over_the_resource_tier() // [F-119]
    {
        BuildTintVerdict verdict = BuildGreyMap.Cell(
            new BuildDragPreviewCell(new CellCoord(2, 14), Buildable: false, Rejection: "cell-occupied"),
            cumulativeCost: 9999, available: 1, costCurrency: Wood);
        Assert.Equal(BuildTintTier.Red, verdict.Tier);
    }

    [Fact] // boundary：无拒绝码却不可放置（防御序）→ 红 + 未明提示，不冒充绿/黄
    public void Unbuildable_without_a_rejection_code_is_red_not_silent_green() // [F-119]
    {
        BuildTintVerdict verdict = BuildGreyMap.Cell(
            new BuildDragPreviewCell(new CellCoord(2, 14), Buildable: false, Rejection: null),
            cumulativeCost: 0, available: 100, costCurrency: Wood);
        Assert.Equal(BuildTintTier.Red, verdict.Tier);
        Assert.NotNull(verdict.Reason);
    }

    // ---- 建造栏两级 ----

    [Theory] // legal：已解锁时代 + 付得起 → 绿
    [InlineData(2, 30, 100)] // E2（BuildPlan 解锁时代）+ 木箭塔 30
    [InlineData(5, 60, 60)]
    public void Bar_entry_unlocked_and_affordable_is_green(int era, double cost, double available) // [F-119]
    {
        Assert.Equal(BuildTintTier.Green, BuildGreyMap.BarEntry(era, cost, available, Wood).Tier);
    }

    [Fact] // legal：一级——缺资源 → 黄灰 + 造价缺口 tooltip
    public void Bar_entry_unaffordable_is_yellow_grey_naming_the_shortfall() // [F-119]
    {
        BuildTintVerdict verdict = BuildGreyMap.BarEntry(currentEra: 2, unitCost: 60, available: 45, costCurrency: Wood);
        Assert.Equal(BuildTintTier.YellowGrey, verdict.Tier);
        Assert.Contains("缺资源", verdict.Reason);
        Assert.Contains("差 15", verdict.Reason);
    }

    [Theory] // illegal：二级——时代锁（K-3 冻结表：BuildPlan 解锁于 E2）→ 红，先于资源判定
    [InlineData(1)]
    [InlineData(0)]
    public void Bar_entry_in_a_locked_era_is_red_even_when_rich(int era) // [F-119]
    {
        BuildTintVerdict verdict = BuildGreyMap.BarEntry(era, unitCost: 30, available: 100_000, costCurrency: Wood);
        Assert.Equal(BuildTintTier.Red, verdict.Tier);
        Assert.Contains("时代锁", verdict.Reason);
        int unlockEra = ActionCatalog.Descriptor(SystemActionKind.BuildPlan).UnlockEra;
        Assert.Contains($"E{unlockEra}", verdict.Reason);
    }

    [Fact] // boundary：解锁时代读 K-3 冻结表而非本类自持数值（表驱动单源）
    public void Bar_entry_unlock_era_comes_from_the_frozen_catalog() // [F-119]
    {
        Assert.Equal(2, ActionCatalog.Descriptor(SystemActionKind.BuildPlan).UnlockEra);
        // E2 起不再有时代锁红——E2/E3 均进入资源两级判定
        Assert.All(new[] { 2, 3, 6 }, era =>
            Assert.NotEqual(BuildTintTier.Red, BuildGreyMap.BarEntry(era, 30, 0, Wood).Tier));
    }

    // ---- 批量同限截断提示 ----

    [Theory] // legal：截断提示含「军师与你同限」口号 + 保留格数 + 上限读 K-3 冻结表
    [InlineData(8, 2)]
    [InlineData(6, 1)]
    public void Cap_truncation_note_names_the_shared_limit(int kept, int era) // [F-036]
    {
        string note = BuildGreyMap.CapTruncationNote(kept, era);
        Assert.Contains("军师与你同限", note);
        Assert.Contains($"前 {kept} 格", note);
        Assert.Contains($"cells≤4+2×E={PlayerBuildLane.MaxBuildCells(era)}", note);
    }

    [Fact] // boundary：提示中的上限随时代放大（E1=6 → E3=10），证明读表而非常量
    public void Cap_note_limit_scales_with_the_era() // [F-036]
    {
        Assert.Contains("=6", BuildGreyMap.CapTruncationNote(6, 1));
        Assert.Contains("=10", BuildGreyMap.CapTruncationNote(10, 3));
    }
}
