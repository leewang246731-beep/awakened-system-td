namespace EraCore.Presentation;

/// <summary>
/// One frame's render decisions under one tier (T-102): the pure decision
/// table the Godot renderers mirror (OffModeDowngrade 先例 — 决策全在纯逻辑
/// 层, headless 可全测). Everything derives from the frozen
/// <see cref="RenderTierCapabilities"/> matrix row; the presentation layer
/// never branches on the tier enum itself.
/// </summary>
/// <param name="Tier">Tier the plan was computed for.</param>
/// <param name="BakeStaticGround">Run the up-front full-map static chunk bake (L 档跳过, 改按需直绘).</param>
/// <param name="ChunkCells">Dirty-mark chunk edge in cells for BOTH layers (granularity from the matrix).</param>
/// <param name="EffectSpawnRequests">Effect spawn requests observed this frame (input echo).</param>
/// <param name="EffectSpawnsAllowed">Requests allowed through the density gate (L 减半).</param>
/// <param name="EffectSpawnsDropped">Requests dropped by the gate (requests − allowed).</param>
/// <param name="ScanlinePass">Scanline overlay pass renders this frame.</param>
/// <param name="PostProcessPass">Post-processing pass renders this frame.</param>
/// <param name="TargetFrameBudgetMs">Frame budget the tier is tuned to hold (telemetry headroom reference).</param>
public sealed record RenderFramePlan(
    RenderTier Tier,
    bool BakeStaticGround,
    int ChunkCells,
    int EffectSpawnRequests,
    int EffectSpawnsAllowed,
    int EffectSpawnsDropped,
    bool ScanlinePass,
    bool PostProcessPass,
    double TargetFrameBudgetMs);

/// <summary>
/// Observed per-layer workload for one frame (inputs to the estimator; the
/// Godot frame loop counts these from the REAL bridge drains).
/// </summary>
/// <param name="DirtyChunks">Chunks drained dirty for this layer this frame.</param>
/// <param name="InstanceWrites">Instance slots rewritten this frame (ground: cells in rebuilt chunks; units: units in rebuilt chunks).</param>
/// <param name="AnyVisibleInstances">Layer has any populated instance slots (drawn even with zero rebuilds).</param>
public sealed record RenderLayerWork(int DirtyChunks, int InstanceWrites, bool AnyVisibleInstances);

/// <summary>
/// Headless frame-budget estimate for one frame (T-102 acceptance: 集显
/// 稳定帧率的 headless 替代口径 — no GPU exists in CI, so the tier deltas
/// are proven on this decision/draw-call ESTIMATE over identical real load
/// traces; 真·集显帧率实测归 T-106 Web 实测口径, 见任务卡注记).
/// </summary>
/// <param name="DecisionCount">Renderer decisions made this frame: chunk rebuilds + unit instance updates + effect allow/drop verdicts + overlay pass toggles.</param>
/// <param name="EstimatedDrawCalls">Draw-submission estimate (model below): 1 per populated MultiMesh layer + 1 per allowed effect + 1 per overlay pass.</param>
/// <param name="InstanceWrites">Instance slots rewritten this frame (both layers summed).</param>
public sealed record RenderWorkEstimate(int DecisionCount, int EstimatedDrawCalls, int InstanceWrites);

