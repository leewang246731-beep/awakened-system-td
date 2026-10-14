using EraCore.Agent.Autopilot;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Persistence;
using EraCore.Endless;
using EraCore.Persistence;

namespace EraCore.Tests.Endless;

/// <summary>
/// Shared fixture of the T-080 endless test suite: the cross-era pool
/// factory (weights chosen so the 混编 share mapping is exact), the run
/// config factory (era1 wave shape, t=0.9), an in-memory K-7 port mirroring
/// <see cref="SqliteSavePort"/>'s no-document behaviour, and the recording
/// T-081 sink double. No file IO, no workstation paths (CS-4.1).
/// </summary>
internal static class EndlessFixture
{
    /// <summary>Test seed family (same value family as the campaign fixtures).</summary>
    public const long Seed = 20261080;

    /// <summary>Threat level of the standard test config (t=0.9: 数量乘子 1.5, 间隔乘子 ≈1.0667).</summary>
    public const double Threat = 0.9;

    /// <summary>Era1 wave shape of the standard test config (基数/起/间隔/成长).</summary>
    public static (int BaseCount, int Begin, int Spacing, double Scaling) Era1Shape => (6, 1, 3, 1.0);

    /// <summary>
    /// The standard mixed pool: 2×E1 regular + 1×E2 regular + 1×E1 elite +
    /// 1×E2 elite + 1×E1 boss + 1×E3 boss — three eras, all three roles.
    /// Regular sub-pool weights 2:1 ⇒ the E1 share of regular draws is
    /// exactly 2/3, E2 1/3.
    /// </summary>
    public static EndlessEnemyPool MixedPool() => new(new[]
    {
        new EndlessEnemyPoolEntry("era1:enemy-skirmisher", 1, EndlessEnemyRole.Regular, 2.0),
        new EndlessEnemyPoolEntry("era2:enemy-warrior", 2, EndlessEnemyRole.Regular, 1.0),
        new EndlessEnemyPoolEntry("era1:enemy-warband", 1, EndlessEnemyRole.Elite, 1.0),
        new EndlessEnemyPoolEntry("era2:enemy-centurion", 2, EndlessEnemyRole.Elite, 1.0),
        new EndlessEnemyPoolEntry("era1:enemy-boss-trampler", 1, EndlessEnemyRole.Boss, 1.0),
        new EndlessEnemyPoolEntry("era3:enemy-boss-liberator", 3, EndlessEnemyRole.Boss, 1.0),
    });

    /// <summary>The standard validated run config (era1, t=0.9, default tunings).</summary>
    public static EndlessRunConfig Config(
        long seed = Seed,
        EndlessEnemyPool? pool = null,
        IReadOnlyList<string>? wonders = null) =>
        new(
            seed,
            startEraIndex: 1,
            threatLevel: Threat,
            waveBaseCount: Era1Shape.BaseCount,
            waveBegin: Era1Shape.Begin,
            waveSpacing: Era1Shape.Spacing,
            waveScaling: Era1Shape.Scaling,
            bossEveryWaves: 10,
            pool ?? MixedPool(),
            EndlessAffixTuning.Default,
            EndlessScoreWeights.Default,
            wonders ?? Array.Empty<string>());

    /// <summary>Runs <paramref name="waves"/> full cycles (plan → kill everything → complete).</summary>
    public static void PlayWaves(EndlessRun run, int waves)
    {
        for (int i = 0; i < waves; i++)
        {
            EndlessWavePlan plan = run.BeginWave();
            run.CompleteWave(plan.EliteCount, plan.BossCount);
        }
    }
}

/// <summary>
/// In-memory K-7 port of the endless tests: keeps the last campaign
/// document and the level slots in memory; <see cref="ReadCampaign"/>
/// mirrors <see cref="SqliteSavePort"/>'s no-document failure so the
/// fresh-profile path is exercised honestly (the port is the seam, not the
/// system under test — CS-3.4).
/// </summary>
internal sealed class InMemorySavePort : ISavePort
{
    private ICampaignMeta? _campaign;
    private readonly Dictionary<string, ILevelSaveBlob> _levels = new(StringComparer.Ordinal);

    /// <summary>Campaign writes observed so far.</summary>
    public int CampaignWrites { get; private set; }

    public void WriteCampaign(ICampaignMeta meta, int formatVersion)
    {
        ArgumentNullException.ThrowIfNull(meta);
        CampaignWrites++;
        _campaign = meta;
    }

    public ICampaignMeta ReadCampaign(int formatVersion) =>
        _campaign ?? throw new InvalidOperationException("No campaign state has been saved in this store yet.");

    public void WriteLevel(string levelSlotId, ILevelSaveBlob blob, int formatVersion) =>
        _levels[levelSlotId] = blob ?? throw new ArgumentNullException(nameof(blob));

    public ILevelSaveBlob ReadLevel(string levelSlotId, int formatVersion) =>
        _levels.TryGetValue(levelSlotId, out ILevelSaveBlob? blob)
            ? blob
            : throw new InvalidOperationException($"No level slot '{levelSlotId}' has been saved in this store yet.");
}

/// <summary>
/// Recording T-081 sink double: captures every
/// <see cref="AutopilotScoreEntry"/> the endless settle path hands over.
/// </summary>
internal sealed class RecordingScoreSink : IAutopilotScoreSink
{
    private readonly List<AutopilotScoreEntry> _entries = new();

    public IReadOnlyList<AutopilotScoreEntry> Entries => _entries;

    public void Record(AutopilotScoreEntry entry) => _entries.Add(entry);
}
