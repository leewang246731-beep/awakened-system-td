using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-21] [F-159] T-102 渲染决策表（RendererDecisionMap）：每档渲染决策
// headless 可测——L（烘焙跳过/特效减半/后期关）、M（现默认）、H（全开+更细
// 脏粒度）；帧预算估算模型同式打分三档；按需直绘的视野 chunk 圈选。
// 重负载（spike1 场景）的整窗预算仿真见 RenderBudgetSimulationTests。
public class RendererDecisionMapTests
{
    private const int Requests = 40; // canonical request count: ~spike1 稳态每 tick 真实射击量级

    // ------------------------------------------------ 三档决策表差异（可测核心）

    [Fact] // legal — L 档决策：静态烘焙跳过 + 特效 spawn 减半 + 扫描线/后期关
    public void Low_tier_decisions_skip_bake_halve_effects_and_drop_overlays()
    {
        RenderFramePlan plan = RendererDecisionMap.PlanFrame(RenderTierMatrix.Low, Requests);

        Assert.False(plan.BakeStaticGround);          // 静态烘焙跳过（改按需直绘）
        Assert.Equal(20, plan.EffectSpawnsAllowed);   // 40 × 0.5 = 20（减半）
        Assert.Equal(20, plan.EffectSpawnsDropped);
        Assert.False(plan.ScanlinePass);
        Assert.False(plan.PostProcessPass);           // 后期关
        Assert.Equal(32, plan.ChunkCells);
    }

    [Fact] // legal — M 档决策：现默认（烘焙+全特效+全开）
    public void Medium_tier_decisions_match_the_current_default()
    {
        RenderFramePlan plan = RendererDecisionMap.PlanFrame(RenderTierMatrix.Medium, Requests);

        Assert.True(plan.BakeStaticGround);
        Assert.Equal(40, plan.EffectSpawnsAllowed);   // 密度 1.0：一个不丢
        Assert.Equal(0, plan.EffectSpawnsDropped);
        Assert.True(plan.ScanlinePass);
        Assert.True(plan.PostProcessPass);
    }

    [Fact] // legal — H 档决策：全开 + 16 格更细脏粒度
    public void High_tier_decisions_keep_everything_on_with_finer_chunks()
    {
        RenderFramePlan plan = RendererDecisionMap.PlanFrame(RenderTierMatrix.High, Requests);

        Assert.True(plan.BakeStaticGround);
        Assert.Equal(16, plan.ChunkCells);            // 更细脏粒度
        Assert.Equal(40, plan.EffectSpawnsAllowed);
        Assert.True(plan.ScanlinePass);
        Assert.True(plan.PostProcessPass);
    }

    [Fact] // legal — 三档决策表两两不同（任务卡验收：三档差异可测）
    public void Three_tier_decision_tables_are_pairwise_distinct()
    {
        var plans = RenderTierMatrix.AllTiers
            .Select(tier => RendererDecisionMap.PlanFrame(RenderTierMatrix.Capabilities(tier), Requests))
            .ToArray();

        for (int i = 0; i < plans.Length; i++)
        {
            for (int j = i + 1; j < plans.Length; j++)
            {
                Assert.NotEqual(plans[i], plans[j]); // L≠M（烘焙/特效/后期），M≠H（粒度），L≠H（全部）
            }
        }
    }

    // ------------------------------------------------------- effect gate

    [Theory] // legal — 密度门：前 allowed 个放行、其余丢弃（确定性 first-allowed）
    [InlineData(RenderTier.Low, 0, true)]
    [InlineData(RenderTier.Low, 19, true)]
    [InlineData(RenderTier.Low, 20, false)]  // 第 21 个起丢弃（40×0.5=20）
    [InlineData(RenderTier.Low, 39, false)]
    [InlineData(RenderTier.Medium, 39, true)]
    [InlineData(RenderTier.High, 39, true)]
    public void Effect_gate_allows_the_first_allowed_requests_only(
        RenderTier tier, int requestIndex, bool expected)
    {
        RenderFramePlan plan = RendererDecisionMap.PlanFrame(RenderTierMatrix.Capabilities(tier), Requests);

        Assert.Equal(expected, RendererDecisionMap.AllowEffectSpawn(plan, requestIndex));
    }

