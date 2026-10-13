using EraCore.Balance;
using EraCore.Contracts.Foundation;

namespace EraCore.Units;

/// <summary>
/// Trample tuning for boss auras (踏群者践踏, SP-3a L107 "周期伤害 [NEW]" — the
/// period/value pair is fixed by this task, the radius comes from enemy
/// content data). Supplied by the caller so the engine itself carries no
/// design numbers (CS-2.3/RL7).
/// </summary>
/// <param name="Damage">Damage per pulse, points.</param>
/// <param name="PeriodTicks">Pulse period in 30 Hz ticks.</param>
public sealed record TrampleProfile(int Damage, int PeriodTicks);

/// <summary>
/// Wave spawn parameters (C-10 E1 thin, T-039) — every gameplay knob is a
/// field so waves are fully parameterized (F-030/F-031/F-033/F-034):
/// regular-line count shape (基数/起/间隔/成长), threat level (feeds the
/// T-036 multiplier library), elite line, boss wave with the v3.2 §6 boss
/// state multipliers, and the in-wave entry rhythm ([NEW]: batches per wave
/// spread over the interval minus the F-034 valley). Structural defaults
/// cite their frozen sources; content overrides arrive from
/// data/waves/{era}.json once T-049 wires it.
/// </summary>
/// <param name="BaseCount">Regular line 基数 (F-030).</param>
/// <param name="BeginWave">Regular line ramp start wave 起 (F-030).</param>
/// <param name="Spacing">Regular line ramp step interval 间隔 (F-030).</param>
/// <param name="Scaling">Regular line growth divisor 成长 (F-030).</param>
/// <param name="ThreatLevel">Level threat t — count/hp/interval multipliers come from <see cref="Threat.Multipliers"/> (T-036, F-049).</param>
/// <param name="RegularRoster">Enemy content ids cycled deterministically for regular spawns.</param>
/// <param name="EliteLineEnabled">Whether the independent elite line spawns (F-031).</param>
/// <param name="EliteEnemyId">Enemy id of elite spawns; null disables the line.</param>
/// <param name="BossWave">Wave number that spawns the boss (E1 final level: 20).</param>
/// <param name="BossEnemyId">Enemy id of the boss; null disables boss waves.</param>
/// <param name="BossHpMultiplier">Boss-state hp multiplier (v3.2 §6: ×1.5).</param>
/// <param name="BossDamageMultiplier">Boss-state damage multiplier (v3.2 §6: ×1.3) — scales trample damage.</param>
/// <param name="FirstWaveGraceMultiplier">Grace before wave 1 (F-034 band 1.5~2; default 1.5).</param>
/// <param name="ValleySeconds">Low-density valley reserved at the end of each wave interval (F-034: 15 s).</param>
/// <param name="BatchesPerWave">In-wave entry rhythm: spawn batches per wave, evenly spread over interval − valley ([NEW-原型校准]).</param>
/// <param name="TickRateHz">Tick domain (K-1 IClock: 30).</param>
/// <param name="CellSizePx">Cell size in px (K-1: 32).</param>
/// <param name="EntryCells">Cells units spawn on (one drawn per unit via the injected IRng).</param>
/// <param name="Trample">Trample tuning for specs carrying a trample radius; null disables trample.</param>
public sealed record WaveSpawnConfig(
    int BaseCount,
    int BeginWave,
    int Spacing,
    double Scaling,
    double ThreatLevel,
    IReadOnlyList<string> RegularRoster,
    bool EliteLineEnabled = false,
    string? EliteEnemyId = null,
    int BossWave = 20, // ci-allow: R7 T-039 E1 终关 Boss 波=20（任务卡/v3.2 §6 口径）——波表数据接管前的记录缺省值，调用方以配置显式覆盖
    string? BossEnemyId = null,
    double BossHpMultiplier = 1.5,
    double BossDamageMultiplier = 1.3,
    double FirstWaveGraceMultiplier = 1.5,
    double ValleySeconds = 15.0,
    int BatchesPerWave = 4,
    int TickRateHz = 30,
    int CellSizePx = 32,
    IReadOnlyList<CellCoord>? EntryCells = null,
    TrampleProfile? Trample = null);

