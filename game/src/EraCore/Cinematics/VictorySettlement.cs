using EraCore.Agent.Governance;
using EraCore.Contracts.Foundation;
using EraCore.Progression;

namespace EraCore.Cinematics;

/// <summary>One checklist row of the verdict reveal (三重目标逐项打勾, T-087).</summary>
/// <param name="LabelZh">Display label of the objective (content name, id fallback).</param>
/// <param name="Passed">Whether the objective passed (勾/叉).</param>
public sealed record VictoryVerdictRow(string LabelZh, bool Passed);

/// <summary>One segment of the lucky wheel layout (幸运转盘（结算）——每段必有奖，无空档).</summary>
/// <param name="ItemId">Decorative item the segment carries (whitelisted reward class).</param>
/// <param name="Rarity">Its in-box rarity bucket (SP-8c 60/30/10 domain).</param>
/// <param name="RewardClass">Its frozen whitelist reward class.</param>
/// <param name="LabelZh">Display label.</param>
public sealed record VictoryWheelSegment(
    string ItemId,
    RewardRarity Rarity,
    string RewardClass,
    string LabelZh);

/// <summary>
/// The decided wheel (RL12 先定后演): full layout + the stop index + the
/// granted item — all fixed at settlement time; the performance only
/// back-derives the pointer path so it lands on <see cref="StopIndex"/>
/// (演出倒推，见 <see cref="WheelDynamics"/>).
/// </summary>
/// <param name="StopIndex">Decided landing segment index.</param>
/// <param name="Segments">Decided layout (无空档：每段一件装饰奖励).</param>
/// <param name="Granted">The granted item (= <c>Segments[StopIndex]</c>).</param>
public sealed record DecidedWheel(
    int StopIndex,
    IReadOnlyList<VictoryWheelSegment> Segments,
    GrantedChestItem Granted)
{
    /// <summary>Value equality over the segment list (the RL12 artifact compares by content, not list identity).</summary>
    public bool Equals(DecidedWheel? other) =>
        other is not null
        && StopIndex == other.StopIndex
        && Granted == other.Granted
        && Segments.SequenceEqual(other.Segments);

    /// <summary>Content hash off the scalar members (list excluded).</summary>
    public override int GetHashCode() => HashCode.Combine(StopIndex, Granted?.GetHashCode() ?? 0);
}

/// <summary>The decided system commentary (按信任档位+人格；RL12：句随结算一次定死).</summary>
/// <param name="Persona">Persona the line was composed for.</param>
/// <param name="Badge">Trust badge at victory time.</param>
/// <param name="MainLine">The settlement one-liner (4 模板 × 3 档成品句).</param>
/// <param name="TrustShiftEcho">T-075 跨档台词回声——本关内徽记跨档时追加，同档为 null.</param>
public sealed record DecidedCommentary(
    SystemPersona Persona,
    TrustBadge Badge,
    string MainLine,
    string? TrustShiftEcho);

/// <summary>
/// The ENTIRE decided settlement performance (T-087, F-002/F-104, RL12
/// 先定后演): one immutable record computed the moment the settlement
/// starts, before a single performance effect fires. The
/// <see cref="CinematicTimeline"/> (and every replay/回看) only READS this
/// record — skipping, accelerating or quitting mid-performance can never
/// change a byte of it.
/// </summary>
/// <param name="LevelId">Campaign level the settlement belongs to.</param>
/// <param name="RunSeed">Run seed the wheel stream derived from (chest stream uses it too, via SettleVictory).</param>
/// <param name="VerdictRows">Triple-objective checklist (3 rows: 生存/策略/挑战).</param>
/// <param name="Tier">Objective tier (青铜/白银/黄金/黄金+额外).</param>
/// <param name="ChestTierId">Chest tier id (bronze/silver/gold/gold_extra).</param>
/// <param name="ChestItems">Decided chest items (already granted by CampaignProgression.SettleVictory).</param>
/// <param name="ChestLightLayers">Chest light-layer count (光效分层：黄金比白银多一层).</param>
/// <param name="Wheel">The decided wheel (layout + stop + granted).</param>
/// <param name="Commentary">The decided commentary.</param>
public sealed record DecidedVictoryPerformance(
    string LevelId,
    long RunSeed,
    IReadOnlyList<VictoryVerdictRow> VerdictRows,
    ObjectiveTier Tier,
    string ChestTierId,
    IReadOnlyList<GrantedChestItem> ChestItems,
    int ChestLightLayers,
    DecidedWheel Wheel,
    DecidedCommentary Commentary)
{
    /// <summary>
    /// Value equality over the row/item lists (RL12: two decisions of the
    /// same inputs are THE SAME artifact — equality must hold across
    /// serialize/restore boundaries, so list members compare by content).
    /// </summary>
    public bool Equals(DecidedVictoryPerformance? other) =>
        other is not null
        && LevelId == other.LevelId
        && RunSeed == other.RunSeed
        && Tier == other.Tier
        && ChestTierId == other.ChestTierId
        && ChestLightLayers == other.ChestLightLayers
        && EqualityComparer<DecidedWheel>.Default.Equals(Wheel, other.Wheel)
        && EqualityComparer<DecidedCommentary>.Default.Equals(Commentary, other.Commentary)
        && VerdictRows.SequenceEqual(other.VerdictRows)
        && ChestItems.SequenceEqual(other.ChestItems);

    /// <summary>Content hash off the scalar members (lists excluded).</summary>
    public override int GetHashCode() =>
        HashCode.Combine(LevelId, RunSeed, Tier, ChestTierId, ChestLightLayers, Wheel);
}

