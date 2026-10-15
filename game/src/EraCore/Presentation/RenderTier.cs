using System.Text.Json;
using EraCore.IO;

namespace EraCore.Presentation;

/// <summary>
/// Render quality tier (T-102, C-21 渲染三档 F-159; 方案附录 G.1 条 8 —
/// 静态 chunk/低频批量/高密度整 buffer 的档位化收口). The three tiers are
/// the Web/低端降级 ladder: L turns the static chunk bake off (on-demand
/// direct draw), halves effect spawns and drops all overlays; M is the
/// current default skeleton behaviour; H keeps everything on and tightens
/// the dirty-chunk granularity. All per-tier facts live in the frozen
/// <see cref="RenderTierMatrix"/> capability table — renderers read the
/// matrix, they never branch on the enum (能力矩阵驱动非 if 蔓延).
/// </summary>
public enum RenderTier
{
    /// <summary>低：关静态烘焙改按需直绘 + 特效减半 + 无后处理（Web/集显稳定档）.</summary>
    Low,

    /// <summary>中：现默认（静态 chunk 烘焙 32×32 + 全特效 + 后处理）.</summary>
    Medium,

    /// <summary>高：全开 + 更细脏粒度（16×16 chunk）.</summary>
    High,
}

/// <summary>
/// Player-facing preference (设置页档位行): the three explicit tiers plus
/// <see cref="Auto"/> — probe-driven selection (Web feature 探测 + 首次
/// 60 帧测帧率自适应). Persisted via <see cref="RenderTierStore"/>.
/// </summary>
public enum RenderTierPreference
{
    /// <summary>自动：环境与帧率探测选档（默认出厂位）.</summary>
    Auto,

    /// <summary>手动固定低档.</summary>
    Low,

    /// <summary>手动固定中档.</summary>
    Medium,

    /// <summary>手动固定高档.</summary>
    High,
}

/// <summary>
/// One row of the frozen render-tier capability matrix (T-102): the six
/// data-driven facts every renderer decision derives from. Technical
/// rendering config, not game balance data — the values are frozen engine
/// constants here (same registry status as
/// <see cref="DirtyRegionSet.DefaultChunkCells"/>), with derivations on
/// each table row in <see cref="RenderTierMatrix"/>.
/// </summary>
/// <param name="Tier">The tier this row describes.</param>
/// <param name="StaticChunkBake">Ground static chunk bake (G.1 条 8 静态 chunk): false ⇒ skip the up-front full-map bake and rebuild only on-demand chunks (L 按需直绘).</param>
/// <param name="DirtyChunkCells">Dirty-mark chunk edge in cells: 32 = the G.1 条 8 static chunk size; 16 = the H finer granularity (更细脏粒度).</param>
/// <param name="EffectDensity">Fraction of effect spawn requests allowed per frame: 0.5 = L 特效减半, 1.0 = full.</param>
/// <param name="ScanlineOverlay">Scanline overlay pass (HoloSkin §13.4) on/off.</param>
/// <param name="PostProcessing">Post-processing pass on/off (L 无后处理).</param>
/// <param name="TargetFrameBudgetMs">Frame budget this tier is tuned to hold: 33.4 ms = stable 30 fps on 集显/Web; 16.7 ms = 60 fps.</param>
public sealed record RenderTierCapabilities(
    RenderTier Tier,
    bool StaticChunkBake,
    int DirtyChunkCells,
    double EffectDensity,
    bool ScanlineOverlay,
    bool PostProcessing,
    double TargetFrameBudgetMs);

/// <summary>
/// The frozen three-tier capability matrix (T-102 acceptance: 三档差异可测).
/// Single source the Godot renderers consume; the decision derivations live
/// in <see cref="RendererDecisionMap"/>.
/// </summary>
public static class RenderTierMatrix
{
    /// <summary>L 档：关静态烘焙（按需直绘）+ 32 chunk + 特效 0.5 + 无扫描线/后处理 + 33.4 ms（30 fps 集显稳定目标）预算.</summary>
    public static RenderTierCapabilities Low { get; } = new(
        RenderTier.Low,
        StaticChunkBake: false,
        DirtyChunkCells: 32,
        EffectDensity: 0.5d,
        ScanlineOverlay: false,
        PostProcessing: false,
        TargetFrameBudgetMs: 33.4d);

    /// <summary>M 档：现默认 —— 静态 chunk 烘焙（G.1 条 8 的 32×32）+ 全特效 + 扫描线/后处理 + 16.7 ms（60 fps）预算.</summary>
    public static RenderTierCapabilities Medium { get; } = new(
        RenderTier.Medium,
        StaticChunkBake: true,
        DirtyChunkCells: 32,
        EffectDensity: 1.0d,
        ScanlineOverlay: true,
        PostProcessing: true,
        TargetFrameBudgetMs: 16.7d);

    /// <summary>H 档：全开 + 16×16 更细脏粒度（脏 chunk 内重写的格数降为 1/4）+ 16.7 ms（60 fps）预算.</summary>
    public static RenderTierCapabilities High { get; } = new(
        RenderTier.High,
        StaticChunkBake: true,
        DirtyChunkCells: 16,
        EffectDensity: 1.0d,
        ScanlineOverlay: true,
        PostProcessing: true,
        TargetFrameBudgetMs: 16.7d);

    /// <summary>All tiers in ladder order (L→M→H); the decision-map tests walk this list.</summary>
    public static IReadOnlyList<RenderTier> AllTiers { get; } = [RenderTier.Low, RenderTier.Medium, RenderTier.High];

