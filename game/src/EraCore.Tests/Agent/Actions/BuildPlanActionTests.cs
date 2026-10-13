using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-071] C-03 BuildPlan handler：草案语义（占用格/地形/成本校验→计划步+风险标注，不直接执行）
public class BuildPlanActionTests
{
    private readonly BuildPlanAction _action = new();

    private static (double UnitCost, int InitialWood) Anchors() => (SimEra1Content.WoodArrowTower().CostTotal, 100);

    [Fact]
    public void Draft_prices_free_cells_from_the_catalog()
    {
        // legal：两格空地 → 步步可建、总价=单价×2、余量充足、零风险
        var (_, view) = RegistryFixture.Create();
        (double unitCost, int wood) = Anchors();
        BuildPlanDraft draft = _action.Draft(view, "era1:tower-wood-arrow", RegistryFixture.FreeCells(view, 2), confirmOverwrite: false)!;

        Assert.NotNull(draft);
        Assert.Equal(unitCost, draft.UnitCost);
        Assert.Equal(unitCost * 2, draft.TotalCost);
        Assert.Equal(wood, draft.AvailableCurrency);
        Assert.True(draft.Affordable);
        Assert.All(draft.Steps, step => Assert.True(step.Buildable));
        Assert.Empty(draft.Risks);
    }

    [Fact]
    public void Draft_flags_occupied_cells_and_requires_explicit_overwrite()
    {
        // boundary：占用格（仓库）默认拒绝；显式 confirm_overwrite 才转为拆除步
        var (_, view) = RegistryFixture.Create();
        (double unitCost, _) = Anchors();
        var cells = new[] { SimE1Fixture.WarehouseCell };

        BuildPlanDraft refused = _action.Draft(view, "era1:tower-wood-arrow", cells, confirmOverwrite: false)!;
        BuildPlanStep step = Assert.Single(refused.Steps);
        Assert.False(step.Buildable);
        Assert.Equal("cell-occupied", step.Rejection);
        Assert.Contains(refused.Risks, r => r.Contains("cell-occupied") && r.Contains("confirm_overwrite"));

        BuildPlanDraft overwrite = _action.Draft(view, "era1:tower-wood-arrow", cells, confirmOverwrite: true)!;
        BuildPlanStep demolition = Assert.Single(overwrite.Steps);
        Assert.True(demolition.Buildable);
        Assert.True(demolition.DemolishesExisting);
        Assert.Equal(unitCost, demolition.Cost);
        Assert.Contains(overwrite.Risks, r => r.Contains("demolition"));
    }

    [Fact]
    public void Draft_rejects_out_of_bounds_and_duplicate_cells()
    {
        // illegal：界外格与重复格各自给出机器可读拒绝码
        var (_, view) = RegistryFixture.Create();
        CellCoord free = RegistryFixture.FreeCells(view, 1)[0];
        BuildPlanDraft draft = _action.Draft(view, "era1:tower-wood-arrow", new[]
        {
            new CellCoord(-3, 4), free, free,
        }, confirmOverwrite: false)!;

        Assert.Equal("cell-out-of-bounds", draft.Steps[0].Rejection);
        Assert.Equal("cell-duplicate", draft.Steps[2].Rejection);
        Assert.True(draft.Steps[1].Buildable);
        Assert.Equal(draft.UnitCost, draft.TotalCost); // 只有可建步计价
        Assert.Contains(draft.Risks, r => r.Contains("cell-out-of-bounds"));
        Assert.Contains(draft.Risks, r => r.Contains("cell-duplicate"));
    }

    [Fact]
    public void Draft_marks_shortfall_when_pool_cannot_cover_the_total()
    {
        // boundary→illegal：4 格总价 120 > 初始 100 → Affordable=false + 资源不足风险标注
        var (_, view) = RegistryFixture.Create();
        BuildPlanDraft draft = _action.Draft(view, "era1:tower-wood-arrow", RegistryFixture.FreeCells(view, 4), confirmOverwrite: false)!;

        Assert.False(draft.Affordable);
        Assert.Contains(draft.Risks, r => r.Contains("insufficient-resources"));
    }

    [Fact]
    public void Draft_returns_null_for_towers_outside_the_catalog()
    {
        // illegal：目录外塔 id → null（注册表映射 ErrTargetInvalid）
        var (_, view) = RegistryFixture.Create();
        Assert.Null(_action.Draft(view, "era1:tower-missing", RegistryFixture.FreeCells(view, 1), confirmOverwrite: false));
    }

    [Fact]
    public void Draft_notes_when_no_cell_is_buildable()
    {
        // illegal：全部请求格被拒 → 汇总风险行
        var (_, view) = RegistryFixture.Create();
        BuildPlanDraft draft = _action.Draft(view, "era1:tower-wood-arrow", new[]
        {
            SimE1Fixture.WarehouseCell, new CellCoord(-1, -1),
        }, confirmOverwrite: false)!;
        Assert.All(draft.Steps, step => Assert.False(step.Buildable));
        Assert.Contains(draft.Risks, r => r.Contains("no-buildable-cells"));
    }

    [Fact]
    public void Draft_rejects_null_arguments()
    {
        // illegal
        var (_, view) = RegistryFixture.Create();
        Assert.Throws<ArgumentNullException>(() => _action.Draft(null!, "era1:tower-wood-arrow", new[] { new CellCoord(1, 1) }, false));
        Assert.Throws<ArgumentNullException>(() => _action.Draft(view, null!, new[] { new CellCoord(1, 1) }, false));
        Assert.Throws<ArgumentNullException>(() => _action.Draft(view, "era1:tower-wood-arrow", null!, false));
    }
}