/// <summary>
/// Decided-before-shown settlement core (T-087). <see cref="Decide"/> is the
/// ONLY place randomness enters the victory performance: one RNG stream,
/// seeded once from (run seed + level id + judgment input), fixes the wheel
/// layout and stop before the performance exists. The chest items arrive
/// already decided inside <see cref="CampaignSettlement"/> (rolled by
/// <see cref="CampaignProgression.SettleVictory"/> off the same run seed) —
/// so skipping the wheel, watching it all, or quitting mid-game all read the
/// same bytes.
/// </summary>
public static class VictorySettlement
{
    /// <summary>Wheel seed domain suffix (keeps the wheel stream off the chest stream of the same run).</summary>
    public const string WheelSeedSuffix = ":victory-wheel";

    /// <summary>Minimum segment count a wheel layout must carry (低于它不成为转盘).</summary>
    public const int MinWheelSegments = 3; // ci-allow: R7 T-087 转盘段数下限为 §12 结算演出结构域常量（非内容平衡数值）

    /// <summary>
    /// Decides the whole victory performance. Draw order (frozen): every
    /// wheel segment in order (rarity by the table's 60/30/10 weights, then
    /// a uniform item of that rarity), THEN the stop index. Same
    /// (run seed, level id, completed count, table) ⇒ byte-identical wheel.
    /// </summary>
    /// <param name="settlement">The banked campaign settlement (judge + chest already decided).</param>
    /// <param name="table">The chest table (wheel segments draw from its decorative pools — R9 不碰数值平衡).</param>
    /// <param name="rng">Seeded random port (CS-1.4) — reseeded here once.</param>
    /// <param name="wheelSegments">Segment count of the wheel (from the loaded script's wheel_spin step, RL7).</param>
    /// <param name="runSeed">The run seed (LevelSession.Victory.Seed — the same stream root the chest used).</param>
    /// <param name="persona">Current system persona (§10.7).</param>
    /// <param name="badgeAtVictory">Trust badge at victory time (点评措辞档).</param>
    /// <param name="badgeAtLevelStart">Trust badge at level start (跨档回声判据).</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">wheelSegments below <see cref="MinWheelSegments"/>, or blank level id.</exception>
    public static DecidedVictoryPerformance Decide(
        CampaignSettlement settlement,
        ChestTable table,
        IRng rng,
        int wheelSegments,
        long runSeed,
        SystemPersona persona,
        TrustBadge badgeAtVictory,
        TrustBadge badgeAtLevelStart)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rng);
        if (string.IsNullOrWhiteSpace(settlement.LevelId))
        {
            throw new ArgumentException("settlement.LevelId must not be blank.", nameof(settlement));
        }

        if (wheelSegments < MinWheelSegments)
        {
            throw new ArgumentException(
                $"wheelSegments must be >= {MinWheelSegments} (got {wheelSegments}).", nameof(wheelSegments));
        }

        // 先定后演的种子：关卡 id + 轮盘域后缀，掺 run seed 与判分完成数
        // （判分输入）——同 seed 同判分 ⇒ 同转盘；档位不同 ⇒ 流不同。
        rng.Reseed(CampaignHash.Combine(
            settlement.LevelId + WheelSeedSuffix,
            runSeed ^ settlement.Objectives.CompletedCount));

        var segments = new List<VictoryWheelSegment>(wheelSegments);
        for (int i = 0; i < wheelSegments; i++)
        {
            RewardRarity rarity = RollRarity(table, rng);
            IReadOnlyList<ChestItemSpec> pool = table.ItemsByRarity[rarity];
            ChestItemSpec item = pool[rng.Next(0, pool.Count)];
            segments.Add(new VictoryWheelSegment(
                item.Id, item.Rarity, item.RewardClass, item.NameZh ?? item.NameEn ?? item.Id));
        }

        int stop = rng.Next(0, wheelSegments);
        VictoryWheelSegment landed = segments[stop];

        return new DecidedVictoryPerformance(
            settlement.LevelId,
            runSeed,
            VerdictRowsOf(settlement),
            settlement.Objectives.Tier,
            settlement.ChestTierId,
            settlement.ChestItems,
            LightLayers(settlement.Objectives.Tier),
            new DecidedWheel(
                stop,
                segments,
                new GrantedChestItem(landed.ItemId, landed.Rarity, landed.RewardClass)),
            new DecidedCommentary(
                persona,
                badgeAtVictory,
                SettlementCommentary.Compose(persona, badgeAtVictory),
                badgeAtLevelStart == badgeAtVictory
                    ? null
                    : SettlementCommentary.TrustShiftEcho(badgeAtLevelStart, badgeAtVictory)));
    }

    /// <summary>
    /// Triple-objective checklist rows (判分结果展示): 生存恒为第 1 元
    /// (SP-8c)，策略/挑战行用内容名（缺名回退 id）。
    /// </summary>
    public static IReadOnlyList<VictoryVerdictRow> VerdictRowsOf(CampaignSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ObjectiveDraw draw = settlement.Objectives.Draw;
        return new[]
        {
            new VictoryVerdictRow("生存 · 守住最终波", settlement.Objectives.Survived),
            new VictoryVerdictRow(draw.Strategy.NameZh ?? draw.Strategy.Id, settlement.Objectives.StrategyPassed),
            new VictoryVerdictRow(draw.Challenge.NameZh ?? draw.Challenge.Id, settlement.Objectives.ChallengePassed),
        };
    }

    /// <summary>
    /// Chest light-layer count (光效分层，黄金多一层——比白银恰多一层；
    /// 黄金+额外的额外奖励走加成件数不走光层): 青铜 1 / 白银 2 / 黄金 3 / 黄金+额外 3.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Unknown tier.</exception>
    public static int LightLayers(ObjectiveTier tier) => tier switch
    {
        ObjectiveTier.Bronze => 1,
        ObjectiveTier.Silver => 2, // ci-allow: R7 T-087 宝箱光效分层数为 §12 结算演出结构域常量（非内容平衡数值）
        ObjectiveTier.Gold => 3, // ci-allow: R7 T-087 黄金档比白银恰多一层（§12 结算演出规格）
        ObjectiveTier.GoldExtra => 3, // ci-allow: R7 T-087 同黄金光层，额外奖励走 bonus 件数（SP-8c）
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "unknown objective tier."),
    };

    /// <summary>
    /// Weighted rarity walk — the same frozen Common→Rare→Legendary
    /// cumulative walk <see cref="ChestRewards"/> rolls with (kept local:
    /// that member is private; the walk order is the table's
    /// <c>OrderedWeights</c>, deterministic).
    /// </summary>
    private static RewardRarity RollRarity(ChestTable table, IRng rng)
    {
        double total = 0.0;
        foreach ((_, double weight) in table.OrderedWeights)
        {
            total += weight;
        }

        double roll = rng.NextDouble() * total;
        double cumulative = 0.0;
        foreach ((RewardRarity rarity, double weight) in table.OrderedWeights)
        {
            cumulative += weight;
            if (roll < cumulative)
            {
                return rarity;
            }
        }

        return table.OrderedWeights[^1].Rarity;
    }
}

