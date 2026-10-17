namespace EraCore.Endless;

/// <summary>
/// The cross-era mixing schedule of one endless run (T-143 混编敌人: 高波次
/// 跨时代混编敌池——权重参数驱动): waves below <see cref="MixFromWave"/> draw
/// from the STARTING ERA ONLY (敌池基准=开局时代，T-143 任选时代的池面),
/// waves from <see cref="MixFromWave"/> on mix every era into each role
/// sub-pool with the two weight knobs (开局时代行 ×<see cref="StartEraWeight"/>,
/// 其他时代行 ×<see cref="OtherEraWeight"/> — 每线内归一，混编按权重). Pure
/// validated data, host-supplied (RL7); <see cref="Default"/> is the T-143
/// production schedule and aligns the mixing boundary with the SP-7a
/// extrapolation segment (&gt;40 = 无尽深段).
/// </summary>
public sealed record EndlessMixPolicy
{
    /// <summary>
    /// Validates and freezes the schedule: a positive mixing wave and
    /// strictly positive weights.
    /// </summary>
    /// <param name="mixFromWave">First wave that mixes cross-era (≥ 1; waves below draw start-era only).</param>
    /// <param name="startEraWeight">Weight multiplier of the starting era's entries while mixing (&gt; 0).</param>
    /// <param name="otherEraWeight">Weight multiplier of every other era's entries while mixing (&gt; 0).</param>
    /// <exception cref="ArgumentException">
    /// mixFromWave &lt; 1, or a weight that is not strictly positive.
    /// </exception>
    public EndlessMixPolicy(int mixFromWave, double startEraWeight, double otherEraWeight)
    {
        if (mixFromWave < 1)
        {
            throw new ArgumentException($"MixFromWave must be ≥ 1 (got {mixFromWave}).", nameof(mixFromWave));
        }

        if (!(startEraWeight > 0))
        {
            throw new ArgumentException($"StartEraWeight must be strictly positive (got {startEraWeight}).", nameof(startEraWeight));
        }

        if (!(otherEraWeight > 0))
        {
            throw new ArgumentException($"OtherEraWeight must be strictly positive (got {otherEraWeight}).", nameof(otherEraWeight));
        }

        MixFromWave = mixFromWave;
        StartEraWeight = startEraWeight;
        OtherEraWeight = otherEraWeight;
    }

    /// <summary>First wave that mixes cross-era (waves below draw start-era only).</summary>
    public int MixFromWave { get; }

    /// <summary>Weight multiplier of the starting era's entries while mixing.</summary>
    public double StartEraWeight { get; }

    /// <summary>Weight multiplier of every other era's entries while mixing.</summary>
    public double OtherEraWeight { get; }

    /// <summary>
    /// T-143 production schedule: mix from wave 41 (对齐 SP-7a「越过 min(40)
    /// 封顶的外推」深段边界——41 起才是无尽深段，SP-7b 词缀同界), era-uniform
    /// weights (SP-7 只钉「跨时代敌人池按权重」不钉权重值；1:1 = 时代均匀，
    /// 与 <see cref="EndlessEraPools"/> 权重一律 1.0 先例同款).
    /// </summary>
    public static EndlessMixPolicy Default { get; } = new(
        mixFromWave: 41, // ci-allow: R7 T-143 混编起始波（对齐 SP-7a >40 深段边界，节奏锚非平衡数值）
        startEraWeight: 1.0,
        otherEraWeight: 1.0);
}
