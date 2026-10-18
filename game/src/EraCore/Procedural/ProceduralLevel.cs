using System.Text;
using System.Text.Json;
using EraCore.Contracts.Foundation;

namespace EraCore.Procedural;

/// <summary>
/// Fixed map parameters of one generated level (T-140) — the same face as
/// the handcrafted <c>map</c> block of <c>game/content/levels/{era}.json</c>
/// (T-085 schema): seed + dimensions + terrain ratios + base/warehouse/
/// entry cells. The seed pins the whole terrain draw, so the row is a
/// pure function of it (同 seed 同图).
/// </summary>
public sealed record ProceduralMap(
    int Seed,
    int Width,
    int Height,
    double ForestRatio,
    double RockRatio,
    double WaterRatio,
    CellCoord BaseCell,
    IReadOnlyList<CellCoord> EntryCells,
    CellCoord WarehouseCell);

/// <summary>
/// One command-script step of a generated level — the closed E1 verb
/// vocabulary the handcrafted rows use (build×4 + assign-carrier), wave-
/// anchored like the T-085 teaching scripts (wave k steps execute in the
/// valley before wave k; wave 1 inside the first-wave grace).
/// </summary>
public sealed record ProceduralScriptStep(
    int Wave,
    string Verb,
    CellCoord? Cell,
    string? Tower,
    string? Resource,
    int? Seats,
    long? FromEntity,
    long? ToEntity,
    string? CarrierTier);

/// <summary>Initial-grant line of a generated level (送资源行).</summary>
public sealed record ProceduralStockLine(string Resource, double Amount);

/// <summary>
/// Wave-table block of a generated level — the §6 参数表 knobs plus the
/// roster and the final-wave boss row (终局带档：该波只出 Boss 批次).
/// </summary>
public sealed record ProceduralWaveTable(
    int BaseCount,
    int BeginWave,
    int Spacing,
    double Scaling,
    int BatchesPerWave,
    double ValleySeconds,
    IReadOnlyList<string> RegularRoster,
    bool EliteLineEnabled,
    string? EliteEnemyId,
    int? BossWave,
    string? BossEnemyId);