/// <summary>
/// Pure pointer math of the lucky wheel (转盘演出倒推, T-087): given the
/// DECIDED stop index and the SCRIPTED deceleration pause frames, the
/// pointer path is a deterministic pure function — the host renders it
/// per frame, and the landing invariant
/// <c>WheelDynamics.SegmentAt(…PointerAngle(…, elapsed = duration)…) == stopIndex</c>
/// holds for every stop (先定后演的演出面：结果决定落点，不是落点决定结果).
/// <para>
/// Shape (frozen): the head phase (duration − Σdecel) sweeps 80% of the
/// total travel linearly; the tail splits the remaining 20% into one jump
/// per scripted pause group, weighted by its frame count — longer pauses
/// later = the decelerating tick…tick……tick read (减速停顿帧数脚本化).
/// Total travel = 3 full revolutions + exactly the arc that centers the
/// decided segment under the needle (start angle 0°).
/// </para>
/// </summary>
public static class WheelDynamics
{
    /// <summary>Share of the travel the fast head phase sweeps (presentation dynamics constant).</summary>
    public const double HeadTravelShare = 0.8;

    /// <summary>Full revolutions before the landing arc (presentation dynamics constant).</summary>
    public const int BaseRevolutions = 3; // ci-allow: R7 T-087 转盘整圈数为 §12 结算演出动力学域常量（非内容平衡数值）

