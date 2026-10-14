using EraCore.Audio;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Tests of the strict BGM program-table parser (T-092, F-135): the legal
/// E1/E2 rows, and every schema violation the loader must reject. Signal
/// behavior of the rendered loops lives in <see cref="BgmSynthTests"/>.
/// </summary>
// [C-25][F-135] 三态 BGM 参数表：严格解析（合法/边界/非法）
public class BgmProgramTests
{
    private const string LegalJson = """
        {
          "content_type": "BgmProgramTable",
          "sample_rate_hz": 22050,
          "loop_bars": 2,
          "beats_per_bar": 4,
          "programs": [
            { "era": "era1", "tempo_bpm": 84, "root_hz": 110.0,
              "chord_intervals": [1.0, 1.5, 2.0],
              "pad_gain": 0.3, "percussion_gain": 0.2, "bass_gain": 0.25, "hat_subdivisions": 2 },
            { "era": "era2", "tempo_bpm": 100, "root_hz": 146.83,
              "chord_intervals": [1.0, 1.26, 2.0],
              "pad_gain": 0.3, "percussion_gain": 0.2, "bass_gain": 0.25, "hat_subdivisions": 4 }
          ]
        }
        """;

    // ---------------------------------------------------------------- legal

    [Fact] // legal — the two E1/E2 rows parse with derived timing math
    public void Legal_table_parses_with_derived_timing()
    {
        BgmProgramTable table = BgmProgramTable.Parse(LegalJson);

        Assert.Equal(2, table.Programs.Count);
        Assert.Equal(22050, table.SampleRateHz);

        BgmProgram era1 = table.Find("era1")!;
        Assert.NotNull(era1);
        Assert.Equal(60.0 / 84.0, era1.BeatSec, 9);   // tempo math is derived, not stored
        Assert.Equal(2 * 4 * (60.0 / 84.0), era1.LoopSec, 9);
        Assert.Equal((int)Math.Round(era1.LoopSec * 22050), era1.LoopSamples);
        Assert.Equal((int)Math.Round(era1.LoopSec * 22050) * 2, BgmSynth.SynthesizePcm(era1, BgmLayerState.Calm).Length);

        BgmProgram era2 = table.Find("era2")!;
        Assert.True(era2.TempoBpm > era1.TempoBpm);

        Assert.Null(table.Find("era9")); // closed set — unknown era stays null, no throw
    }

    [Theory] // legal — the three states are cumulative layers (calm ⊂ combat ⊂ boss)
    [InlineData(BgmLayerState.Calm, false, false)]
    [InlineData(BgmLayerState.Combat, true, false)]
    [InlineData(BgmLayerState.Boss, true, true)]
    public void State_layering_is_cumulative(BgmLayerState state, bool percussion, bool bass)
    {
        BgmProgram program = BgmProgramTable.Parse(LegalJson).Find("era1")!;

        Assert.Equal(percussion, program.HasPercussion(state));
        Assert.Equal(bass, program.HasBass(state));
    }

    // -------------------------------------------------------------- boundary

    [Theory] // boundary — global scalars sit exactly on their accepted bounds
    [InlineData("\"sample_rate_hz\": 8000", 8000)]
    [InlineData("\"sample_rate_hz\": 48000", 48000)]
    [InlineData("\"loop_bars\": 1", 1)]
    [InlineData("\"loop_bars\": 16", 16)]
    [InlineData("\"beats_per_bar\": 2", 2)]
    [InlineData("\"beats_per_bar\": 8", 8)]
    public void Boundary_globals_parse(string mutation, int expected)
    {
        string json = ApplyMutation(LegalJson, mutation);

        BgmProgramTable table = BgmProgramTable.Parse(json);

        string key = mutation.Split(':')[0].Trim().Trim('"');
        switch (key)
        {
            case "sample_rate_hz":
                Assert.Equal(expected, table.SampleRateHz);
                break;
            case "loop_bars":
                Assert.Equal(expected, table.LoopBars);
                break;
            case "beats_per_bar":
                Assert.Equal(expected, table.BeatsPerBar);
                break;
            default:
                throw new InvalidOperationException($"unhandled mutation key {key}");
        }
    }

