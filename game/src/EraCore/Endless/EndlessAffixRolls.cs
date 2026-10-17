namespace EraCore.Endless;

/// <summary>
/// The every-10-wave ROLLING affix schedule of one endless run (T-143 每 10
/// 波词缀: 现有四词缀基础上滚动词缀——重复规则/叠加规则参数化): from
/// <see cref="RollStartWave"/> on, each consecutive <see cref="RollEveryWaves"/>
/// wave block draws <see cref="KindsPerRoll"/> affix KINDS out of the frozen
/// four (<see cref="EndlessAffixKind"/> — the KINDS and magnitudes stay the
/// SP-7b frozen set; only the activation schedule rolls). The roll is a pure
/// DETERMINISTIC derivation from the run seed (hash-indexed draw, no rng
/// contact — same seed ⇒ same sequence at every wave), so the roster rng
/// stream of <see cref="EndlessRules.PlanWave"/> is untouched by rolling.
/// Repeat rule: <see cref="AllowConsecutiveRepeat"/> decides whether a kind
/// rolled in block b may be rolled again in block b+1. Stacking rule:
/// <see cref="RepeatIntensityGain"/> scales a kind's intensity once per
/// CONSECUTIVE re-roll (run length r ⇒ ×gain^(r−1); a kind absent from the
/// next block resets its run).
/// </summary>
public sealed record EndlessAffixRollPolicy
{
    /// <summary>
    /// Validates and freezes the policy: positive cadence/start, kinds in
    /// 1..4, a gain ≥ 1, and the no-repeat feasibility coupling — with
    /// <see cref="AllowConsecutiveRepeat"/> off the next block must avoid the
    /// previous block's kinds, which is satisfiable only while
    /// kindsPerRoll ≤ ⌊4/2⌋ = 2.
    /// </summary>
    /// <param name="rollEveryWaves">Roll cadence in waves (≥ 1; 10 = SP-7b「每 10 波」节奏).</param>
    /// <param name="rollStartWave">First rolled wave (≥ 1; 41 = SP-7b 首词缀波锚).</param>
    /// <param name="kindsPerRoll">Kinds drawn per block (1..4).</param>
    /// <param name="allowConsecutiveRepeat">重复规则: a kind may repeat in the immediately-following block.</param>
    /// <param name="repeatIntensityGain">叠加规则: intensity multiplier per consecutive re-roll (≥ 1).</param>
    /// <exception cref="ArgumentException">
    /// Non-positive cadence/start, kinds outside 1..4, a gain &lt; 1, or
    /// no-repeat with kindsPerRoll &gt; 2 (infeasible draw domain).
    /// </exception>
    public EndlessAffixRollPolicy(
        int rollEveryWaves,
        int rollStartWave,
        int kindsPerRoll,
        bool allowConsecutiveRepeat,
        double repeatIntensityGain)
    {
        if (rollEveryWaves < 1)
        {
            throw new ArgumentException($"RollEveryWaves must be ≥ 1 (got {rollEveryWaves}).", nameof(rollEveryWaves));
        }

        if (rollStartWave < 1)
        {
            throw new ArgumentException($"RollStartWave must be ≥ 1 (got {rollStartWave}).", nameof(rollStartWave));
        }

        if (kindsPerRoll is < 1 or > 4)
        {
            throw new ArgumentException($"KindsPerRoll must lie in 1..4 (got {kindsPerRoll}).", nameof(kindsPerRoll));
        }

        if (!allowConsecutiveRepeat && kindsPerRoll > NoRepeatMaxKindsPerRoll)
        {
            throw new ArgumentException(
                $"With AllowConsecutiveRepeat off, KindsPerRoll must be ≤ {NoRepeatMaxKindsPerRoll} " +
                $"(got {kindsPerRoll}) — the next block could not avoid the previous {kindsPerRoll} kinds.",
                nameof(kindsPerRoll));
        }

        if (!(repeatIntensityGain >= 1.0))
        {
            throw new ArgumentException(
                $"RepeatIntensityGain must be ≥ 1 (got {repeatIntensityGain}).", nameof(repeatIntensityGain));
        }

        RollEveryWaves = rollEveryWaves;
        RollStartWave = rollStartWave;
        KindsPerRoll = kindsPerRoll;
        AllowConsecutiveRepeat = allowConsecutiveRepeat;
        RepeatIntensityGain = repeatIntensityGain;
    }

    /// <summary>
    /// Kinds per roll the no-repeat rule can sustain (⌊4/2⌋=2: the next
    /// block must draw from 4−k unrolled kinds, feasible iff 4−k ≥ k).
    /// </summary>
    public const int NoRepeatMaxKindsPerRoll = 2; // ci-allow: R7 T-143 无重复滚动可行性上界（工程推导值，非平衡数值）

