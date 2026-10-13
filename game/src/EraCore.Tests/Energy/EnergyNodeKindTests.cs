using System.Text.Json;
using EraCore.Energy;
using Xunit;

namespace EraCore.Tests.Energy;

// E1 energy node taxonomy and anchor table (T-033): producer kinds pinned to
// game/content/era1/era.json energy.levels, ladder ×4 asserted inside the
// v3.2 §4 L65 band [3,5] — against BOTH the engine constants and the JSON
// itself, so the two cannot drift apart.
// [C-08][F-009]
public class EnergyNodeKindTests
{
    // ---- taxonomy ----------------------------------------------------------

    [Theory] // legal — producer/consumer split of the E1 set
    [InlineData(EnergyNodeKind.LaborCamp, true)]
    [InlineData(EnergyNodeKind.BeastPen, true)]
    [InlineData(EnergyNodeKind.Facility, false)]
    public void IsProducer_matches_the_E1_taxonomy(EnergyNodeKind kind, bool expected)
    {
        Assert.Equal(expected, E1EnergyAnchors.IsProducer(kind));
    }

    [Theory] // legal — anchor outputs per producer kind (era1.json energy.levels)
    [InlineData(EnergyNodeKind.LaborCamp, 10.0)]
    [InlineData(EnergyNodeKind.BeastPen, 40.0)]
    public void OutputPerSecond_returns_anchor_outputs(EnergyNodeKind kind, double expected)
    {
        Assert.Equal(expected, E1EnergyAnchors.OutputPerSecond(kind));
    }

    [Theory] // illegal — non-producer or undefined kinds carry no anchor
    [InlineData(EnergyNodeKind.Facility)]
    [InlineData((EnergyNodeKind)99)]
    [InlineData((EnergyNodeKind)0)]
    public void OutputPerSecond_rejects_non_producer_kinds(EnergyNodeKind kind)
    {
        Assert.Throws<ArgumentException>(() => E1EnergyAnchors.OutputPerSecond(kind));
    }

    // ---- ladder band -------------------------------------------------------

    [Fact] // boundary — E1 tier growth is exactly ×4 (10 → 40), a legal value
    public void L1_to_L2_multiplier_is_exactly_four()
    {
        Assert.Equal(4.0, E1EnergyAnchors.L1ToL2Multiplier);
    }

    [Fact] // legal — ×4 lies inside the v3.2 §4 L65 band [3,5] (T-033 验收)
    public void Ladder_multiplier_lies_inside_the_v32_band()
    {
        double multiplier = E1EnergyAnchors.L1ToL2Multiplier;
        Assert.InRange(multiplier, E1EnergyAnchors.LadderMultiplierMin, E1EnergyAnchors.LadderMultiplierMax);

        // The shipped anchors satisfy the runtime band check the ledger ctor runs.
        E1EnergyAnchors.EnsureLadderInBand();
        Assert.Equal(4.0, multiplier); // unchanged by the check: still the shipped ×4
    }

    // ---- era1.json anchor cross-check --------------------------------------

    [Fact] // legal — engine constants equal era1.json energy.levels outputs (F-009)
    public void Engine_anchor_constants_match_era1_json()
    {
        JsonElement energy = LoadEra1Energy();
        var levels = energy.GetProperty("levels");
        Assert.Equal(2, levels.GetArrayLength());

        double l1 = levels[0].GetProperty("output").GetDouble();
        double l2 = levels[1].GetProperty("output").GetDouble();

        // JSON is the anchor source; constants are the expected side here
        // (xUnit2000: constant first).
        Assert.Equal(E1EnergyAnchors.LaborCampOutputPerSecond, l1);
        Assert.Equal(E1EnergyAnchors.BeastPenOutputPerSecond, l2);
    }

    [Fact] // legal — the [3,5] band equals era1.json ladder_multiplier_min/max (F-009)
    public void Engine_ladder_band_matches_era1_json()
    {
        JsonElement energy = LoadEra1Energy();

        Assert.Equal(
            E1EnergyAnchors.LadderMultiplierMin,
            energy.GetProperty("ladder_multiplier_min").GetDouble());
        Assert.Equal(
            E1EnergyAnchors.LadderMultiplierMax,
            energy.GetProperty("ladder_multiplier_max").GetDouble());
    }

    [Fact] // legal — JSON-derived tier growth ×4 stays inside the JSON band (spec-level check)
    public void Era1_json_ladder_growth_is_inside_its_own_band()
    {
        JsonElement energy = LoadEra1Energy();
        var levels = energy.GetProperty("levels");

        double growth = levels[1].GetProperty("output").GetDouble()
            / levels[0].GetProperty("output").GetDouble();

        Assert.Equal(4.0, growth);
        Assert.InRange(
            growth,
            energy.GetProperty("ladder_multiplier_min").GetDouble(),
            energy.GetProperty("ladder_multiplier_max").GetDouble());
    }

    /// <summary>
    /// Loads the single era item's <c>energy</c> object from
    /// game/content/era1/era.json (repo-relative walk, same convention as
    /// Era1ContentTests — RL9: no absolute paths).
    /// </summary>
    private static JsonElement LoadEra1Energy()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "era1", "era.json");
            if (File.Exists(probe))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(probe));
                // Clone keeps the element valid after the document is disposed.
                JsonElement item = doc.RootElement.GetProperty("items")[0];
                return item.GetProperty("energy").Clone();
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/era1/era.json（测试须在仓库内运行）");
    }
}