    [Theory] // boundary — 请求数边界：0 个全放行域空；奇数请求的减半向下取整
    [InlineData(0, 0, 0)]   // 无请求：允许 0、丢弃 0
    [InlineData(1, 0, 1)]   // 1 个请求 × 0.5 → floor=0 允许（半个特效不 spawn）
    [InlineData(2, 1, 1)]
    [InlineData(3, 1, 2)]
    [InlineData(7, 3, 4)]
    public void Odd_request_counts_floor_the_halved_budget(
        int requests, int allowedLow, int droppedLow)
    {
        RenderFramePlan low = RendererDecisionMap.PlanFrame(RenderTierMatrix.Low, requests);
        Assert.Equal(allowedLow, low.EffectSpawnsAllowed);
        Assert.Equal(droppedLow, low.EffectSpawnsDropped);
        Assert.Equal(requests, low.EffectSpawnsAllowed + low.EffectSpawnsDropped);

        RenderFramePlan medium = RendererDecisionMap.PlanFrame(RenderTierMatrix.Medium, requests);
        Assert.Equal(requests, medium.EffectSpawnsAllowed); // M/H 全放行不受取整影响
    }

    [Theory] // illegal — 负请求数拒绝；null 能力行/计划拒绝
    [InlineData(-1)]
    [InlineData(-100)]
    public void PlanFrame_rejects_negative_effect_requests(int requests)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RendererDecisionMap.PlanFrame(RenderTierMatrix.Medium, requests));
    }

    [Fact] // illegal — null 参数拒绝（能力行/计划/层负载）
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => RendererDecisionMap.PlanFrame(null!, 0));
        Assert.Throws<ArgumentNullException>(
            () => RendererDecisionMap.AllowEffectSpawn(null!, 0));
        var plan = RendererDecisionMap.PlanFrame(RenderTierMatrix.Medium, 0);
        Assert.Throws<ArgumentNullException>(
            () => RendererDecisionMap.EstimateFrameWork(null!, new RenderLayerWork(0, 0, false), new RenderLayerWork(0, 0, false)));
        Assert.Throws<ArgumentNullException>(
            () => RendererDecisionMap.EstimateFrameWork(plan, null!, new RenderLayerWork(0, 0, false)));
        Assert.Throws<ArgumentNullException>(
            () => RendererDecisionMap.EstimateFrameWork(plan, new RenderLayerWork(0, 0, false), null!));
    }

    // -------------------------------------------------- draw/decision estimate

    [Fact] // legal — 同一帧负载下三档估算：L 绘制调用估算显著低于 M（减半特效+两 pass 全免）
    public void Estimate_scores_the_same_load_with_fewer_draws_on_low()
    {
        // canonical steady frame: 3 dirty unit chunks rewriting 60 units, ground steady
        // (baked buffer drawn, no rebuild), 40 effect requests; layers populated.
        var ground = new RenderLayerWork(DirtyChunks: 0, InstanceWrites: 0, AnyVisibleInstances: true);
        var units = new RenderLayerWork(DirtyChunks: 3, InstanceWrites: 60, AnyVisibleInstances: true);

        RenderWorkEstimate low = Estimate(RenderTierMatrix.Low, ground, units);
        RenderWorkEstimate medium = Estimate(RenderTierMatrix.Medium, ground, units);
        RenderWorkEstimate high = Estimate(RenderTierMatrix.High, ground, units);

        // 绘制调用估算（模型见 RendererDecisionMap 类注）：
        // L = 1(ground)+1(units)+20(fx)+0+0 = 22；M/H = 1+1+40+1+1 = 44。
        Assert.Equal(22, low.EstimatedDrawCalls);
        Assert.Equal(44, medium.EstimatedDrawCalls);
        Assert.Equal(44, high.EstimatedDrawCalls);
        Assert.True(low.EstimatedDrawCalls * 2 <= medium.EstimatedDrawCalls, "L 应显著低于 M（≤0.5×）");
        Assert.True(low.DecisionCount < medium.DecisionCount); // 丢弃的特效请求也少决策一半
        Assert.Equal(medium.EstimatedDrawCalls, high.EstimatedDrawCalls); // 同式打分：H 差异在粒度不在 pass 数
    }

    [Fact] // legal — 空层不计绘制：无实例层 0 draw；startup（零特效请求）L=0 绘制 0 写入 vs M=烘焙写入
    public void Empty_layers_do_not_submit_draws()
    {
        var empty = new RenderLayerWork(0, 0, false);
        var populated = new RenderLayerWork(1, 1024, true);

        RenderFramePlan lowPlan = RendererDecisionMap.PlanFrame(RenderTierMatrix.Low, 0);
        RenderFramePlan mediumPlan = RendererDecisionMap.PlanFrame(RenderTierMatrix.Medium, 0);

        RenderWorkEstimate lowStartup = RendererDecisionMap.EstimateFrameWork(lowPlan, empty, empty);
        Assert.Equal(0, lowStartup.EstimatedDrawCalls); // 无实例无 pass：一 draw 不交
        Assert.Equal(0, lowStartup.InstanceWrites);     // 烘焙跳过：零实例写入

        RenderWorkEstimate mediumStartup = RendererDecisionMap.EstimateFrameWork(mediumPlan, populated, empty);
        Assert.Equal(3, mediumStartup.EstimatedDrawCalls); // ground 层 + 扫描线 + 后期
        Assert.Equal(1024, mediumStartup.InstanceWrites);  // 全图烘焙写入（L 档为 0）
    }

    [Fact] // legal — 决策数构成：chunk 重建 + 单位实例更新 + 特效判定 + pass 开关
    public void Decision_count_is_the_sum_of_its_parts()
    {
        var ground = new RenderLayerWork(DirtyChunks: 2, InstanceWrites: 2048, true);
        var units = new RenderLayerWork(DirtyChunks: 2, InstanceWrites: 60, true);

        RenderWorkEstimate medium = Estimate(RenderTierMatrix.Medium, ground, units);

        Assert.Equal(2 + 60 + 40 + 1 + 1, medium.DecisionCount);
        Assert.Equal(2048 + 60, medium.InstanceWrites);
    }

    private static RenderWorkEstimate Estimate(
        RenderTierCapabilities caps, RenderLayerWork ground, RenderLayerWork units)
    {
        RenderFramePlan plan = RendererDecisionMap.PlanFrame(caps, Requests);
        return RendererDecisionMap.EstimateFrameWork(plan, ground, units);
    }

    // ------------------------------------------------------ view chunks (L)

    [Fact] // legal — 视野圈选：chunk 内视野只圈 1 个 chunk；跨边界视野按边界切开
    public void View_rect_selects_only_the_chunks_it_touches()
    {
        // 33..51 全在 chunk 1（32..63）之内 ⇒ 单 chunk
        IReadOnlyList<(int ChunkX, int ChunkY)> inside = RendererDecisionMap.ChunksInView(
            128, 128, 32, viewX0Cell: 33, viewY0Cell: 40, viewX1Cell: 52, viewY1Cell: 50);
        var chunk = Assert.Single(inside);
        Assert.Equal((1, 1), chunk);

        // 50..70 跨 chunk 边界 64 ⇒ (1,·)+(2,·)；y 60..72 跨边界 64 ⇒ 2×2
        IReadOnlyList<(int ChunkX, int ChunkY)> spanning = RendererDecisionMap.ChunksInView(
            128, 128, 32, 50, 60, 70, 72);
        Assert.Equal([(1, 1), (2, 1), (1, 2), (2, 2)], spanning);
    }

    [Fact] // legal — 视野跨 chunk 边界时按边界切开（H 的 16 格粒度圈出更小 chunk 集）
    public void View_rect_spanning_chunk_edges_lists_each_touched_chunk()
    {
        IReadOnlyList<(int ChunkX, int ChunkY)> at32 = RendererDecisionMap.ChunksInView(
            128, 128, 32, 30, 30, 34, 34);
        IReadOnlyList<(int ChunkX, int ChunkY)> at16 = RendererDecisionMap.ChunksInView(
            128, 128, 16, 30, 30, 34, 34);

        Assert.Equal([(0, 0), (1, 0), (0, 1), (1, 1)], at32);
        Assert.Equal([(1, 1), (2, 1), (1, 2), (2, 2)], at16); // 更细粒度：更小的重写单元
    }

    [Fact] // boundary — 视野越界/反向钳到地图边：边 chunk 兜底，永不空集
    public void Out_of_map_view_rects_clamp_to_edge_chunks()
    {
        IReadOnlyList<(int ChunkX, int ChunkY)> left = RendererDecisionMap.ChunksInView(
            64, 64, 32, -100, -100, -50, -50);
        IReadOnlyList<(int ChunkX, int ChunkY)> right = RendererDecisionMap.ChunksInView(
            64, 64, 32, 500, 500, 600, 600);
        IReadOnlyList<(int ChunkX, int ChunkY)> inverted = RendererDecisionMap.ChunksInView(
            64, 64, 32, 60, 60, 10, 10);

        Assert.Equal([(0, 0)], left);
        Assert.Equal([(1, 1)], right);
        Assert.Equal([(0, 0), (1, 0), (0, 1), (1, 1)], inverted); // 反向矩形按 min/max 归一
    }

    [Theory] // illegal — 非正地图几何/块边拒绝
    [InlineData(0, 64, 32)]
    [InlineData(64, 0, 32)]
    [InlineData(64, 64, 0)]
    [InlineData(-1, 64, 32)]
    [InlineData(64, 64, -8)]
    public void ChunksInView_rejects_invalid_geometry(int width, int height, int chunkCells)
    {
        Assert.Throws<ArgumentException>(
            () => RendererDecisionMap.ChunksInView(width, height, chunkCells, 0, 0, 1, 1));
    }
}