/// <summary>
/// One spawn batch of one wave: <paramref name="Count"/> units materialize
/// together at StartTick + TickOffset.
/// </summary>
/// <param name="TickOffset">Offset from the wave start tick (≥ 0).</param>
/// <param name="Count">Units in this batch.</param>
/// <param name="IsBoss">Boss batch (boss-state multipliers apply).</param>
/// <param name="IsElite">Elite-line batch (independent formula F-031).</param>
public sealed record SpawnBatch(long TickOffset, int Count, bool IsBoss, bool IsElite)
{
    /// <summary>True for regular-line batches.</summary>
    public bool IsRegular => !IsBoss && !IsElite;
}

/// <summary>
/// Deterministic spawn schedule of one wave: which batches enter at which
/// offsets inside the wave interval, plus the wave-shape accounting the
/// tests assert against the T-036 formula library.
/// </summary>
/// <param name="Wave">Wave number.</param>
/// <param name="StartTick">Absolute tick the wave begins at.</param>
/// <param name="IntervalTicks">Ticks until the next wave (from Campaign.WaveIntervalSeconds × tick rate).</param>
/// <param name="Batches">Spawn batches in entry order.</param>
/// <param name="RegularCount">Regular-line total (WaveFormula.Count output).</param>
/// <param name="EliteCount">Elite-line total (Elite.Count output).</param>
/// <param name="HasBoss">Whether the wave carries a boss.</param>
public sealed record WavePlan(
    int Wave,
    long StartTick,
    long IntervalTicks,
    IReadOnlyList<SpawnBatch> Batches,
    int RegularCount,
    int EliteCount,
    bool HasBoss)
{
    /// <summary>Absolute tick of the last entry of this wave.</summary>
    public long LastEntryTick
    {
        get
        {
            long max = StartTick;
            foreach (SpawnBatch batch in Batches)
            {
                long at = StartTick + batch.TickOffset;
                if (at > max)
                {
                    max = at;
                }
            }

            return max;
        }
    }

    /// <summary>Total units this wave spawns.</summary>
    public int TotalCount => Batches.Sum(b => b.Count);
}

/// <summary>
/// Wave spawner (C-10 E1 thin, T-039): turns wave numbers into unit spawn
/// events. Counts come EXCLUSIVELY from the T-036 formula library —
/// <see cref="WaveFormula.Count"/> for the regular line (F-030) and
/// <see cref="Elite.Count"/> for the independent elite line (F-031) — this
/// class never re-implements a formula. The interval comes from
/// <see cref="Campaign.WaveIntervalSeconds"/> (F-034); hp from the threat
/// hp multiplier and, on boss waves, the v3.2 §6 boss-state ×1.5 (damage
/// knobs scale ×1.3 through the trample profile). Entry cells are drawn
/// from the injected <see cref="IRng"/> and entity ids are issued
/// sequentially, so identical (seed, config, tick sequence) inputs produce
/// identical spawn sequences — the root of replay determinism. C-12
/// (T-040) takes over id issuance and bus publication at merge.
/// </summary>
public sealed class WaveSpawner
{
    private readonly EnemySpecCatalog _catalog;
    private readonly WaveSpawnConfig _cfg;
    private readonly IRng _rng;
    private readonly IClock _clock;
    private readonly double _threatCountMultiplier;
    private readonly double _threatHpMultiplier;
    private readonly long _intervalTicks;
    private readonly long _graceTicks;
    private readonly long _valleyTicks;
    private readonly IReadOnlyList<CellCoord> _entryCells;
    private long _nextEntityId;
    private long _spawnCounter;
    private WavePlan? _plan;
    private int _nextBatchIndex;