    /// <summary>Roll cadence in waves.</summary>
    public int RollEveryWaves { get; }

    /// <summary>First rolled wave (waves below roll nothing).</summary>
    public int RollStartWave { get; }

    /// <summary>Kinds drawn per block (1..4).</summary>
    public int KindsPerRoll { get; }

    /// <summary>重复规则: a kind may repeat in the immediately-following block.</summary>
    public bool AllowConsecutiveRepeat { get; }

    /// <summary>叠加规则: intensity multiplier per consecutive re-roll.</summary>
    public double RepeatIntensityGain { get; }

    /// <summary>
    /// T-143 production tier: every 10 waves from 41 on, ONE kind per block
    /// (SP-7b「每 10 波词缀」节奏 + 首词缀波锚), no consecutive repeat
    /// (连续两块不同词缀——重复规则默认严), re-roll intensity ×1.5
    /// (镜像 SP-7b 叠层强度 ×1.5 先例).
    /// </summary>
    public static EndlessAffixRollPolicy Default { get; } = new(
        rollEveryWaves: 10,      // ci-allow: R7 T-143 SP-7b「每 10 波」节奏锚（冻结表行口径沿用）
        rollStartWave: 41,       // ci-allow: R7 T-143 SP-7b 首词缀波 41 锚（冻结表行口径沿用）
        kindsPerRoll: 1,
        allowConsecutiveRepeat: false,
        repeatIntensityGain: 1.5); // ci-allow: R7 T-143 叠加规则默认档（镜像 SP-7b StackIntensity ×1.5 先例，[NEW-T-143]）
}

/// <summary>
/// The deterministic every-10-wave affix roller (T-143): resolves which of
/// the frozen four affix kinds a wave carries under an
/// <see cref="EndlessAffixRollPolicy"/>, as a PURE function of (seed, wave,
/// policy) — hash-indexed draws over the ordinal kind list, no
/// <see cref="Contracts.Foundation.IRng"/> contact (CS-1.4: this is
/// deterministic seed expansion, not randomness — the run's roster rng
/// stream never sees the rolls). Block b draws after filtering out the
/// previous block's kinds when the repeat rule forbids them; the per-kind
/// repeat-run length feeds the stacking rule. The 81+ global stack rule
/// (<see cref="EndlessAffixes.Stacks"/>) keeps applying underneath — rolling
/// replaces the ACTIVATION SCHEDULE only, never the magnitudes or the frozen
/// stacking cadence.
/// </summary>
public static class EndlessAffixRolls
{
    /// <summary>
    /// Block-count overflow guard: the block walk is O(block) per call, so
    /// an absurd wave (int domain ⇒ up to ~2×10⁸ blocks) is clamped here
    /// first — beyond the clamp the sequence simply freezes at the clamped
    /// block (same discipline as <see cref="EndlessAffixes.MaxStacksClamp"/>;
    /// engineering ceiling, not a balance number).
    /// </summary>
    public const int MaxBlocksClamp = 1000; // ci-allow: R7 T-143 滚动块溢出守卫（工程上限，非平衡数值）

    private static readonly EndlessAffixKind[] KindDomain =
    {
        EndlessAffixKind.Swift,
        EndlessAffixKind.Harden,
        EndlessAffixKind.Phase,
        EndlessAffixKind.SuicideEmpower,
    };

