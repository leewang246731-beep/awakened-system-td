using System.Text.Json;
using EraCore.Balance;
using Xunit;

namespace EraCore.Tests.Balance;

// Golden regression first cut (T-035): every committed row of
// game/content/golden/wave-e2-golden.json is recomputed from the T-036
// formula library using the file's own header parameters — any drift in
// formulas, parameters or the golden file itself turns this red.
// Golden content is regenerated only via
// `python tools/ci/gen_wave_golden.py --update-golden` (CS-6.1).
// [F-171] wave golden regression (C-29)
public class WaveE2GoldenTableTests
{
    private sealed record GoldenSpec(
        double Threat,
        int Base,
        int Begin,
        int Spacing,
        double Scaling,
        int ShieldStartWave,
        double IntervalBaseSeconds,
        int IntervalRounding,
        List<JsonElement> Rows);

    private static JsonDocument LoadGoldenDoc()
    {
        // Repo-relative probing (RL9: no absolute paths), same pattern as
        // Era1ContentTests: walk up from the test assembly to game/content/golden.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "golden");
            if (Directory.Exists(probe))
            {
                var path = Path.Combine(probe, "wave-e2-golden.json");
                Assert.True(File.Exists(path), "金样文件缺失：game/content/golden/wave-e2-golden.json");
                return JsonDocument.Parse(File.ReadAllText(path));
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到 game/content/golden（测试须在仓库内运行）");
    }

    private static GoldenSpec LoadSpec()
    {
        JsonElement root = LoadGoldenDoc().RootElement;

        Assert.True(root.TryGetProperty("params", out var p), "金样缺 params 节");
        Assert.True(root.TryGetProperty("rows", out var rows), "金样缺 rows 节");

        return new GoldenSpec(
            Threat: root.GetProperty("threat").GetDouble(),
            Base: p.GetProperty("base").GetInt32(),
            Begin: p.GetProperty("begin").GetInt32(),
            Spacing: p.GetProperty("spacing").GetInt32(),
            Scaling: p.GetProperty("scaling").GetDouble(),
            ShieldStartWave: root.GetProperty("shield_start_wave").GetInt32(),
            IntervalBaseSeconds: root.GetProperty("interval_base_s").GetDouble(),
            IntervalRounding: root.GetProperty("interval_s_rounding").GetInt32(),
            Rows: rows.EnumerateArray().ToList());
    }

    [Fact] // legal — header carries the T-035 calibration set
    public void Header_pins_the_T035_calibration()
    {
        GoldenSpec spec = LoadSpec();

        Assert.Equal(0.75, spec.Threat, precision: 9); // F.5 wave-5 benchmark threat
        Assert.Equal(10, spec.Base); // [NEW-原型校准] calibrated so wave 5 = F.5 anchor 6
        Assert.Equal(5, spec.Begin);
        Assert.Equal(5, spec.Spacing);
        Assert.Equal(2.0, spec.Scaling, precision: 9);
        Assert.Equal(31, spec.ShieldStartWave); // 护盾 E3 起: E2 rows stay at 0
        Assert.Equal(120.0, spec.IntervalBaseSeconds, precision: 9); // v3.2 §6 波间隔 120s×乘子
    }

    [Fact] // legal — 30 rows, waves 1..30 exactly once
    public void Rows_cover_waves_one_to_thirty_exactly_once()
    {
        List<JsonElement> rows = LoadSpec().Rows;

        Assert.Equal(30, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(i + 1, rows[i].GetProperty("wave").GetInt32());
        }
    }

    [Fact] // legal — every row recomputed from the T-036 formulas (drift = red)
    public void Every_row_matches_the_T036_formulas()
    {
        GoldenSpec spec = LoadSpec();
        ThreatMultipliers m = Threat.Multipliers(spec.Threat);

        foreach (JsonElement row in spec.Rows)
        {
            int wave = row.GetProperty("wave").GetInt32();

            Assert.Equal(
                WaveFormula.Count(wave, spec.Base, spec.Begin, spec.Spacing, spec.Scaling, m.Count),
                row.GetProperty("count").GetInt32());

            Assert.Equal(Elite.Count(wave), row.GetProperty("elite").GetInt32());

            Assert.Equal(
                Shield.At(wave, spec.ShieldStartWave),
                row.GetProperty("shield").GetInt32());

            // ToEven mirrors python round() so the generator and this test agree
            double expectedInterval = Math.Round(
                spec.IntervalBaseSeconds * m.Interval,
                spec.IntervalRounding,
                MidpointRounding.ToEven);
            Assert.Equal(expectedInterval, row.GetProperty("interval_s").GetDouble(), precision: 6);
        }
    }

    [Fact] // legal — the four F.5 anchor waves are pinned by name for loud failures
    public void F5_anchor_rows_are_pinned()
    {
        GoldenSpec spec = LoadSpec();

        JsonElement Row(int wave) => spec.Rows[wave - 1];

        Assert.Equal(6, Row(5).GetProperty("count").GetInt32()); // F.5: 第 5 波 6 散兵
        Assert.Equal(1, Row(8).GetProperty("elite").GetInt32()); // first elite wave
        Assert.Equal(4, Row(20).GetProperty("elite").GetInt32()); // F.5: 精英线 4 攻城锤
        Assert.Equal(6, Row(30).GetProperty("elite").GetInt32()); // F.5 wave-30 gate loadout
        Assert.Equal(9, Row(30).GetProperty("count").GetInt32());
        Assert.All(spec.Rows, r => Assert.Equal(0, r.GetProperty("shield").GetInt32())); // E2 无护盾

        // interval column is constant at t=0.75: 120×1.3 = 156 s (附录 F.2)
        Assert.Equal(156.0, Row(1).GetProperty("interval_s").GetDouble(), precision: 6);
        Assert.Equal(156.0, Row(30).GetProperty("interval_s").GetDouble(), precision: 6);
    }
}