    private static string ApplyMutation(string json, string mutation)
    {
        string key = mutation.Split(':')[0].Trim().Trim('"');
        return json switch
        {
            _ when key == "sample_rate_hz" => json.Replace("\"sample_rate_hz\": 22050", mutation),
            _ when key == "loop_bars" => json.Replace("\"loop_bars\": 2", mutation),
            _ when key == "beats_per_bar" => json.Replace("\"beats_per_bar\": 4", mutation),
            _ => throw new InvalidOperationException($"unhandled mutation key {key}"),
        };
    }

    [Fact] // boundary — chord may span 1.0~2.5 with first tone exactly at the root
    public void Boundary_chord_interval_accepted()
    {
        string json = LegalJson.Replace("[1.0, 1.5, 2.0]", "[1.0, 2.5]");

        BgmProgram program = BgmProgramTable.Parse(json).Find("era1")!;

        Assert.Equal(new[] { 1.0, 2.5 }, program.ChordIntervals);
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — wrong content marker
    public void Illegal_marker_rejected()
    {
        string json = LegalJson.Replace("BgmProgramTable", "WrongMarker");

        Assert.Throws<EraCore.Audio.AudioSchemaException>(() => BgmProgramTable.Parse(json));
    }

    [Fact] // illegal — duplicate era id
    public void Illegal_duplicate_era_rejected()
    {
        string json = LegalJson.Replace("\"era2\"", "\"era1\"");

        Assert.Throws<EraCore.Audio.AudioSchemaException>(() => BgmProgramTable.Parse(json));
    }

    [Theory] // illegal — scalars one step outside every bound
    [InlineData("\"tempo_bpm\": 39.9", "\"tempo_bpm\": 84")]
    [InlineData("\"tempo_bpm\": 200.1", "\"tempo_bpm\": 84")]
    [InlineData("\"root_hz\": 54.9", "\"root_hz\": 110.0")]
    [InlineData("\"root_hz\": 440.1", "\"root_hz\": 110.0")]
    [InlineData("\"pad_gain\": 0", "\"pad_gain\": 0.3")]
    [InlineData("\"percussion_gain\": 1.01", "\"percussion_gain\": 0.2")]
    [InlineData("\"hat_subdivisions\": 9", "\"hat_subdivisions\": 2")]
    public void Illegal_ranges_rejected(string mutation, string original)
    {
        string json = LegalJson.Replace(original, mutation);

        Assert.Throws<EraCore.Audio.AudioSchemaException>(() => BgmProgramTable.Parse(json));
    }

    [Theory] // illegal — chord shape rules: not rooted at 1.0 / not ascending / too few tones
    [InlineData("[1.01, 1.5, 2.0]")]
    [InlineData("[1.0, 1.5, 1.5]")]
    [InlineData("[1.0]")]
    public void Illegal_chords_rejected(string chord)
    {
        string json = LegalJson.Replace("[1.0, 1.5, 2.0]", chord);

        Assert.Throws<EraCore.Audio.AudioSchemaException>(() => BgmProgramTable.Parse(json));
    }

    [Fact] // illegal — program count must stay the frozen 2
    public void Illegal_program_count_rejected()
    {
        int insertAt = LegalJson.LastIndexOf(']'); // closing bracket of the programs array
        string json = LegalJson.Insert(insertAt,
            ",{ \"era\": \"era3\", \"tempo_bpm\": 90, \"root_hz\": 120.0, \"chord_intervals\": [1.0, 1.5], " +
            "\"pad_gain\": 0.3, \"percussion_gain\": 0.2, \"bass_gain\": 0.25, \"hat_subdivisions\": 2 }");

        Assert.Throws<EraCore.Audio.AudioSchemaException>(() => BgmProgramTable.Parse(json));
    }

    [Theory] // illegal — blank/whitespace inputs
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    public void Illegal_input_rejected(string json)
    {
        Assert.ThrowsAny<Exception>(() => BgmProgramTable.Parse(json));
    }
}
