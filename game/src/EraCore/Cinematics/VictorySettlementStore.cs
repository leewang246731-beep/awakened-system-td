using System.Text.Json;
using System.Text.Json.Serialization;
using EraCore.Agent.Governance;
using EraCore.IO;
using EraCore.Progression;

namespace EraCore.Cinematics;

/// <summary>
/// One persisted settlement record (T-087 结算状态入存档): the decided
/// performance of one level's latest settlement + its viewed flag. Durable
/// facts only — playback state stays with the timeline. 重进不重播：the
/// live victory moment launches the settlement; a persisted record NEVER
/// auto-replays, it only feeds the 可回看 channel (same decided bytes).
/// </summary>
public sealed record VictorySettlementRecord
{
    /// <summary>Campaign level the settlement belongs to (latest per level wins).</summary>
    public required string LevelId { get; init; }

    /// <summary>The decided-before-shown performance (immutable snapshot).</summary>
    public required DecidedVictoryPerformance Decided { get; init; }

    /// <summary>Whether the performance completed at least once (回看 re-arms nothing; replays stay explicit).</summary>
    public required bool Viewed { get; init; }
}

/// <summary>Root shape of the settlement archive file (one record per level id, latest wins).</summary>
public sealed record VictorySettlementArchive
{
    /// <summary>Records in insertion order (an upsert of the same level keeps the tail position).</summary>
    public required IReadOnlyList<VictorySettlementRecord> Records { get; init; }

    /// <summary>Factory state: nothing settled yet.</summary>
    public static VictorySettlementArchive Default { get; } = new() { Records = Array.Empty<VictorySettlementRecord>() };
}

/// <summary>
/// System.Text.Json source-generation context for the settlement archive
/// (AOT red line 07 D-05, mirrors <see cref="CinematicsJsonContext"/>).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(VictorySettlementArchive))]
[JsonSerializable(typeof(VictorySettlementRecord))]
[JsonSerializable(typeof(DecidedVictoryPerformance))]
[JsonSerializable(typeof(DecidedWheel))]
[JsonSerializable(typeof(DecidedCommentary))]
[JsonSerializable(typeof(VictoryVerdictRow))]
[JsonSerializable(typeof(VictoryWheelSegment))]
[JsonSerializable(typeof(GrantedChestItem))]
[JsonSerializable(typeof(ObjectiveTier))]
[JsonSerializable(typeof(TrustBadge))]
[JsonSerializable(typeof(SystemPersona))]
[JsonSerializable(typeof(RewardRarity))]
public sealed partial class VictorySettlementJsonContext : JsonSerializerContext
{
}

/// <summary>
/// D-03 store binding of the decided settlements (RL12's durable side): the
/// decided record is saved the moment the settlement starts — BEFORE the
/// performance — so quitting mid-performance leaves the rewards intact and
/// byte-stable; loading is tolerant (missing/corrupt file falls back to
/// <see cref="VictorySettlementArchive.Default"/>: a settlement marker must
/// never block startup).
/// </summary>
public static class VictorySettlementStore
{
    /// <summary>Archive file path relative to the injected data root.</summary>
    public const string RelativePath = "cinematics/victory-settlements.json";

    /// <summary>Loads the archive; missing/unparsable/member-incomplete ⇒ factory default.</summary>
    /// <exception cref="ArgumentNullException">store is null.</exception>
    public static VictorySettlementArchive Load(IFileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Exists(RelativePath))
        {
            return VictorySettlementArchive.Default;
        }

        try
        {
            byte[] bytes = store.ReadAllBytes(RelativePath);
            VictorySettlementArchive? parsed = JsonSerializer.Deserialize(
                bytes, VictorySettlementJsonContext.Default.VictorySettlementArchive);
            return parsed ?? VictorySettlementArchive.Default;
        }
        catch (JsonException)
        {
            return VictorySettlementArchive.Default; // corrupt archive — replays nothing, never blocks startup
        }
    }

    /// <summary>Round-trips the archive through the source-generated codec.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void Save(IFileStore store, VictorySettlementArchive archive)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(archive);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            archive, VictorySettlementJsonContext.Default.VictorySettlementArchive);
        store.WriteAllBytes(RelativePath, bytes);
    }

    /// <summary>
    /// Inserts or replaces the record of its level id (the LATEST settlement
    /// of a level is the reviewable one — a re-won level overwrites with its
    /// fresh decision; the position keeps the first-insert order).
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">record.LevelId blank.</exception>
    public static VictorySettlementArchive Upsert(VictorySettlementArchive archive, VictorySettlementRecord record)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.LevelId))
        {
            throw new ArgumentException("record.LevelId must not be blank.", nameof(record));
        }

        var records = new List<VictorySettlementRecord>(archive.Records.Count + 1);
        bool replaced = false;
        foreach (VictorySettlementRecord existing in archive.Records)
        {
            if (string.Equals(existing.LevelId, record.LevelId, StringComparison.Ordinal))
            {
                if (!replaced)
                {
                    records.Add(record);
                    replaced = true;
                }

                continue; // duplicate rows of one level collapse onto the latest
            }

            records.Add(existing);
        }

        if (!replaced)
        {
            records.Add(record);
        }

        return new VictorySettlementArchive { Records = records };
    }

    /// <summary>The latest settled record of one level, when any.</summary>
    /// <exception cref="ArgumentNullException">archive null, or levelId null/blank.</exception>
    public static VictorySettlementRecord? LatestFor(VictorySettlementArchive archive, string levelId)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (string.IsNullOrWhiteSpace(levelId))
        {
            throw new ArgumentException("levelId must not be blank.", nameof(levelId));
        }

        VictorySettlementRecord? latest = null;
        foreach (VictorySettlementRecord record in archive.Records)
        {
            if (string.Equals(record.LevelId, levelId, StringComparison.Ordinal))
            {
                latest = record;
            }
        }

        return latest;
    }

    /// <summary>Returns the record with the viewed flag set (idempotent value copy).</summary>
    /// <exception cref="ArgumentNullException">record is null.</exception>
    public static VictorySettlementRecord MarkViewed(VictorySettlementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record with { Viewed = true };
    }

    /// <summary>
    /// Canonical bytes of one decided performance — the RL12 byte-equality
    /// seam: the three playback paths (full / accelerated / quit-restore)
    /// must serialize to identical bytes.
    /// </summary>
    /// <exception cref="ArgumentNullException">decided is null.</exception>
    public static byte[] Encode(DecidedVictoryPerformance decided)
    {
        ArgumentNullException.ThrowIfNull(decided);
        return JsonSerializer.SerializeToUtf8Bytes(
            decided, VictorySettlementJsonContext.Default.DecidedVictoryPerformance);
    }

    /// <summary>Decodes canonical bytes back into a decided performance (review/replay path).</summary>
    /// <exception cref="ArgumentNullException">bytes is null.</exception>
    /// <exception cref="JsonException">Payload is not a decided performance.</exception>
    public static DecidedVictoryPerformance Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return JsonSerializer.Deserialize(bytes, VictorySettlementJsonContext.Default.DecidedVictoryPerformance)
            ?? throw new JsonException("decided performance deserialized to null.");
    }
}
