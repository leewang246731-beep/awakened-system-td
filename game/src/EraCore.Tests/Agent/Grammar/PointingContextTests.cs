using EraCore.Agent.Grammar;
using Xunit;

namespace EraCore.Tests.Agent.Grammar;

// [C-15] [F-077] 多模态指认上下文（T-069；v3.2 §11.2/§11.5 指认补槽）：
// 纯数据记录——最近选中实体 + 最近选中格 + 镜头中心格；解析器用它消解
// 这个/这里 类指认槽。presentation 后续接鼠标（本层零 UI 依赖）。
// [C-15] [F-077] 类级关联：本类全部方法覆盖 T-069 多模态指认上下文
public class PointingContextTests
{
    [Fact] // legal：实体指认 → sel:{id}；位置指认 → 最近选中格优先
    public void Resolves_selected_entity_and_cell()
    {
        var context = new PointingContext(
            SelectedId: "era1:tower-campfire",
            SelectedCellX: 3, SelectedCellY: 4,
            CameraCellX: 12, CameraCellY: 34);

        Assert.Equal("sel:era1:tower-campfire", context.ResolveSelected());
        Assert.Equal("cell:3,4", context.ResolveCell());
    }

    [Fact] // boundary：无最近选中格 → 位置指认回落镜头中心格（最近选中/镜头中心两级）
    public void ResolveCell_falls_back_to_camera_center()
    {
        var context = new PointingContext(null, null, null, 12, 34);

        Assert.Equal("cell:12,34", context.ResolveCell());
    }

    [Fact] // illegal：无选中实体/无任何格 → null（调用方按缺槽走澄清卡，绝不猜）
    public void Unresolvable_pointing_yields_null()
    {
        var empty = new PointingContext(null, null, null, null, null);

        Assert.False(empty.HasSelection);
        Assert.False(empty.HasCell);
        Assert.Null(empty.ResolveSelected());
        Assert.Null(empty.ResolveCell());

        var onlyCell = new PointingContext(null, null, null, 1, 2);
        Assert.Null(onlyCell.ResolveSelected()); // 位置格不能顶替实体指认
    }

    [Fact] // [F-077] 纯数据等值（record 语义：同事实同上下文）
    public void Contexts_with_same_facts_are_equal()
    {
        Assert.Equal(
            new PointingContext("a", 1, 2, 3, 4),
            new PointingContext("a", 1, 2, 3, 4));
    }
}
