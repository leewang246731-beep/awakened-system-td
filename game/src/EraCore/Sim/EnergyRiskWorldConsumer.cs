using EraCore.Contracts.Foundation;
using EraCore.Energy;
using EraCore.Units;

namespace EraCore.Sim;

/// <summary>
/// One live ground-pollution zone of an E4 meltdown (SP-4 L147「地面污染
/// 60 s（过境敌 −25% 速度）」, T-123 world-layer consumer state): anchored at
/// the destroyed generator's cell, slows every enemy crossing the radius and
/// ticks the HP decay while it stands inside. Immutable record; the owning
/// <see cref="EnergyRiskWorldConsumer"/> keeps the spawn-ordered list.
/// </summary>
/// <param name="Anchor">Blast anchor cell of the meltdown (the wreck site).</param>
/// <param name="RadiusCells">Zone radius in cells (= the meltdown explosion radius).</param>
/// <param name="StartTick">Tick the zone was spawned on.</param>
/// <param name="UntilTick">Last tick the zone is live on (spawn + duration); pruning removes it after.</param>
/// <param name="SlowPercent">Speed debuff of crossing enemies in percent (25).</param>
/// <param name="HpDecayPerSecond">Per-second HP decay of enemies standing inside ([NEW-T-123] content value, era4.json).</param>
public sealed record RadiationZone(
    CellCoord Anchor,
    double RadiusCells,
    long StartTick,
    long UntilTick,
    int SlowPercent,
    double HpDecayPerSecond);

/// <summary>
/// Settlement report of one applied energy-risk blast (T-123): counts of the
/// buildings/enemies hit and the enemy damage settled — observability for the
/// host and the tests (hand-computed anchors), no gameplay authority.
/// </summary>
/// <param name="Tick">Tick the blast settled on.</param>
/// <param name="Kind">Overheat explosion, meltdown or annihilation.</param>
/// <param name="Anchor">World anchor cell of the blast.</param>
/// <param name="BuildingsHit">Buildings inside the radius that took damage.</param>
/// <param name="EnemiesHit">Enemies inside the radius that took damage.</param>
/// <param name="EnemyDamageSettled">Total enemy points settled (shield + effective hp).</param>
public sealed record RiskBlastReport(
    long Tick,
    EnergyRiskEventKind Kind,
    CellCoord Anchor,
    int BuildingsHit,
    int EnemiesHit,
    int EnemyDamageSettled);

/// <summary>
/// One link of an E5 annihilation chain plan (T-134, SP-4 L148「连锁引爆半径内
/// 其他能源建筑（每连锁一栋伤害 ×0.6 衰减）」): the detonating energy building,
/// its chain depth (culprit = 0), and the flat unit damage its own detonation
/// carries (<c>UnitDamage × decay^depth</c>) at the (force-field-halvable)
/// radius. Pure plan — the host applies the links; deterministic by construction
/// (BFS in node-id order, each building detonates at most once).
/// </summary>
/// <param name="NodeId">The energy node that detonates on this link.</param>
/// <param name="Cell">Its world cell (the link's blast anchor).</param>
/// <param name="LinkDepth">Chain depth (0 = the culprit; k = k-th chained building).</param>
/// <param name="UnitDamage">Flat damage this link's detonation deals inside its radius.</param>
/// <param name="RadiusCells">This link's blast radius (already force-field-halved when applicable).</param>
public sealed record AnnihilationChainLink(
    Energy.EnergyNodeId NodeId,
    CellCoord Cell,
    int LinkDepth,
    double UnitDamage,
    double RadiusCells);