/// <summary>
/// One generated procedural level (T-140, F-007): isomorphic to a
/// handcrafted level row — the JSON face (<see cref="ToLevelJson"/>)
/// loads through the EXISTING handcrafted pipeline unchanged (schema
/// urn:eratd:content:levels:1; the T-085 loader's structural rules all
/// hold by construction). <see cref="TerrainHash"/> pins the drawn grid
/// (逐格 identity — the GridWorld FNV over dimensions + terrain), and
/// <see cref="IdentityHash"/> pins the WHOLE level payload including the
/// reroll count (重摇次数入哈希). Pure data — regeneration replay equals
/// field equality plus hash equality.
/// </summary>
public sealed record ProceduralLevel(
    string Id,
    string Era,
    int Slot,
    string NameZh,
    string NameEn,
    double Threat,
    int WinWaves,
    double GraceMultiplier,
    int BudgetSeconds,
    int AdjacentEnemyCamps,
    ProceduralMap Map,
    IReadOnlyList<ProceduralStockLine> InitialStock,
    ProceduralWaveTable Waves,
    IReadOnlyList<ProceduralScriptStep> CommandScript,
    int Rerolls,
    long TerrainHash)
{
    private const ulong FnvOffset = 14695981039346656037UL;

    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Strategy objective ids (SP-8a pool references) of the objectives block.</summary>
    public IReadOnlyList<string> StrategyObjectiveIds { get; init; } = Array.Empty<string>();

    /// <summary>Challenge objective ids (SP-8b pool references) of the objectives block.</summary>
    public IReadOnlyList<string> ChallengeObjectiveIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// FNV-1a 64 over the whole level payload — every field, every script
    /// step, the map block and the reroll count. Two generations with the
    /// same seed and inputs produce equal hashes; any field drift (including
    /// a different reroll path onto a coincidentally similar map) shows.
    /// </summary>
    public long IdentityHash()
    {
        ulong hash = FnvOffset;
        hash = Mix(hash, Id);
        hash = Mix(hash, Era);
        hash = Mix(hash, (ulong)Slot);
        hash = Mix(hash, (ulong)Threat.GetHashCode());
        hash = Mix(hash, (ulong)WinWaves);
        hash = Mix(hash, (ulong)GraceMultiplier.GetHashCode());
        hash = Mix(hash, (ulong)BudgetSeconds);
        hash = Mix(hash, (ulong)AdjacentEnemyCamps);
        hash = Mix(hash, (ulong)Map.Seed);
        hash = Mix(hash, (ulong)Map.Width);
        hash = Mix(hash, (ulong)Map.Height);
        hash = Mix(hash, (ulong)Map.ForestRatio.GetHashCode());
        hash = Mix(hash, (ulong)Map.RockRatio.GetHashCode());
        hash = Mix(hash, (ulong)Map.WaterRatio.GetHashCode());
        hash = Mix(hash, (ulong)Map.BaseCell.X);
        hash = Mix(hash, (ulong)Map.BaseCell.Y);
        hash = Mix(hash, (ulong)Map.WarehouseCell.X);
        hash = Mix(hash, (ulong)Map.WarehouseCell.Y);
        foreach (CellCoord entry in Map.EntryCells)
        {
            hash = Mix(hash, (ulong)entry.X);
            hash = Mix(hash, (ulong)entry.Y);
        }

        foreach (ProceduralStockLine line in InitialStock)
        {
            hash = Mix(hash, line.Resource);
            hash = Mix(hash, (ulong)line.Amount.GetHashCode());
        }

        hash = Mix(hash, (ulong)Waves.BaseCount);
        hash = Mix(hash, (ulong)Waves.BeginWave);
        hash = Mix(hash, (ulong)Waves.Spacing);
        hash = Mix(hash, (ulong)Waves.Scaling.GetHashCode());
        hash = Mix(hash, (ulong)Waves.BatchesPerWave);
        hash = Mix(hash, (ulong)Waves.ValleySeconds.GetHashCode());
        foreach (string enemy in Waves.RegularRoster)
        {
            hash = Mix(hash, enemy);
        }

        hash = Mix(hash, Waves.BossWave is null ? 0UL : (ulong)Waves.BossWave.Value);
        hash = Mix(hash, Waves.BossEnemyId ?? string.Empty);
        foreach (ProceduralScriptStep step in CommandScript)
        {
            hash = Mix(hash, (ulong)step.Wave);
            hash = Mix(hash, step.Verb);
            hash = Mix(hash, step.Cell is null ? 0UL : (ulong)step.Cell.X);
            hash = Mix(hash, step.Cell is null ? 0UL : (ulong)step.Cell.Y);
            hash = Mix(hash, step.Tower ?? string.Empty);
            hash = Mix(hash, step.Resource ?? string.Empty);
            hash = Mix(hash, step.Seats is null ? 0UL : (ulong)step.Seats.Value);
            hash = Mix(hash, step.FromEntity is null ? 0UL : (ulong)step.FromEntity.Value);
            hash = Mix(hash, step.ToEntity is null ? 0UL : (ulong)step.ToEntity.Value);
        }

        hash = Mix(hash, (ulong)Rerolls);
        hash ^= (ulong)TerrainHash;
        hash *= FnvPrime;
        return unchecked((long)hash);
    }

    /// <summary>
    /// Serializes the level into a single-item handcrafted-level document
    /// (schema <c>urn:eratd:content:levels:1</c>) — the interchange face the
    /// existing loading pipeline consumes. Number formatting is
    /// round-trippable (R-format for doubles), so equal levels serialize to
    /// byte-equal strings.
    /// </summary>
    public string ToLevelJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", "urn:eratd:content:levels:1");
            writer.WriteString("content_type", "Level");
            writer.WriteString("era", Era);
            writer.WriteStartArray("items");
            WriteLevelRow(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteLevelRow(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("id", Id);
        writer.WriteString("content_type", "Level");
        writer.WriteString("era", Era);
        writer.WriteNumber("slot", Slot);
        writer.WriteString("name_zh", NameZh);
        writer.WriteString("name_en", NameEn);
        writer.WriteBoolean("teaching_level", false);
        writer.WriteBoolean("awakening_cinematic", false);
        writer.WriteNumber("threat", Threat);
        writer.WriteNumber("win_waves", WinWaves);
        writer.WriteNumber("first_wave_grace_multiplier", GraceMultiplier);
        writer.WriteNumber("budget_seconds", BudgetSeconds);
        writer.WriteNumber("adjacent_enemy_camps", AdjacentEnemyCamps);
        writer.WriteNumber("_rerolls", Rerolls); // 注解键（'_' 前缀装载器忽略）：重摇次数入产物留痕
        writer.WriteNumber("_terrain_hash", TerrainHash); // 注解键：逐格地形哈希（同 seed 同图证据链）

        writer.WriteStartObject("map");
        writer.WriteNumber("seed", Map.Seed);
        writer.WriteNumber("width", Map.Width);
        writer.WriteNumber("height", Map.Height);
        writer.WriteNumber("forest_ratio", Map.ForestRatio);
        writer.WriteNumber("rock_ratio", Map.RockRatio);
        writer.WriteNumber("water_ratio", Map.WaterRatio);
        WriteCell(writer, "base_cell", Map.BaseCell);
        WriteCell(writer, "warehouse_cell", Map.WarehouseCell);
        writer.WriteStartArray("entry_cells");
        foreach (CellCoord entry in Map.EntryCells)
        {
            WriteCellObject(writer, entry);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteStartArray("initial_stock");
        foreach (ProceduralStockLine line in InitialStock)
        {
            writer.WriteStartObject();
            writer.WriteString("resource", line.Resource);
            writer.WriteNumber("amount", line.Amount);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartObject("waves");
        writer.WriteNumber("base_count", Waves.BaseCount);
        writer.WriteNumber("begin_wave", Waves.BeginWave);
        writer.WriteNumber("spacing", Waves.Spacing);
        writer.WriteNumber("scaling", Waves.Scaling);
        writer.WriteNumber("batches_per_wave", Waves.BatchesPerWave);
        writer.WriteNumber("valley_seconds", Waves.ValleySeconds);
        writer.WriteStartArray("regular_roster");
        foreach (string enemy in Waves.RegularRoster)
        {
            writer.WriteStringValue(enemy);
        }

        writer.WriteEndArray();
        writer.WriteBoolean("elite_line_enabled", Waves.EliteLineEnabled);
        if (Waves.BossWave is not null)
        {
            writer.WriteNumber("boss_wave", Waves.BossWave.Value);
            writer.WriteString("boss_enemy_id", Waves.BossEnemyId);
        }

        writer.WriteEndObject();

        writer.WriteStartArray("command_script");
        foreach (ProceduralScriptStep step in CommandScript)
        {
            WriteStep(writer, step);
        }

        writer.WriteEndArray();

        writer.WriteStartObject("objectives");
        WriteIdArray(writer, "strategy", StrategyObjectiveIds);
        WriteIdArray(writer, "challenge", ChallengeObjectiveIds);
        writer.WriteEndObject();

        writer.WriteStartObject("unlocks");
        writer.WriteStartArray("actions");
        writer.WriteEndArray();
        writer.WriteStartArray("features");
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteStep(Utf8JsonWriter writer, ProceduralScriptStep step)
    {
        writer.WriteStartObject();
        writer.WriteNumber("wave", step.Wave);
        writer.WriteString("verb", step.Verb);
        if (step.Cell is not null)
        {
            WriteCell(writer, "cell", step.Cell);
        }

        if (step.Tower is not null)
        {
            writer.WriteString("tower", step.Tower);
        }

        if (step.Resource is not null)
        {
            writer.WriteString("resource", step.Resource);
        }

        if (step.Seats is not null)
        {
            writer.WriteNumber("seats", step.Seats.Value);
        }

        if (step.FromEntity is not null)
        {
            writer.WriteNumber("from_entity", step.FromEntity.Value);
        }

        if (step.ToEntity is not null)
        {
            writer.WriteNumber("to_entity", step.ToEntity.Value);
        }

        if (step.CarrierTier is not null)
        {
            writer.WriteString("carrier_tier", step.CarrierTier);
        }

        writer.WriteEndObject();
    }

    private static void WriteCell(Utf8JsonWriter writer, string name, CellCoord cell)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("x", cell.X);
        writer.WriteNumber("y", cell.Y);
        writer.WriteEndObject();
    }

    private static void WriteCellObject(Utf8JsonWriter writer, CellCoord cell)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", cell.X);
        writer.WriteNumber("y", cell.Y);
        writer.WriteEndObject();
    }

    private static void WriteIdArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> ids)
    {
        writer.WriteStartArray(name);
        foreach (string id in ids)
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
    }

    private static ulong Mix(ulong hash, string text)
    {
        foreach (char c in text)
        {
            hash ^= c;
            hash *= FnvPrime;
        }

        return hash;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        unchecked
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                hash ^= (value >> shift) & 0xFFUL;
                hash *= FnvPrime;
            }

            return hash;
        }
    }
}