    /// <summary>Angle tolerance of the segment lookup (double-precision arc arithmetic drift; CS-3.3 容差来源).</summary>
    public const double AngleEpsilon = 1e-9;

    /// <summary>
    /// Pointer angle in degrees [0, 360) at one elapsed tick of the wheel
    /// step. Deterministic pure function of the decided stop and the
    /// scripted profile; <paramref name="elapsedTicks"/> is clamped to
    /// [0, <paramref name="durationTicks"/>].
    /// </summary>
    /// <exception cref="ArgumentException">decel profile empty, or a pause shorter than one frame.</exception>
    public static double PointerAngle(
        int segmentCount,
        int stopIndex,
        int durationTicks,
        IReadOnlyList<int> decelTicks,
        int elapsedTicks)
    {
        if (decelTicks.Count == 0 || decelTicks.Any(pause => pause < 1))
        {
            throw new ArgumentException("decel profile must carry pauses of at least one frame.", nameof(decelTicks));
        }

        double arc = 360.0 / segmentCount;
        double center = (stopIndex + 0.5) * arc;
        double travel = BaseRevolutions * 360.0 + Mod(center, 360.0);

        int headTicks = durationTicks - decelTicks.Sum();
        int elapsed = Math.Clamp(elapsedTicks, 0, durationTicks);
        if (headTicks <= 0 || elapsed <= headTicks)
        {
            return Mod(travel * HeadTravelShare * (elapsed / (double)Math.Max(1, headTicks)), 360.0);
        }

        // Tail: one jump per pause group, weighted by its scripted frame count.
        int tailElapsed = elapsed - headTicks;
        int tailTotal = decelTicks.Sum();
        int boundary = 0;
        double weightCovered = 0.0;
        foreach (int pause in decelTicks)
        {
            int next = boundary + pause;
            if (tailElapsed <= next)
            {
                break; // inside this dwell window — the group's jump already fired
            }

            weightCovered += pause / (double)tailTotal;
            boundary = next;
        }

        double tailShare = weightCovered + WeightAtBoundary(decelTicks, boundary, tailTotal);
        return Mod(travel * (HeadTravelShare + (1.0 - HeadTravelShare) * tailShare), 360.0);
    }

    /// <summary>
    /// Segment index under one pointer angle (segments span equal arcs from 0°;
    /// the decided landing is a segment CENTER, so the epsilon absorbs the
    /// double-precision drift of the arc arithmetic).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">segmentCount below 1.</exception>
    public static int SegmentAt(double angleDegrees, int segmentCount)
    {
        if (segmentCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentCount), segmentCount, "segment count must be >= 1.");
        }

        double arc = 360.0 / segmentCount;
        int index = (int)Math.Floor(Mod(angleDegrees, 360.0) / arc + AngleEpsilon);
        return Math.Clamp(index, 0, segmentCount - 1);
    }

    private static double WeightAtBoundary(IReadOnlyList<int> decelTicks, int boundary, int tailTotal)
    {
        // The dwell window the pointer entered contributes its FULL jump
        // (jumps fire at window starts); windows fully passed already
        // contributed via weightCovered. boundary = frames before that window.
        foreach (int pause in decelTicks)
        {
            if (boundary <= 0)
            {
                return pause / (double)tailTotal;
            }

            boundary -= pause;
        }

        return 1.0;
    }

    private static double Mod(double value, double modulus) =>
        value - modulus * Math.Floor(value / modulus);
}
