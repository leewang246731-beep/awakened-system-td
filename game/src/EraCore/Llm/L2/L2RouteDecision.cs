namespace EraCore.Llm.L2;

/// <summary>
/// The three advisor tiers the player can route orders through (T-137,
/// F-078/C-27). This is the ROUTING domain enum — the disclosure-side
/// three-tier vocabulary stays <see cref="EraCore.Presentation.DataFlowTier"/>
/// (文案域); the two map 1:1 only at the settings page seam (single switch,
/// 禁在两处重复措辞指文案，非枚举).
/// </summary>
public enum LlmTierKind
{
    /// <summary>L1 军令语法 — offline grammar parsing (<c>L1IntentParser</c>), always available.</summary>
    L1Grammar = 0,

    /// <summary>L2 本地小模型 — llama.cpp server on 127.0.0.1, model via opt-in download.</summary>
    L2Local = 1,

    /// <summary>L3 BYOK 云端 — the configured provider row through <c>LlmGateway</c>.</summary>
    L3Cloud = 2,
}

/// <summary>Live phase of the L2 runtime lane (T-137) — protocol+state live in EraCore, the PROCESS lives in the presentation host.</summary>
public enum L2RuntimePhase
{
    /// <summary>Model GGUF not present (not downloaded / removed) — L2 unusable, degrade L1.</summary>
    ModelMissing,

    /// <summary>llama-server executable not present (neither bundled nor downloaded) — degrade L1.</summary>
    RuntimeMissing,

    /// <summary>Model (or runtime) download in progress — degrade L1 until done.</summary>
    Downloading,

    /// <summary>Both artifacts present but the server is not running (never started / stopped) — degrade L1 until started.</summary>
    Idle,

    /// <summary>Process spawn issued, health probe not yet green — degrade L1 while starting.</summary>
    Starting,

    /// <summary>Server up, health probe green, endpoint answering — L2 usable.</summary>
    Ready,

    /// <summary>Process died or health probe failed after Ready — degrade L1 (the acceptance-critical failure path).</summary>
    ProcessDead,
}

/// <summary>Point-in-time snapshot of the L2 lane the route decider consumes.</summary>
/// <param name="Phase">Current phase.</param>
/// <param name="ModelPresent">Model file present at the expected path (or an override path).</param>
/// <param name="RuntimePresent">llama-server executable found (bundled / downloaded / override).</param>
/// <param name="Port">Local port the lane is bound to.</param>
/// <param name="Detail">Diagnostic detail (phase-specific; never contains key material — the L2 lane has none).</param>
public sealed record L2RuntimeSnapshot(
    L2RuntimePhase Phase,
    bool ModelPresent,
    bool RuntimePresent,
    int Port = L2Facts.DefaultPort,
    string? Detail = null)
{
    /// <summary>Factory snapshot for「nothing installed」.</summary>
    public static L2RuntimeSnapshot NothingInstalled(int port = L2Facts.DefaultPort) =>
        new(L2RuntimePhase.ModelMissing, ModelPresent: false, RuntimePresent: false, port);

    /// <summary>True only in the phase where routing an L2 fill is legal.</summary>
    public bool CanServe => Phase == L2RuntimePhase.Ready;
}

/// <summary>Why an order was (or would be) downgraded from L2 to L1 (T-137 降级矩阵判据).</summary>
public enum L2DegradationReason
{
    /// <summary>No downgrade (route answered the selected tier, or L1 was selected).</summary>
    None,

    /// <summary>Platform row disables L2 (Web sandbox) — degrade L1.</summary>
    WebTier,

    /// <summary>Model file missing (not downloaded / deleted) — degrade L1.</summary>
    ModelMissing,

    /// <summary>llama-server executable missing — degrade L1.</summary>
    RuntimeMissing,

    /// <summary>Download or process start still in progress — degrade L1 meanwhile.</summary>
    NotReady,

    /// <summary>Server process died after being ready — degrade L1.</summary>
    ProcessDead,

    /// <summary>Wave-gap fill exceeded its budget — this order falls back to L1 (放弃本轮不卡波).</summary>
    FillTimeout,

    /// <summary>L2 lane answered but produced no usable tool call — this order falls back to L1.</summary>
    FillFailed,
}

/// <summary>
/// One routing verdict (T-137 三档路由): which tier effectively handles the
/// order, whether that involved a downgrade, and why. Pure value — produced
/// by <see cref="L2RouteDecider"/>, consumed by the presenter for both the
/// banner copy and the call log.
/// </summary>
/// <param name="EffectiveTier">The tier that will actually handle the order.</param>
/// <param name="Downgraded">True when <paramref name="EffectiveTier"/> differs from the selected tier (L2→L1 fallback).</param>
/// <param name="Reason">Degradation reason (None when no downgrade).</param>
public sealed record L2RouteDecision(
    LlmTierKind EffectiveTier,
    bool Downgraded,
    L2DegradationReason Reason)
{
    /// <summary>The no-downgrade verdict for one selected tier.</summary>
    public static L2RouteDecision Direct(LlmTierKind tier) => new(tier, Downgraded: false, L2DegradationReason.None);

    /// <summary>The downgrade verdict — always down to L1 (任务卡验收词：失败降 L1).</summary>
    public static L2RouteDecision DegradeToL1(L2DegradationReason reason) => new(LlmTierKind.L1Grammar, Downgraded: true, reason);
}
