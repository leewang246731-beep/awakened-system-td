namespace EraCore.Endless;

/// <summary>
/// The cross-era mixing schedule of one endless run (T-143 混编敌人: 高波次
/// 跨时代混编敌池——权重参数驱动; T-154 波渐升): waves below
/// <see cref="MixFromWave"/> draw from the STARTING ERA ONLY (敌池基准=开局
/// 时代，T-143 任选时代的池面), waves from <see cref="MixFromWave"/> on mix
/// every era into each role sub-pool with the two weight knobs (开局时代行
/// ×start-era weight, 其他时代行 ×other-era weight — 每线内归一，混编按权重).
/// T-154 业主裁定「主场主导+波渐升」: the knobs may LINERARLY RAMP with the
/// wave — from the at-mix weights at <see cref="MixFromWave"/> to the end
/// weights at <see cref="RampEndWave"/> on (query
/// <see cref="WeightsAt(int)"/>; production values live in
/// <c>game/content/endless/endless-policies.json</c>, RL7). Pure validated
/// data, host-supplied; <see cref="Default"/> is the no-table fallback tier
/// (era-uniform; the T-154 production ramp ships in the JSON table — same
/// discipline as the BGM program table, audio/bgm-programs.json 先例).
/// </summary>
public sealed record EndlessMixPolicy
{
    /// <summary>
    /// Validates and freezes the schedule: a positive mixing wave, strictly
    /// positive weights, ramp parameters all-or-nothing, and a ramp end at
    /// or past the mixing wave.
    /// </summary>
    /// <param name="mixFromWave">First wave that mixes cross-era (≥ 1; waves below draw start-era only).</param>
    /// <param name="startEraWeight">At-mix weight multiplier of the starting era's entries (&gt; 0).</param>
    /// <param name="otherEraWeight">At-mix weight multiplier of every other era's entries (&gt; 0).</param>
    /// <param name="rampEndWave">T-154 渐升终值波 (≥ <paramref name="mixFromWave"/>; from this wave on the end weights hold; null = no ramp).</param>
    /// <param name="endStartEraWeight">Ramped end weight of the starting era (&gt; 0; null iff <paramref name="rampEndWave"/> is null).</param>
    /// <param name="endOtherEraWeight">Ramped end weight of the other eras (&gt; 0; null iff <paramref name="rampEndWave"/> is null).</param>
    /// <exception cref="ArgumentException">
    /// mixFromWave &lt; 1, a weight that is not strictly positive, partial
    /// ramp parameters, rampEndWave &lt; mixFromWave, or a ramped end weight
    /// that is not strictly positive.
    /// </exception>
    public EndlessMixPolicy(
        int mixFromWave,
        double startEraWeight,
        double otherEraWeight,
        int? rampEndWave = null,
        double? endStartEraWeight = null,
        double? endOtherEraWeight = null)
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

        bool hasRamp = rampEndWave is not null;
        if (hasRamp != (endStartEraWeight is not null) || hasRamp != (endOtherEraWeight is not null))
        {
            throw new ArgumentException(
                "Ramp parameters are all-or-nothing: rampEndWave/endStartEraWeight/endOtherEraWeight must be supplied together " +
                "(partial ramp = an ambiguous schedule).",
                nameof(rampEndWave));
        }

        if (rampEndWave is int endWave && endWave < mixFromWave)
        {
            throw new ArgumentException(
                $"RampEndWave must be ≥ MixFromWave (got {endWave} < {mixFromWave}).",
                nameof(rampEndWave));
        }

        if (endStartEraWeight is double endStart && !(endStart > 0))
        {
            throw new ArgumentException(
                $"EndStartEraWeight must be strictly positive (got {endStart}).", nameof(endStartEraWeight));
        }

        if (endOtherEraWeight is double endOther && !(endOther > 0))
        {
            throw new ArgumentException(
                $"EndOtherEraWeight must be strictly positive (got {endOther}).", nameof(endOtherEraWeight));
        }

        MixFromWave = mixFromWave;
        StartEraWeight = startEraWeight;
        OtherEraWeight = otherEraWeight;
        RampEndWave = rampEndWave;
        EndStartEraWeight = endStartEraWeight;
        EndOtherEraWeight = endOtherEraWeight;
    }

    /// <summary>First wave that mixes cross-era (waves below draw start-era only).</summary>
    public int MixFromWave { get; }

    /// <summary>At-mix weight multiplier of the starting era's entries (ramp start).</summary>
    public double StartEraWeight { get; }

    /// <summary>At-mix weight multiplier of every other era's entries (ramp start).</summary>
    public double OtherEraWeight { get; }

    /// <summary>
    /// T-154 渐升终值波: from this wave on the end weights hold (null = the
    /// at-mix weights are constant — the fixed-knob schedule).
    /// </summary>
    public int? RampEndWave { get; }

    /// <summary>Ramped end weight of the starting era (null on a fixed schedule).</summary>
    public double? EndStartEraWeight { get; }

    /// <summary>Ramped end weight of the other eras (null on a fixed schedule).</summary>
    public double? EndOtherEraWeight { get; }

    /// <summary>
    /// The wave-resolved weight knobs (T-154 波渐升查询面): a fixed schedule
    /// returns its constants; a ramped schedule linearly interpolates the
    /// weights between <see cref="MixFromWave"/> (at-mix values) and
    /// <see cref="RampEndWave"/> (end values), holding the end values from
    /// the ramp end on. Waves below <see cref="MixFromWave"/> return the
    /// at-mix values (the baseline segment draws start-era-only and never
    /// consumes the knobs — the figure is nominal there).
    /// </summary>
    /// <param name="wave">Wave being planned (any int; only the mixing segment is consumed).</param>
    public (double StartEraWeight, double OtherEraWeight) WeightsAt(int wave)
    {
        if (RampEndWave is not int endWave)
        {
            return (StartEraWeight, OtherEraWeight);
        }

        double endStart = EndStartEraWeight ?? StartEraWeight;
        double endOther = EndOtherEraWeight ?? OtherEraWeight;
        if (wave >= endWave)
        {
            return (endStart, endOther);
        }

        if (wave <= MixFromWave)
        {
            return (StartEraWeight, OtherEraWeight);
        }

        double t = (double)(wave - MixFromWave) / (endWave - MixFromWave);
        return (StartEraWeight + ((endStart - StartEraWeight) * t),
                OtherEraWeight + ((endOther - OtherEraWeight) * t));
    }

    /// <summary>
    /// No-table fallback schedule: mix from wave 41 (对齐 SP-7a「越过 min(40)
    /// 封顶的外推」深段边界——41 起才是无尽深段，SP-7b 词缀同界), era-uniform
    /// weights. T-154 起生产档=「主场主导+波渐升」（41 波 9:1 → 81 波起 3:1
    /// 线性渐升），数值冻结在 <c>game/content/endless/endless-policies.json</c>
    /// （RL7：数值活注册表——audio/bgm-programs.json 同构先例）；本静态档保留为
    /// 未递交策略表时的兜底（宿主装载 JSON 表传入 EndlessEntryRequest.PolicyTable
    /// 即得生产档）。
    /// </summary>
    public static EndlessMixPolicy Default { get; } = new(
        mixFromWave: 41, // ci-allow: R7 T-143 混编起始波（对齐 SP-7a >40 深段边界，节奏锚非平衡数值；T-154 起为无表兜底档，生产渐升档在 endless-policies.json）
        startEraWeight: 1.0,
        otherEraWeight: 1.0);
}