/// <summary>
/// Pure renderer decision table for the C-21 three-tier ladder (T-102,
/// F-159). The Godot side consumes exactly these outputs — MultiMesh
/// rebuild paths, the effect spawn gate and the overlay toggles — so every
/// tier difference is assertable headless (三档差异可测).
/// <para>
/// Draw-call model (estimation, documented): one MultiMesh layer submits a
/// single draw regardless of chunk count; each gated-through effect spawns
/// one sprite draw (worst-case unbatched); scanline and post-processing are
/// one full-screen pass each. The model deliberately favours none of the
/// tiers — all three are scored by the same formula.
/// </para>
/// </summary>
public static class RendererDecisionMap
{
    /// <summary>
    /// Computes the frame plan for one tier from the frozen capability row
    /// and this frame's observed effect spawn requests.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="effectSpawnRequests"/> is negative.</exception>
    public static RenderFramePlan PlanFrame(RenderTierCapabilities capabilities, int effectSpawnRequests)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (effectSpawnRequests < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectSpawnRequests), effectSpawnRequests, "effect spawn requests cannot be negative.");
        }

        // Density gate: floor keeps L's "减半" exact for even counts and
        // rounds down for odd counts (a half spawn is no spawn).
        int allowed = (int)Math.Floor(effectSpawnRequests * capabilities.EffectDensity);
        allowed = Math.Clamp(allowed, 0, effectSpawnRequests);
        return new RenderFramePlan(
            capabilities.Tier,
            BakeStaticGround: capabilities.StaticChunkBake,
            ChunkCells: capabilities.DirtyChunkCells,
            EffectSpawnRequests: effectSpawnRequests,
            EffectSpawnsAllowed: allowed,
            EffectSpawnsDropped: effectSpawnRequests - allowed,
            ScanlinePass: capabilities.ScanlineOverlay,
            PostProcessPass: capabilities.PostProcessing,
            TargetFrameBudgetMs: capabilities.TargetFrameBudgetMs);
    }

    /// <summary>
    /// Per-request effect gate verdict: the first
    /// <see cref="RenderFramePlan.EffectSpawnsAllowed"/> requests of the
    /// frame spawn, the rest drop (deterministic first-allowed policy —
    /// snapshot unit order is fixed per tick, so the same load always gates
    /// the same requests).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    public static bool AllowEffectSpawn(RenderFramePlan plan, int requestIndex)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return requestIndex >= 0 && requestIndex < plan.EffectSpawnsAllowed;
    }

    /// <summary>
    /// Frame-budget estimate for one frame under one plan: decision count,
    /// draw-call estimate and instance writes from the observed layer work.
    /// All tiers are scored by the identical formula (see class doc).
    /// </summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static RenderWorkEstimate EstimateFrameWork(
        RenderFramePlan plan, RenderLayerWork ground, RenderLayerWork units)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(units);

        int decisions =
            ground.DirtyChunks          // ground chunk rebuild decisions
            + units.InstanceWrites      // per-unit transform+color update decisions
            + plan.EffectSpawnRequests  // every request gets an allow/drop verdict
            + (plan.ScanlinePass ? 1 : 0)
            + (plan.PostProcessPass ? 1 : 0);

        int draws =
            (ground.AnyVisibleInstances ? 1 : 0)
            + (units.AnyVisibleInstances ? 1 : 0)
            + plan.EffectSpawnsAllowed
            + (plan.ScanlinePass ? 1 : 0)
            + (plan.PostProcessPass ? 1 : 0);

        return new RenderWorkEstimate(decisions, draws, ground.InstanceWrites + units.InstanceWrites);
    }

    /// <summary>
    /// Chunks intersecting a view cell-rect (L 档按需直绘: only the chunks
    /// the camera sees are ever drawn; the view rect clamps to the map so a
    /// camera outside the world yields the edge chunks, never empty).
    /// </summary>
    /// <param name="mapWidthCells">Map width in cells.</param>
    /// <param name="mapHeightCells">Map height in cells.</param>
    /// <param name="chunkCells">Chunk edge in cells.</param>
    /// <param name="viewX0Cell">View left edge in cells (floor).</param>
    /// <param name="viewY0Cell">View top edge in cells (floor).</param>
    /// <param name="viewX1Cell">View right edge in cells, exclusive (ceil).</param>
    /// <param name="viewY1Cell">View bottom edge in cells, exclusive (ceil).</param>
    public static IReadOnlyList<(int ChunkX, int ChunkY)> ChunksInView(
        int mapWidthCells, int mapHeightCells, int chunkCells,
        int viewX0Cell, int viewY0Cell, int viewX1Cell, int viewY1Cell)
    {
        if (mapWidthCells <= 0 || mapHeightCells <= 0 || chunkCells <= 0)
        {
            throw new ArgumentException(
                $"map geometry must be positive (got {mapWidthCells}x{mapHeightCells} chunk {chunkCells}).");
        }

        int x0 = Math.Clamp(Math.Min(viewX0Cell, viewX1Cell), 0, mapWidthCells - 1);
        int y0 = Math.Clamp(Math.Min(viewY0Cell, viewY1Cell), 0, mapHeightCells - 1);
        int x1 = Math.Clamp(Math.Max(viewX0Cell, viewX1Cell), 0, mapWidthCells);
        int y1 = Math.Clamp(Math.Max(viewY0Cell, viewY1Cell), 0, mapHeightCells);

        var chunks = new List<(int ChunkX, int ChunkY)>();
        for (int chunkY = y0 / chunkCells; chunkY <= (y1 - 1) / chunkCells; chunkY++)
        {
            for (int chunkX = x0 / chunkCells; chunkX <= (x1 - 1) / chunkCells; chunkX++)
            {
                chunks.Add((chunkX, chunkY));
            }
        }

        return chunks;
    }
}