    /// <summary>
    /// Kinds active at <paramref name="wave"/> under the rolling schedule:
    /// empty below <see cref="EndlessAffixRollPolicy.RollStartWave"/>,
    /// otherwise the block's rolled kinds in ordinal kind order. Same
    /// (seed, policy) ⇒ same kinds at every wave — the determinism anchor.
    /// </summary>
    /// <exception cref="ArgumentNullException">policy is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">wave is negative.</exception>
    public static IReadOnlyList<EndlessAffixKind> KindsAt(long seed, int wave, EndlessAffixRollPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (wave < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wave), wave, "Waves are never negative.");
        }

        return Block(seed, wave, policy).Kinds;
    }

    /// <summary>
    /// Number of rolled kinds active at the wave (the 记分词缀数 of the
    /// T-080 score formula under the rolling schedule).
    /// </summary>
    /// <exception cref="ArgumentNullException">policy is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">wave is negative.</exception>
    public static int CountAt(long seed, int wave, EndlessAffixRollPolicy policy) =>
        KindsAt(seed, wave, policy).Count;

    /// <summary>
    /// Per-kind repeat intensity at the wave (叠加规则):
    /// <c>RepeatIntensityGain^(runLength−1)</c> for every rolled kind — 1.0
    /// for a kind's first consecutive block, compounding on each immediate
    /// re-roll. Kinds absent from the wave carry no entry.
    /// </summary>
    /// <exception cref="ArgumentNullException">policy is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">wave is negative.</exception>
    public static IReadOnlyDictionary<EndlessAffixKind, double> RepeatIntensityAt(
        long seed, int wave, EndlessAffixRollPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (wave < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wave), wave, "Waves are never negative.");
        }

        var gains = new Dictionary<EndlessAffixKind, double>();
        foreach ((EndlessAffixKind kind, int runLength) in Block(seed, wave, policy).RunLengths)
        {
            gains[kind] = Math.Pow(policy.RepeatIntensityGain, runLength - 1);
        }

        return gains;
    }

    /// <summary>
    /// Resolves the full modifier set of one wave under the rolling
    /// schedule: the rolled kinds replace the fixed SP-7b activation table
    /// (<see cref="EndlessAffixes.ActiveAt"/>), each kind's channel delta
    /// scales by the frozen stack intensity × the kind's repeat intensity;
    /// everything else (neutral absent channels, tick conversion, granted
    /// base suicide pair) reuses <see cref="EndlessAffixes.Modifiers"/>
    /// verbatim.
    /// </summary>
    /// <exception cref="ArgumentNullException">tuning or policy is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">wave is negative.</exception>
    public static EndlessEnemyModifiers Modifiers(
        long seed, int wave, EndlessAffixTuning tuning, EndlessAffixRollPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(policy);
        return EndlessAffixes.Modifiers(wave, tuning, KindsAt(seed, wave, policy), RepeatIntensityAt(seed, wave, policy));
    }

    /// <summary>
    /// The rolled block a wave lands in (T-143 cadence): waves below the
    /// roll start carry no kinds; otherwise block =
    /// ⌊(wave−RollStartWave)/RollEveryWaves⌋ clamped at
    /// <see cref="MaxBlocksClamp"/>, with its rolled kinds and repeat-run
    /// lengths walked deterministically from block 0.
    /// </summary>
    private static (IReadOnlyList<EndlessAffixKind> Kinds, IReadOnlyDictionary<EndlessAffixKind, int> RunLengths) Block(
        long seed, int wave, EndlessAffixRollPolicy policy)
    {
        if (wave < policy.RollStartWave)
        {
            return (Array.Empty<EndlessAffixKind>(), new Dictionary<EndlessAffixKind, int>());
        }

        long block = (wave - policy.RollStartWave) / policy.RollEveryWaves;
        int target = (int)Math.Min(block, MaxBlocksClamp);

        List<EndlessAffixKind> kinds = new(policy.KindsPerRoll);
        var runLengths = new Dictionary<EndlessAffixKind, int>();
        for (int b = 0; b <= target; b++)
        {
            // 重复规则：无连续重复时，候选域先剔除上一块已出的词缀
            // （policy 校验保证候选域 ≥ KindsPerRoll）。
            List<EndlessAffixKind> eligible = KindDomain
                .Where(kind => policy.AllowConsecutiveRepeat || b == 0 || !kinds.Contains(kind))
                .ToList();

            var picked = new List<EndlessAffixKind>(policy.KindsPerRoll);
            var pool = new List<EndlessAffixKind>(eligible);
            for (int pick = 0; pick < policy.KindsPerRoll; pick++)
            {
                int index = (int)(PickHash(seed, b, pick) % (ulong)pool.Count);
                picked.Add(pool[index]);
                pool.RemoveAt(index);
            }

            picked.Sort();
            kinds = picked;
            runLengths = picked.ToDictionary(
                kind => kind,
                kind => policy.AllowConsecutiveRepeat && runLengths.TryGetValue(kind, out int previous)
                    ? previous + 1
                    : 1);
        }

        return (kinds, runLengths);
    }

    /// <summary>
    /// FNV-1a over (seed, domain tag, block, pick) — the deterministic draw
    /// primitive. Explicit byte extraction (no BitConverter) keeps the byte
    /// stream platform-shape-stable.
    /// </summary>
    private static ulong PickHash(long seed, int block, int pick)
    {
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;

            ulong seedBits = (ulong)seed;
            for (int shift = 0; shift < 64; shift += 8)
            {
                hash ^= (seedBits >> shift) & 0xFFUL;
                hash *= prime;
            }

            foreach (byte tagByte in "endless-affix-roll"u8)
            {
                hash ^= tagByte;
                hash *= prime;
            }

            ulong blockBits = (ulong)block;
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash ^= (blockBits >> shift) & 0xFFUL;
                hash *= prime;
            }

            ulong pickBits = (ulong)pick;
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash ^= (pickBits >> shift) & 0xFFUL;
                hash *= prime;
            }

            return hash;
        }
    }
}