    /// <summary>
    /// Validates the configuration structurally and resolves everything
    /// derivable from it (threat multipliers, interval, grace, valley).
    /// </summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// Empty roster/entry cells, non-positive tick rate / cell size /
    /// batches / multipliers, negative threat, or a valley that leaves no
    /// entry room inside the wave interval.
    /// </exception>
    public WaveSpawner(EnemySpecCatalog catalog, WaveSpawnConfig config, IRng rng, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(clock);

        if (config.RegularRoster.Count == 0)
        {
            throw new ArgumentException("RegularRoster must not be empty.", nameof(config));
        }

        foreach (string id in config.RegularRoster)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("RegularRoster entries must be non-blank ids.", nameof(config));
            }
        }

        if (config.EliteLineEnabled && string.IsNullOrWhiteSpace(config.EliteEnemyId))
        {
            throw new ArgumentException("EliteEnemyId is required when the elite line is enabled.", nameof(config));
        }

        if (config.TickRateHz <= 0)
        {
            throw new ArgumentException($"TickRateHz must be positive (got {config.TickRateHz}).", nameof(config));
        }

        if (config.CellSizePx <= 0)
        {
            throw new ArgumentException($"CellSizePx must be positive (got {config.CellSizePx}).", nameof(config));
        }

        if (config.BatchesPerWave < 1)
        {
            throw new ArgumentException(
                $"BatchesPerWave must be at least 1 (got {config.BatchesPerWave}).", nameof(config));
        }

        if (config.BossHpMultiplier <= 0 || config.BossDamageMultiplier <= 0)
        {
            throw new ArgumentException("Boss multipliers must be positive.", nameof(config));
        }

        if (config.FirstWaveGraceMultiplier <= 0)
        {
            throw new ArgumentException(
                $"FirstWaveGraceMultiplier must be positive (got {config.FirstWaveGraceMultiplier}).",
                nameof(config));
        }

        if (config.ValleySeconds < 0)
        {
            throw new ArgumentException(
                $"ValleySeconds must be non-negative (got {config.ValleySeconds}).", nameof(config));
        }

        if (config.ThreatLevel < 0)
        {
            throw new ArgumentException(
                $"ThreatLevel must be non-negative (got {config.ThreatLevel}).", nameof(config));
        }

        if (config.EntryCells is null || config.EntryCells.Count == 0)
        {
            throw new ArgumentException("EntryCells must not be empty.", nameof(config));
        }

        foreach (CellCoord cell in config.EntryCells)
        {
            ArgumentNullException.ThrowIfNull(cell);
        }

        ThreatMultipliers multipliers = Threat.Multipliers(config.ThreatLevel);
        _threatCountMultiplier = multipliers.Count;
        _threatHpMultiplier = multipliers.Hp;
        _intervalTicks = (long)Math.Round(
            Campaign.WaveIntervalSeconds(config.ThreatLevel) * config.TickRateHz,
            MidpointRounding.AwayFromZero);
        _graceTicks = (long)Math.Round(_intervalTicks * config.FirstWaveGraceMultiplier, MidpointRounding.AwayFromZero);
        _valleyTicks = (long)Math.Round(config.ValleySeconds * config.TickRateHz, MidpointRounding.AwayFromZero);
        if (_valleyTicks >= _intervalTicks)
        {
            throw new ArgumentException(
                $"ValleySeconds ({config.ValleySeconds}) must leave entry room inside the wave interval " +
                $"({_intervalTicks} ticks).",
                nameof(config));
        }

        _entryCells = config.EntryCells;
        _catalog = catalog;
        _cfg = config;
        _rng = rng;
        _clock = clock;
    }

    /// <summary>Threat count multiplier resolved from the config (T-036).</summary>
    public double ThreatCountMultiplier => _threatCountMultiplier;

    /// <summary>Threat hp multiplier resolved from the config (T-036).</summary>
    public double ThreatHpMultiplier => _threatHpMultiplier;

    /// <summary>Ticks between wave starts (T-036 Campaign, F-034).</summary>
    public long IntervalTicks => _intervalTicks;

    /// <summary>Ticks of grace before wave 1 (F-034).</summary>
    public long GraceTicks => _graceTicks;

    /// <summary>The installed plan, or null before the first wave.</summary>
    public WavePlan? CurrentPlan => _plan;

    /// <summary>Wave number of the installed plan; 0 before the first wave.</summary>
    public int CurrentWave => _plan?.Wave ?? 0;

    /// <summary>
    /// Absolute start tick of a wave in the autonomous schedule: wave 1
    /// begins after the grace, later waves one interval apart.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">wave is not positive.</exception>
    public long WaveStartTick(int wave)
    {
        if (wave < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(wave), wave, "Wave numbers are 1-based.");
        }

        return wave == 1 ? _graceTicks : _graceTicks + ((wave - 1) * _intervalTicks);
    }

    /// <summary>
    /// Pure schedule computation for one wave anchored at
    /// <paramref name="startTick"/>. Regular counts come from
    /// <see cref="WaveFormula.Count"/> (F-030), elite counts from
    /// <see cref="Elite.Count"/> (F-031); batches are spread evenly over
    /// (interval − valley) so the whole wave is always entered before the
    /// next one (T-039 acceptance: 第 N 波全部入场时间 ≤ 波间隔). Boss waves
    /// spawn the boss alone at offset 0 ([NEW-E1 薄版]: escort composition is
    /// content-data territory, T-049).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">wave &lt; 1 or startTick &lt; 0.</exception>
    public WavePlan PlanWave(int wave, long startTick)
    {
        if (wave < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(wave), wave, "Wave numbers are 1-based.");
        }

        if (startTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startTick), startTick, "Ticks are non-negative.");
        }

        int regular = WaveFormula.Count(
            wave, _cfg.BaseCount, _cfg.BeginWave, _cfg.Spacing, _cfg.Scaling, _threatCountMultiplier);
        int elite = _cfg.EliteLineEnabled && _cfg.EliteEnemyId is not null ? Elite.Count(wave) : 0;
        bool boss = _cfg.BossEnemyId is not null && wave == _cfg.BossWave;

        var batches = new List<SpawnBatch>();
        if (boss)
        {
            batches.Add(new SpawnBatch(0, 1, IsBoss: true, IsElite: false));
        }
        else
        {
            int batchCount = Math.Clamp(_cfg.BatchesPerWave, 1, Math.Max(1, regular + elite));
            long entryWindow = _intervalTicks - _valleyTicks;
            long spacingTicks = batchCount > 1 ? entryWindow / (batchCount - 1) : 0;
            int[] regularSplit = SplitEvenly(regular, batchCount);
            int[] eliteSplit = SplitEvenly(elite, batchCount);
            for (int i = 0; i < batchCount; i++)
            {
                long offset = i * spacingTicks;
                if (regularSplit[i] > 0)
                {
                    batches.Add(new SpawnBatch(offset, regularSplit[i], IsBoss: false, IsElite: false));
                }

                if (eliteSplit[i] > 0)
                {
                    batches.Add(new SpawnBatch(offset, eliteSplit[i], IsBoss: false, IsElite: true));
                }
            }
        }

        return new WavePlan(wave, startTick, _intervalTicks, batches, regular, elite, boss);
    }

    /// <summary>
    /// Installs a plan for <paramref name="wave"/> starting at
    /// <paramref name="startTick"/> and resets the materialization cursor —
    /// the manual-control entry point (integration harnesses, boss-wave
    /// drills). Returns the installed plan.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">wave &lt; 1 or startTick &lt; 0.</exception>
    public WavePlan BeginWave(int wave, long startTick)
    {
        _plan = PlanWave(wave, startTick);
        _nextBatchIndex = 0;
        return _plan;
    }

    /// <summary>
    /// Materializes every batch of the installed plan whose entry time has
    /// come by <paramref name="tick"/>. Each unit draws one entry cell from
    /// the injected <see cref="IRng"/>; regular species cycle the roster by
    /// spawn order. Batches are consumed in plan order, whole.
    /// </summary>
    /// <exception cref="InvalidOperationException">No wave was ever begun.</exception>
    public IReadOnlyList<Unit> Poll(long tick)
    {
        var spawned = new List<Unit>();
        WavePlan? plan = _plan;
        if (plan is null)
        {
            throw new InvalidOperationException("No wave begun; call BeginWave or Advance first.");
        }

        while (_nextBatchIndex < plan.Batches.Count
            && tick >= plan.StartTick + plan.Batches[_nextBatchIndex].TickOffset)
        {
            SpawnBatch batch = plan.Batches[_nextBatchIndex++];
            for (int i = 0; i < batch.Count; i++)
            {
                spawned.Add(CreateUnit(batch, tick));
            }
        }

        return spawned;
    }

    /// <summary>
    /// One autonomous tick (IClock-driven): begins waves on schedule (wave 1
    /// after the grace, then every interval) and polls the installed plan.
    /// Returns the units materialized this tick.
    /// </summary>
    public IReadOnlyList<Unit> Advance()
    {
        long tick = _clock.Tick;
        if (_plan is null)
        {
            BeginWave(1, WaveStartTick(1));
        }

        List<Unit> spawned = new(Poll(tick));
        if (tick >= _plan!.StartTick + _plan.IntervalTicks)
        {
            // Interval elapsed: begin the next wave (its offset-0 batch is
            // immediately due) and poll again.
            BeginWave(_plan.Wave + 1, _plan.StartTick + _plan.IntervalTicks);
            spawned.AddRange(Poll(tick));
        }

        return spawned;
    }

    private Unit CreateUnit(SpawnBatch batch, long tick)
    {
        string enemyId = batch.IsBoss
            ? _cfg.BossEnemyId!
            : batch.IsElite
                ? _cfg.EliteEnemyId!
                : _cfg.RegularRoster[(int)(_spawnCounter++ % _cfg.RegularRoster.Count)];
        EnemySpec spec = _catalog.Get(enemyId);
        CellCoord entry = _entryCells[_rng.Next(0, _entryCells.Count)];
        float half = _cfg.CellSizePx / 2f;
        double hpMultiplier = _threatHpMultiplier * (batch.IsBoss ? _cfg.BossHpMultiplier : 1.0);
        var unit = new Unit(
            new EntityId(++_nextEntityId),
            spec.Id,
            entry.X * _cfg.CellSizePx + half,
            entry.Y * _cfg.CellSizePx + half,
            (int)Math.Ceiling(spec.Hp * hpMultiplier),
            spec.Armor,
            (float)spec.SpeedPxPerTick,
            spec.IsBoss || batch.IsBoss,
            batch.IsElite);
        unit.MarkSpawned(tick);
        if (spec.TrampleRadiusCells > 0 && _cfg.Trample is not null)
        {
            double damageMultiplier = batch.IsBoss ? _cfg.BossDamageMultiplier : 1.0;
            unit.SetTrample(
                (int)Math.Ceiling(_cfg.Trample.Damage * damageMultiplier),
                (float)(spec.TrampleRadiusCells * _cfg.CellSizePx),
                _cfg.Trample.PeriodTicks);
        }

        return unit;
    }

    /// <summary>
    /// Deterministic even split: the first <c>total % parts</c> groups carry
    /// one extra unit; sum equals <paramref name="total"/> exactly.
    /// </summary>
    private static int[] SplitEvenly(int total, int parts)
    {
        int[] result = new int[parts];
        if (total <= 0)
        {
            return result;
        }

        int baseShare = total / parts;
        int remainder = total % parts;
        for (int i = 0; i < parts; i++)
        {
            result[i] = baseShare + (i < remainder ? 1 : 0);
        }

        return result;
    }
}