/// <summary>
/// World-layer consumer of <see cref="EnergyRiskOutcome"/> (T-123, F-010;
/// the W6-exit P1 deferred item 「AOE 消费面」 cleared here): the energy
/// ledger is position-free by design (C-08 owns the pool, not the map), so
/// the <see cref="Simulation"/> drains its append-only
/// <see cref="EnergyLedger.RiskEvents"/> queue after every settle and hands
/// each new outcome to this consumer, which applies the world-side payload
/// — E3 overheat AND E4 meltdown share one blast path (SP-4 L146/L147 后果列):
/// <list type="bullet">
/// <item>buildings inside <see cref="EnergyRiskOutcome.RadiusCells"/> of the
/// anchor take <c>round(MaxHp × BuildingDamageMaxHpFraction)</c> damage
/// (E3 50% / E4 80% — a fraction of MAX, so overlapping blasts do not
/// shrink);</item>
/// <item>enemies inside the radius take the flat
/// <see cref="EnergyRiskOutcome.EnemyDamage"/> through
/// <see cref="Unit.ApplyDamage"/> (E3 300; E4 carries 0 — SP-4 L147 damages
/// buildings, not units);</item>
/// <item>a positive <see cref="EnergyRiskOutcome.RadiationZoneSeconds"/>
/// (E4 only) spawns a <see cref="RadiationZone"/>: crossing enemies get the
/// −25% speed debuff (re-armed every tick they stay inside) and the HP decay
/// batch (per-second integer batch on the 30-tick boundary of the zone's
/// own start tick — 5/s in era4.json, derived = 300÷60).</item>
/// </list>
/// No <see cref="IRng"/> draws and no design numbers live here: every
/// magnitude arrives on the outcome record (built from the era's
/// <c>data/eras/{era}.json</c> risk table, RL7) or the
/// <paramref name="radiationHpDecayPerSecond"/> channel (era4.json →
/// <see cref="SimulationConfig.RadiationZoneHpDecayPerSecond"/>), so same
/// outcomes + same world state ⇒ same settlements by construction (CS-1.4).
/// Iteration orders are the caller's (placement-order buildings,
/// group-order units) and are never permuted — replay-stable.
/// </summary>
public sealed class EnergyRiskWorldConsumer
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Hold-over window of the per-tick zone slow re-arm in ticks: the slow
    /// is stamped to expire one tick after the current one, so an enemy that
    /// leaves the zone sheds the debuff on its next resolve (no lingering
    /// full-duration slow from a single crossing) — engine mechanics on the
    /// frozen 30 Hz domain, not a design number.
    /// </summary>
    internal const int ZoneSlowHoldTicks = 2;

    private readonly double _ticksPerSecond;
    private readonly double _hpDecayPerSecond;
    private readonly List<RadiationZone> _zones = new();

    /// <summary>
    /// Creates the consumer. <paramref name="ticksPerSecond"/> pins the tick
    /// domain (K-1 30 Hz); <paramref name="radiationHpDecayPerSecond"/> is
    /// the era content channel (era4.json
    /// <c>energy.meltdown.consequences.radiation_zone_hp_decay_per_s</c>,
    /// [NEW-T-123] = 5 = 300÷60) — 0 disables the HP-decay batch (E3 has no
    /// radiation zone at all, so the channel stays inert).
    /// </summary>
    /// <exception cref="ArgumentException">Non-finite/negative arguments.</exception>
    public EnergyRiskWorldConsumer(double ticksPerSecond = 30.0, double radiationHpDecayPerSecond = 0.0)
    {
        if (!double.IsFinite(ticksPerSecond) || ticksPerSecond <= 0)
        {
            throw new ArgumentException($"TicksPerSecond must be finite and positive (got {ticksPerSecond}).",
                nameof(ticksPerSecond));
        }

        if (!double.IsFinite(radiationHpDecayPerSecond) || radiationHpDecayPerSecond < 0)
        {
            throw new ArgumentException(
                $"RadiationHpDecayPerSecond must be finite and non-negative (got {radiationHpDecayPerSecond}).",
                nameof(radiationHpDecayPerSecond));
        }

        _ticksPerSecond = ticksPerSecond;
        _hpDecayPerSecond = radiationHpDecayPerSecond;
    }

    /// <summary>Live radiation zones in spawn order (expired ones pruned by <see cref="TickZones"/>).</summary>
    public IReadOnlyList<RadiationZone> RadiationZones => _zones;

    /// <summary>
    /// Applies one outcome's blast at <paramref name="anchor"/>: every
    /// building whose cell lies within the outcome radius takes the
    /// max-HP-fraction damage (row replaced through
    /// <paramref name="replaceBuilding"/> — the placement-order bookkeeping
    /// list stays authoritative), every living enemy inside takes the flat
    /// enemy damage through <see cref="Unit.ApplyDamage"/> (shield → armor →
    /// hp, the single damage pipeline — splash precedent). Returns the
    /// settlement report. Pure with respect to the consumer's own state (no
    /// zone is spawned here — see <see cref="SpawnZone"/>).
    /// </summary>
    /// <param name="outcome">Fired risk event (magnitudes are its own).</param>
    /// <param name="tick">Tick the blast settles on.</param>
    /// <param name="anchor">Anchor cell of the culprit generator.</param>
    /// <param name="buildings">Placement-order building rows (read-only scan).</param>
    /// <param name="replaceBuilding">Applies a damaged row at its index (Sim 替换 seam, upgrade-path precedent).</param>
    /// <param name="units">Group-order units (read-only scan).</param>
    /// <param name="onKill">Optional kill callback (Sim 击杀计数 seam).</param>
    /// <param name="radiusMultiplier">Blast radius multiplier (E5 annihilation: <see cref="EnergyRiskOutcome.ForceFieldRadiusMultiplier"/> when a force-field tower stands; default 1.0 = every earlier channel).</param>
    public RiskBlastReport ApplyBlast(
        EnergyRiskOutcome outcome,
        long tick,
        CellCoord anchor,
        IReadOnlyList<SimBuilding> buildings,
        Action<int, SimBuilding> replaceBuilding,
        IReadOnlyList<Unit> units,
        Action<Unit>? onKill = null,
        double radiusMultiplier = 1.0)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(buildings);
        ArgumentNullException.ThrowIfNull(replaceBuilding);
        ArgumentNullException.ThrowIfNull(units);
        if (!double.IsFinite(radiusMultiplier) || radiusMultiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radiusMultiplier), radiusMultiplier, "Blast radius multiplier must be finite and positive.");
        }

        double radius = outcome.RadiusCells * radiusMultiplier;
        int buildingsHit = 0;
        int enemiesHit = 0;
        int enemyDamage = 0;

        for (int i = 0; i < buildings.Count; i++)
        {
            SimBuilding building = buildings[i];
            double dx = building.Cell.X - anchor.X;
            double dy = building.Cell.Y - anchor.Y;
            if (Math.Sqrt((dx * dx) + (dy * dy)) > radius)
            {
                continue;
            }

            // SP-4: 对建筑=其最大 HP×fraction（半径内）——damage reads MAX
            // HP (not the current row), so a second overlapping blast takes
            // the same bite; the row floors at 0 (wreck-like, same standing
            // as the sale-ruin rows — removal/estate semantics stay with
            // the T-065 estate domain).
            int damage = (int)Math.Round(
                building.MaxHpResolved * outcome.BuildingDamageMaxHpFraction,
                MidpointRounding.AwayFromZero);
            int newHp = Math.Max(0, building.Hp - damage);
            if (newHp == building.Hp)
            {
                continue; // zero-fraction row (or already at 0): nothing to write
            }

            replaceBuilding(i, building with { Hp = newHp });
            buildingsHit++;
        }

        if (outcome.EnemyDamage > 0)
        {
            int raw = (int)Math.Round(outcome.EnemyDamage, MidpointRounding.AwayFromZero);
            foreach (Unit unit in units)
            {
                if (!unit.Alive || !IsInside(unit, anchor, radius))
                {
                    continue;
                }

                enemyDamage += unit.ApplyDamage(raw, tick);
                enemiesHit++;
                if (!unit.Alive)
                {
                    onKill?.Invoke(unit);
                }
            }
        }

        return new RiskBlastReport(tick, outcome.Kind, anchor, buildingsHit, enemiesHit, enemyDamage);
    }

    /// <summary>
    /// Plans the E5 annihilation chain reaction (T-134, SP-4 L148「连锁引爆
    /// 半径内其他能源建筑（每连锁一栋伤害 ×0.6 衰减）」): a breadth-first
    /// detonation plan starting at the culprit (<paramref name="culpritCell"/>)
    /// — every energy building inside the (force-field-halvable) radius of a
    /// detonated anchor joins the chain at its parent's depth + 1, its own
    /// detonation dealing <c>UnitDamage × decay^depth</c>. Deterministic: the
    /// frontier and <paramref name="energyBuildings"/> both process in
    /// node-id order and each building detonates at most once. Pure — the
    /// host applies the links (unit damage per link + chained-producer loss).
    /// </summary>
    /// <param name="outcome">The fired annihilation outcome (magnitudes are its own).</param>
    /// <param name="culpritCell">World cell of the annihilated fusion plant (link 0).</param>
    /// <param name="energyBuildings">Standing energy buildings (node id + cell); the culprit need not be among them.</param>
    /// <param name="forceFieldPresent">Whether a force-field tower stands (SP-1b L54: halves the blast radius).</param>
    public static IReadOnlyList<AnnihilationChainLink> PlanAnnihilationChain(
        EnergyRiskOutcome outcome,
        CellCoord culpritCell,
        IReadOnlyList<(Energy.EnergyNodeId Id, CellCoord Cell)> energyBuildings,
        bool forceFieldPresent)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(culpritCell);
        ArgumentNullException.ThrowIfNull(energyBuildings);
        if (outcome.Kind != EnergyRiskEventKind.Annihilation)
        {
            throw new ArgumentException(
                $"Chain planning is the annihilation channel's (got {outcome.Kind}).", nameof(outcome));
        }

        double radius = outcome.RadiusCells
            * (forceFieldPresent ? outcome.ForceFieldRadiusMultiplier : 1.0);
        double decay = outcome.ChainDamageDecayFraction;

        // Node-id-ordered candidate list: deterministic frontier regardless
        // of the caller's registration order.
        var candidates = energyBuildings
            .OrderBy(row => row.Id.Value, Comparer<long>.Default)
            .ToList();

        var links = new List<AnnihilationChainLink> { new(outcome.Culprit, culpritCell, 0, outcome.EnemyDamage, radius) };
        var detonated = new HashSet<Energy.EnergyNodeId> { outcome.Culprit };
        var frontier = new List<AnnihilationChainLink>(links);

        while (frontier.Count > 0 && decay > 0)
        {
            var next = new List<AnnihilationChainLink>();
            foreach (AnnihilationChainLink origin in frontier)
            {
                foreach ((Energy.EnergyNodeId id, CellCoord cell) in candidates)
                {
                    if (detonated.Contains(id))
                    {
                        continue; // each building detonates at most once
                    }

                    double dx = cell.X - origin.Cell.X;
                    double dy = cell.Y - origin.Cell.Y;
                    if (Math.Sqrt((dx * dx) + (dy * dy)) > radius)
                    {
                        continue;
                    }

                    var link = new AnnihilationChainLink(
                        id, cell, origin.LinkDepth + 1, outcome.EnemyDamage * Math.Pow(decay, origin.LinkDepth + 1), radius);
                    detonated.Add(id);
                    links.Add(link);
                    next.Add(link);
                }
            }

            frontier = next;
        }

        return links;
    }

    /// <summary>
    /// Spawns the outcome's radiation zone (E4 meltdown only — positive
    /// <see cref="EnergyRiskOutcome.RadiationZoneSeconds"/>) at the blast
    /// anchor: radius = the explosion radius, slow = the outcome fraction,
    /// decay = the era content channel. Returns the zone, or null when the
    /// outcome carries no zone (E3 overheat: seconds 0).
    /// </summary>
    public RadiationZone? SpawnZone(EnergyRiskOutcome outcome, CellCoord anchor, long tick)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(anchor);
        if (outcome.RadiationZoneSeconds <= 0)
        {
            return null;
        }

        var zone = new RadiationZone(
            Anchor: anchor,
            RadiusCells: outcome.RadiusCells,
            StartTick: tick,
            UntilTick: tick + SecondsToTicks(outcome.RadiationZoneSeconds),
            SlowPercent: (int)Math.Round(outcome.RadiationEnemySlowFraction * 100.0, MidpointRounding.AwayFromZero),
            HpDecayPerSecond: _hpDecayPerSecond);
        _zones.Add(zone);
        return zone;
    }

    /// <summary>
    /// One per-tick pass over the live zones (called by the Sim right after
    /// the blast drain, i.e. before the unit layer's
    /// <see cref="Unit.ResolveSpeedEffects"/> of the same tick): enemies
    /// inside a zone get its slow re-armed for a one-tick hold (shed on
    /// leaving), and every whole second of zone age (on the zone's own
    /// 30-tick boundary) they take the integer per-second HP-decay batch
    /// through <see cref="Unit.ApplyDamage"/> (5/s ⇒ 5 points per batch; a
    /// full 60 s stand takes 300 = the E3 overheat enemy-hit budget,
    /// era4.json derivation). Expired zones are pruned after the pass.
    /// Zones iterate in spawn order, units in group order — deterministic.
    /// </summary>
    /// <param name="tick">Current simulation tick.</param>
    /// <param name="units">Group-order units (read-only scan).</param>
    /// <param name="onKill">Optional kill callback (Sim 击杀计数 seam).</param>
    public void TickZones(long tick, IReadOnlyList<Unit> units, Action<Unit>? onKill = null)
    {
        ArgumentNullException.ThrowIfNull(units);
        if (_zones.Count == 0)
        {
            return;
        }

        for (int zi = 0; zi < _zones.Count; zi++)
        {
            RadiationZone zone = _zones[zi];
            if (tick > zone.UntilTick)
            {
                continue;
            }

            long age = tick - zone.StartTick;
            bool decayTick = _hpDecayPerSecond >= 1.0
                && age > 0
                && age % SecondsToTicks(1.0) == 0;
            foreach (Unit unit in units)
            {
                if (!unit.Alive || !IsInside(unit, zone.Anchor, zone.RadiusCells))
                {
                    continue;
                }

                if (zone.SlowPercent > 0)
                {
                    unit.ApplySlow(zone.SlowPercent, tick + ZoneSlowHoldTicks);
                }

                if (decayTick)
                {
                    _ = unit.ApplyDamage((int)Math.Round(_hpDecayPerSecond, MidpointRounding.AwayFromZero), tick);
                    if (!unit.Alive)
                    {
                        onKill?.Invoke(unit);
                    }
                }
            }
        }

        _zones.RemoveAll(z => tick > z.UntilTick);
    }

    /// <summary>
    /// FNV-1a hash over the live zone list (spawn order) — folded into the
    /// Sim hash only when zones exist (absent consumers contribute nothing,
    /// so E1/E2/E3-without-meltdown hashes stay bit-identical).
    /// </summary>
    public long ComputeStateHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, (ulong)_zones.Count);
        foreach (RadiationZone zone in _zones)
        {
            hash = Mix(hash, (ulong)zone.Anchor.X);
            hash = Mix(hash, (ulong)zone.Anchor.Y);
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(zone.RadiusCells));
            hash = Mix(hash, (ulong)zone.StartTick);
            hash = Mix(hash, (ulong)zone.UntilTick);
            hash = Mix(hash, (ulong)zone.SlowPercent);
            hash = Mix(hash, BitConverter.DoubleToUInt64Bits(zone.HpDecayPerSecond));
        }

        return unchecked((long)hash);

        static ulong Mix(ulong hash, ulong value)
        {
            hash ^= value;
            return hash * FnvPrime;
        }
    }

    /// <summary>
    /// Distance test of one unit against a zone/blast anchor in the cell
    /// unit: Euclidean pixel distance ÷ <see cref="Simulation.CellSizePx"/>
    /// (1 cell = 32 px, K-1 domain; the splash-candidate precedent). Edge
    /// inclusive at the radius.
    /// </summary>
    private static bool IsInside(Unit unit, CellCoord anchor, double radiusCells)
    {
        float centreX = (anchor.X * Simulation.CellSizePx) + (Simulation.CellSizePx / 2f);
        float centreY = (anchor.Y * Simulation.CellSizePx) + (Simulation.CellSizePx / 2f);
        float dx = unit.X - centreX;
        float dy = unit.Y - centreY;
        return Math.Sqrt((dx * dx) + (dy * dy)) / Simulation.CellSizePx <= radiusCells;
    }

    private long SecondsToTicks(double seconds) => (long)Math.Ceiling(seconds * _ticksPerSecond);
}