    /// <summary>The frozen table lookup.</summary>
    public static RenderTierCapabilities Capabilities(RenderTier tier) => tier switch
    {
        RenderTier.Low => Low,
        RenderTier.Medium => Medium,
        RenderTier.High => High,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "unknown render tier."),
    };

    /// <summary>Single-letter tier id for smoke/log lines (L/M/H).</summary>
    public static string TierId(RenderTier tier) => tier switch
    {
        RenderTier.Low => "L",
        RenderTier.Medium => "M",
        RenderTier.High => "H",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "unknown render tier."),
    };
}

/// <summary>
/// Environment + timing facts the <see cref="Auto"/> preference probes
/// (T-102: Godot Web feature 探测 + 首次 60 帧测帧率自适应). The Godot side
/// supplies the facts (OS.HasFeature("web"), the average of
/// Engine.GetFramesPerSecond() over the first 60 real frames); selection
/// itself stays pure so it is fully testable headless.
/// </summary>
/// <param name="IsWeb">Running inside the HTML5 export (R-E2: 单线程/弱 GPU ⇒ Web 自动落 L).</param>
/// <param name="FirstMinuteAverageFps">Mean FPS over the first 60 frames; ≤ 0 means unknown (headless single-iteration runs have no frame pacing data).</param>
public sealed record RenderProbeFacts(bool IsWeb, double FirstMinuteAverageFps);

/// <summary>
/// Tier selection (pure): a manual preference always wins; <see cref="RenderTierPreference.Auto"/>
/// resolves from the probe facts. Threshold derivation: the design target is
/// 60 fps — a sustained average below 45 (75% of target) cannot hold 60, so
/// drop to L for headroom; ≥ 55 (92%) is comfortable for the H ladder; in
/// between stays M. No data (headless, probe incomplete) keeps the current
/// default M — an unreadable probe must never silently downgrade.
/// </summary>
public static class RenderTierSelector
{
    /// <summary>Average FPS below this auto-selects L (cannot hold the 60 fps design target).</summary>
    public const double LowBelowFps = 45d;

    /// <summary>Average FPS at or above this auto-selects H (comfortable headroom for 全开).</summary>
    public const double HighAtOrAboveFps = 55d;

    /// <summary>Resolves the effective tier for one preference + probe facts.</summary>
    public static RenderTier Select(RenderTierPreference preference, RenderProbeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return preference switch
        {
            RenderTierPreference.Low => RenderTier.Low,
            RenderTierPreference.Medium => RenderTier.Medium,
            RenderTierPreference.High => RenderTier.High,
            RenderTierPreference.Auto => AutoSelect(facts),
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "unknown render tier preference."),
        };
    }

    private static RenderTier AutoSelect(RenderProbeFacts facts) =>
        facts.IsWeb
            ? RenderTier.Low // R-E2: Web 导出=单线程弱 GPU 目标，自动落 L（手动覆盖仍可改）
            : facts.FirstMinuteAverageFps <= 0
                ? RenderTier.Medium // 无帧率数据（headless/探测未完成）：保持现默认
                : facts.FirstMinuteAverageFps < LowBelowFps
                    ? RenderTier.Low
                    : facts.FirstMinuteAverageFps >= HighAtOrAboveFps
                        ? RenderTier.High
                        : RenderTier.Medium;
}

/// <summary>
/// Persisted render-tier preference (T-102 设置页档位行): only the chosen
/// preference is durable — the resolved tier is re-probed every launch.
/// </summary>
public sealed record RenderTierSettings
{
    /// <summary>Preferred selection; factory default is <see cref="RenderTierPreference.Auto"/>.</summary>
    public required RenderTierPreference Preference { get; init; }

    /// <summary>Factory defaults: auto-probe on first launch.</summary>
    public static RenderTierSettings Default { get; } = new() { Preference = RenderTierPreference.Auto };
}

/// <summary>
/// D-03 store binding of the render-tier preference (PanelSettingsStore
/// precedent): one JSON file under the injected <see cref="IFileStore"/>
/// root; a missing/corrupt file falls back to
/// <see cref="RenderTierSettings.Default"/> — a broken preference must
/// never block startup.
/// </summary>
public static class RenderTierStore
{
    /// <summary>Settings file path relative to the injected data root.</summary>
    public const string RelativePath = "settings/render-tier.json";

    /// <summary>
    /// Loads the preference; missing file, unparsable file, or an unknown
    /// preference token all yield <see cref="RenderTierSettings.Default"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public static RenderTierSettings Load(IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Exists(RelativePath))
        {
            return RenderTierSettings.Default;
        }

        try
        {
            byte[] bytes = store.ReadAllBytes(RelativePath);
            RenderTierSettings? parsed = JsonSerializer.Deserialize(
                bytes, PresentationJsonContext.Default.RenderTierSettings);
            if (parsed is null)
            {
                return RenderTierSettings.Default;
            }

            // Numeric payloads decode without throwing even out of range
            // (string-enum converter still allows integers) — an undefined
            // preference is the same class of corruption as bad JSON.
            return Enum.IsDefined(typeof(RenderTierPreference), parsed.Preference)
                ? parsed
                : RenderTierSettings.Default;
        }
        catch (JsonException)
        {
            return RenderTierSettings.Default; // corrupt preference file — factory default, never a startup failure
        }
    }

    /// <summary>Round-trips the preference through the source-generated codec.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="settings"/> is null.</exception>
    public static void Save(IFileStore store, RenderTierSettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            settings, PresentationJsonContext.Default.RenderTierSettings);
        store.WriteAllBytes(RelativePath, bytes);
    }
}
