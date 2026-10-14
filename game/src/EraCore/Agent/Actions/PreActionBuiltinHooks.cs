using System.Text;
using System.Text.Json;
using EraCore.Contracts.Actions;
using EraCore.Contracts.Foundation;

namespace EraCore.Agent.Actions;

/// <summary>
/// Built-in PreAction hook #1 (T-070, F-075): placement-conflict rewrite
/// (the placement-conflict rewrite of the T-070 card) — a BuildPlan whose target cell is
/// OCCUPIED (a living building sits on it) is automatically offset by
/// exactly 1 cell (+1 on x) when that offset cell is buildable, and the
/// rewrite is recorded as a notice (提示). One single offset step, never a
/// cascade: if the offset target is itself unbuildable the cell stays
/// as-is and the honest downstream rejection reports it (the C-13
/// self-correction loop then takes over). The explicit demolition path
/// (confirm_overwrite=true) is NEVER rewritten — the player-flagged
/// conversion of occupied cells into demolition steps is the deliberate
/// route. Requires a world view; without one the hook passes everything
/// through.
/// </summary>
public sealed class BuildPlacementConflictHook : IPreActionHook
{
    /// <summary>Offset applied to a conflicting cell: exactly 1 cell on x (F.4 自动偏移 1 格).</summary>
    public const int OffsetX = 1;

    private const string OccupiedRejection = "cell-occupied";

    /// <inheritdoc />
    public string Id => "placement-conflict";

    /// <inheritdoc />
    public PreActionHookVerdict Apply(PreActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Verb != SystemActionKind.BuildPlan || context.WorldView is null)
        {
            return PreActionHookVerdict.Pass();
        }

        try
        {
            using var document = JsonDocument.Parse(context.ActionJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cells", out JsonElement cells)
                || cells.ValueKind != JsonValueKind.Array)
            {
                return PreActionHookVerdict.Pass();
            }

            if (root.TryGetProperty("confirm_overwrite", out JsonElement overwrite)
                && overwrite.ValueKind == JsonValueKind.True)
            {
                return PreActionHookVerdict.Pass(); // explicit demolition path — never rewritten
            }

            var shifts = new List<(int Index, int FromX, int FromY, int ToX, int ToY)>();
            int index = 0;
            foreach (JsonElement cell in cells.EnumerateArray())
            {
                if (TryReadCell(cell, out int x, out int y)
                    && !context.WorldView.IsBuildable(new CellCoord(x, y), out string? rejection)
                    && rejection == OccupiedRejection)
                {
                    var offset = new CellCoord(x + OffsetX, y);
                    if (context.WorldView.IsBuildable(offset, out _))
                    {
                        shifts.Add((index, x, y, offset.X, offset.Y));
                    }
                }

                index++;
            }

            if (shifts.Count == 0)
            {
                return PreActionHookVerdict.Pass();
            }

            string rewritten = RewriteCells(root, cells, shifts);
            string note = string.Join(
                ";", shifts.Select(s => $"cell({s.FromX},{s.FromY})→({s.ToX},{s.ToY}):occupied:auto-offset+1"));
            return PreActionHookVerdict.Rewrite(rewritten, note);
        }
        catch (JsonException)
        {
            return PreActionHookVerdict.Pass(); // malformed payload — the schema gate reports it
        }
    }

    private static bool TryReadCell(JsonElement cell, out int x, out int y)
    {
        x = 0;
        y = 0;
        return cell.ValueKind == JsonValueKind.Object
            && cell.TryGetProperty("x", out JsonElement xElement) && xElement.TryGetInt32(out x)
            && cell.TryGetProperty("y", out JsonElement yElement) && yElement.TryGetInt32(out y);
    }

    private static string RewriteCells(
        JsonElement root,
        JsonElement cells,
        IReadOnlyList<(int Index, int FromX, int FromY, int ToX, int ToY)> shifts)
    {
        // The payload already passed the closed-schema gate: keys are
        // exactly action/tower_id/cells[/confirm_overwrite] — rebuild in
        // that order with the shifted coordinates.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("action", root.GetProperty("action").GetString());
            writer.WriteString("tower_id", root.GetProperty("tower_id").GetString());
            writer.WriteStartArray("cells");
            int shiftAt = 0;
            int index = 0;
            foreach (JsonElement cell in cells.EnumerateArray())
            {
                if (shiftAt < shifts.Count && shifts[shiftAt].Index == index)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("x", shifts[shiftAt].ToX);
                    writer.WriteNumber("y", shifts[shiftAt].ToY);
                    writer.WriteEndObject();
                    shiftAt++;
                }
                else
                {
                    cell.WriteTo(writer);
                }

                index++;
            }

            writer.WriteEndArray();
            if (root.TryGetProperty("confirm_overwrite", out JsonElement overwrite))
            {
                writer.WriteBoolean("confirm_overwrite", overwrite.ValueKind == JsonValueKind.True);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>
/// Built-in PreAction hook #2 (T-070, F-075): 纪念馆保护 — building on a
/// memorial/ruins cell is BLOCKED outright (遗迹格建造拦截; F.4 危险位
/// family: 遗迹与纪念馆拆除永不自动允许 — construction is denied before it
/// can even ask). The protected-cell probe is injected: pass a predicate
/// over the live world (the ruins/memorial registry provides it); the
/// default (null) protects nothing — E1 has no memorials yet, and the
/// wiring lands with the ruins feature (C-20). Applies to BuildPlan only.
/// </summary>
public sealed class MemorialProtectionHook : IPreActionHook
{
    private readonly Func<CellCoord, bool> _isProtectedCell;

    /// <param name="isProtectedCell">
    /// Probe answering "is this cell a protected memorial/ruins cell";
    /// null protects nothing (until the ruins feature wires its registry).
    /// </param>
    public MemorialProtectionHook(Func<CellCoord, bool>? isProtectedCell = null)
    {
        _isProtectedCell = isProtectedCell ?? (static _ => false);
    }

    /// <inheritdoc />
    public string Id => "memorial-protection";

    /// <inheritdoc />
    public PreActionHookVerdict Apply(PreActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Verb != SystemActionKind.BuildPlan)
        {
            return PreActionHookVerdict.Pass();
        }

        try
        {
            using var document = JsonDocument.Parse(context.ActionJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cells", out JsonElement cells)
                || cells.ValueKind != JsonValueKind.Array)
            {
                return PreActionHookVerdict.Pass();
            }

            foreach (JsonElement cell in cells.EnumerateArray())
            {
                if (cell.ValueKind == JsonValueKind.Object
                    && cell.TryGetProperty("x", out JsonElement xElement) && xElement.TryGetInt32(out int x)
                    && cell.TryGetProperty("y", out JsonElement yElement) && yElement.TryGetInt32(out int y)
                    && _isProtectedCell(new CellCoord(x, y)))
                {
                    return PreActionHookVerdict.Block(
                        $"build-on-protected-cell:({x},{y}):纪念馆/遗迹格受保护（F.4 遗迹格建造拦截）");
                }
            }

            return PreActionHookVerdict.Pass();
        }
        catch (JsonException)
        {
            return PreActionHookVerdict.Pass(); // malformed payload — the schema gate reports it
        }
    }
}
