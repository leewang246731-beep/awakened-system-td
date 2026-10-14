using EraCore.Audio;
using EraCore.Narrative;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Tests of the persona timbre table and voice-token renderer (T-092,
/// F-137): strict parsing of the closed id set, four-persona separability
/// through real signal measures (per-syllable zero-crossing rates), the
/// narrator/off-mode downgrade policy, determinism, and the guards.
/// </summary>
// [C-25][F-137] 军师人格语音音色：参数表+合成+中性旁白降级
public class PersonaVoiceTests
{
    private const string LegalJson = """
        {
          "content_type": "PersonaTimbreTable",
          "sample_rate_hz": 16000,
          "peak_amplitude": 0.6,
          "timbres": [
            { "id": "advisor",  "zh": "参谋", "base_freq_hz": 440.0, "contour": "steady",  "wave": "sine",     "syllable_ms": 90,  "gap_ms": 45, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.0,   "gain": 0.5 },
            { "id": "snarky",   "zh": "毒舌", "base_freq_hz": 660.0, "contour": "sharp",   "wave": "square",   "syllable_ms": 60,  "gap_ms": 25, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.06,  "gain": 0.28 },
            { "id": "goofy",    "zh": "沙雕", "base_freq_hz": 392.0, "contour": "bounce",  "wave": "triangle", "syllable_ms": 70,  "gap_ms": 35, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.335, "gain": 0.5 },
            { "id": "tsundere", "zh": "傲娇", "base_freq_hz": 494.0, "contour": "wobble",  "wave": "sine",     "syllable_ms": 100, "gap_ms": 40, "vibrato_hz": 6.0, "vibrato_depth": 0.04, "step_ratio": 1.0,   "gain": 0.5 },
            { "id": "narrator", "zh": "旁白", "base_freq_hz": 262.0, "contour": "steady",  "wave": "sine",     "syllable_ms": 140, "gap_ms": 60, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.0,   "gain": 0.45 }
          ]
        }
        """;

    private static PersonaTimbreTable Table() => PersonaTimbreTable.Parse(LegalJson);

    private static byte[] Render(PersonaTimbre timbre, string key) =>
        PersonaVoiceSynth.SynthesizePcm(timbre, key, 16000, 0.6);

    private static short[] Samples(byte[] pcm)
    {
        short[] samples = new short[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(pcm[2 * i] | (pcm[(2 * i) + 1] << 8));
        }

        return samples;
    }

    /// <summary>Zero-crossing rate of one syllable segment — the cheap pitch meter these tests lean on.</summary>
    private static double Zcr(short[] s, int start, int count)
    {
        int changes = 0;
        for (int i = start + 1; i < start + count; i++)
        {
            if ((s[i - 1] < 0 && s[i] >= 0) || (s[i - 1] >= 0 && s[i] < 0))
            {
                changes++;
            }
        }

        return changes / (count / (double)16000); // crossings per second
    }

    private static (int Start, int Length) SyllableWindow(PersonaTimbre timbre, string key, int index)
    {
        int syllableSamples = (int)Math.Round(timbre.SyllableSec * 16000);
        int gapSamples = (int)Math.Round(timbre.GapSec * 16000);
        return (index * (syllableSamples + gapSamples), syllableSamples);
    }

    // ---------------------------------------------------------------- legal

    [Fact] // legal — the closed five-row set parses and maps the persona enum
    public void Legal_table_parses()
    {
        PersonaTimbreTable table = Table();

        Assert.Equal(5, table.Timbres.Count);
        Assert.NotNull(table.Find("narrator"));
        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            Assert.NotNull(table.Of(persona));
        }

        Assert.Equal("参谋", table.Of(SystemPersona.Advisor).Zh);
        Assert.Same(table.Find("narrator"), table.Narrator);
    }

    [Fact] // legal — the four personas render four byte-distinct tokens on one line
    public void Personas_are_byte_distinct()
    {
        PersonaTimbreTable table = Table();

        var distinct = Enum.GetValues<SystemPersona>()
            .Select(persona => Convert.ToBase64String(Render(table.Of(persona), "e1.end.handover")))
            .Distinct()
            .Count();

        Assert.Equal(4, distinct);
    }

    [Fact] // legal — 参谋 steady: every syllable carries the same pitch
    public void Advisor_contour_is_steady()
    {
        PersonaTimbre advisor = Table().Of(SystemPersona.Advisor);
        short[] s = Samples(Render(advisor, "e1.end.handover"));
        (int start, int length) = SyllableWindow(advisor, "e1.end.handover", 0);
        double first = Zcr(s, start, length);
        (int start2, int length2) = SyllableWindow(advisor, "e1.end.handover", 1);
        double second = Zcr(s, start2, length2);

        Assert.Equal(first, second, 1); // same frequency ⇒ same crossing rate
    }

    [Fact] // legal — 毒舌 sharp: pitch climbs syllable over syllable
    public void Snarky_contour_climbs()
    {
        PersonaTimbre snarky = Table().Of(SystemPersona.Snarky);
        string key = "e1.end.handover";
        short[] s = Samples(Render(snarky, key));
        int syllables = PersonaVoiceSynth.SyllableCount(key);

        (int firstStart, int firstLen) = SyllableWindow(snarky, key, 0);
        (int lastStart, int lastLen) = SyllableWindow(snarky, key, syllables - 1);
        double first = Zcr(s, firstStart, firstLen);
        double last = Zcr(s, lastStart, lastLen);

        Assert.True(last > first * 1.1, $"last {last:0} vs first {first:0} — sharp contour must climb");
    }

    [Fact] // legal — 沙雕 bounce: odd syllables jump a fourth over the even ones
    public void Goofy_contour_bounces()
    {
        PersonaTimbre goofy = Table().Of(SystemPersona.Goofy);
        string key = "e1.end.handover";
        short[] s = Samples(Render(goofy, key));

        (int evenStart, int evenLen) = SyllableWindow(goofy, key, 0);
        (int oddStart, int oddLen) = SyllableWindow(goofy, key, 1);
        double evenHz = Zcr(s, evenStart, evenLen) / 2.0;
        double oddHz = Zcr(s, oddStart, oddLen) / 2.0;

        // step_ratio 1.335 ≈ perfect fourth; ZCR tolerance is coarse — ±20%
        Assert.InRange(oddHz / evenHz, 1.335 * 0.8, 1.335 * 1.2);
    }

    [Fact] // legal — the neutral narrator is distinct from every persona timbre
    public void Narrator_is_distinct_from_personas()
    {
        PersonaTimbreTable table = Table();
        byte[] narrator = Render(table.Narrator, "e1.end.handover");

        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            Assert.False(
                Render(table.Of(persona), "e1.end.handover").SequenceEqual(narrator),
                $"{persona} token equals the narrator token");
        }
    }

    [Fact] // legal — tokens are audible, in range, click-free at onset
    public void Tokens_are_audible_and_clickfree()
    {
        foreach (PersonaTimbre timbre in Table().Timbres)
        {
            short[] s = Samples(Render(timbre, "e1.open.narrator"));

            short peak = 0;
            foreach (short v in s)
            {
                Assert.True(Math.Abs(v) <= 32767);
                peak = Math.Max(peak, (short)Math.Abs(v));
            }

            Assert.True(peak > 32767 * 0.1, $"{timbre.Id}: peak {peak} too quiet");
            Assert.Equal(0, s[0]); // attack ramp starts at digital silence
        }
    }

    [Theory] // legal — the off-mode downgrade: system off ⇒ narrator row, on ⇒ the persona row
    [InlineData(SystemPersona.Snarky)]
    [InlineData(SystemPersona.Goofy)]
    [InlineData(SystemPersona.Tsundere)]
    [InlineData(SystemPersona.Advisor)]
    public void Offmode_downgrade_resolves(SystemPersona persona)
    {
        Assert.Equal(PersonaTimbreTable.NarratorId, VoiceDowngrade.Resolve(persona, systemEnabled: false));
        Assert.Equal(Table().Of(persona).Id, VoiceDowngrade.Resolve(persona, systemEnabled: true));
    }

    // -------------------------------------------------------------- boundary

    [Theory] // boundary — syllable counts stay inside the frozen 3~6 window
    [InlineData("")]
    [InlineData("a")]
    [InlineData("e1.open.narrator")]
    [InlineData("sys-alarm-integrity")]
    [InlineData("mock.e1.taunt.w3")]
    public void Syllable_count_stays_bounded(string key)
    {
        if (key.Length == 0)
        {
            Assert.Throws<ArgumentException>(() => PersonaVoiceSynth.SyllableCount(key));
            return;
        }

        int count = PersonaVoiceSynth.SyllableCount(key);

        Assert.InRange(count, PersonaVoiceSynth.MinSyllables, PersonaVoiceSynth.MaxSyllables);
        Assert.Equal(count, PersonaVoiceSynth.SyllableCount(key)); // deterministic per key
    }

    [Fact] // boundary — rendering is deterministic: same timbre+key ⇒ identical bytes
    public void Synthesis_is_deterministic()
    {
        PersonaTimbre timbre = Table().Of(SystemPersona.Tsundere);

        Assert.True(
            Render(timbre, "e1.open.narrator").SequenceEqual(Render(timbre, "e1.open.narrator")));
        Assert.False(
            Render(timbre, "e1.open.narrator").SequenceEqual(Render(timbre, "e1.end.handover"))); // key drives the token
    }

    [Fact] // boundary — WAV images wrap the PCM payload at the table rate
    public void Wav_image_wraps_payload()
    {
        PersonaTimbre timbre = Table().Of(SystemPersona.Advisor);
        byte[] pcm = Render(timbre, "e1.end.handover");
        byte[] wav = PersonaVoiceSynth.SynthesizeWav(timbre, "e1.end.handover", 16000, 0.6);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.True(wav.AsSpan(44).SequenceEqual(pcm));
    }

    // -------------------------------------------------------------- illegal

    [Theory] // illegal — schema violations: marker, ranges, closed enum names
    [InlineData("\"content_type\": \"WrongMarker\"")]
    [InlineData("\"syllable_ms\": 30")]
    [InlineData("\"base_freq_hz\": 1200")]
    [InlineData("\"contour\": \"zigzag\"")]
    [InlineData("\"wave\": \"sawtooth\"")]
    [InlineData("\"gain\": 1.5")]
    public void Illegal_schema_rejected(string mutation)
    {
        string json = MutateAdvisorRow(LegalJson, mutation);

        Assert.Throws<EraCore.Audio.AudioSchemaException>(() => PersonaTimbreTable.Parse(json));
    }

    /// <summary>Applies one mutation to the advisor row (first timbre) of the legal fixture.</summary>
    private static string MutateAdvisorRow(string json, string mutation)
    {
        string key = mutation.Split(':')[0].Trim().Trim('"');
        string original = key switch
        {
            "content_type" => "\"content_type\": \"PersonaTimbreTable\"",
            "syllable_ms" => "\"syllable_ms\": 90",
            "base_freq_hz" => "\"base_freq_hz\": 440.0",
            "contour" => "\"contour\": \"steady\"",
            "wave" => "\"wave\": \"sine\"",
            "gain" => "\"gain\": 0.5",
            _ => throw new InvalidOperationException($"unhandled mutation key {key}"),
        };

        return json.Replace(original, mutation);
    }

    [Fact] // illegal — an id outside the closed set (4 personas + narrator)
    public void Illegal_id_rejected()
    {
        string json = LegalJson.Replace("\"id\": \"narrator\"", "\"id\": \"pirate\"");

        Assert.Throws<EraCore.Audio.AudioSchemaException>(() => PersonaTimbreTable.Parse(json));
    }

    [Fact] // illegal — row count is frozen at 5 with unique ids, which structurally
            // forces the narrator row (renaming it trips the closed-set guard)
    public void Narrator_row_cannot_be_replaced()
    {
        string json = LegalJson.Replace("\"id\": \"narrator\"", "\"id\": \"tsundere\"");

        Assert.Throws<EraCore.Audio.AudioSchemaException>(() => PersonaTimbreTable.Parse(json));
    }

    [Fact] // illegal — wrong row count
    public void Illegal_row_count_rejected()
    {
        int insertAt = LegalJson.LastIndexOf(']'); // closing bracket of the timbres array
        string json = LegalJson.Insert(insertAt,
            ",{ \"id\": \"extra\", \"zh\": \"额外\", \"base_freq_hz\": 300.0, \"contour\": \"steady\", " +
            "\"wave\": \"sine\", \"syllable_ms\": 90, \"gap_ms\": 40, \"vibrato_hz\": 0.0, \"vibrato_depth\": 0.0, \"step_ratio\": 1.0, \"gain\": 0.5 }");

        Assert.Throws<EraCore.Audio.AudioSchemaException>(() => PersonaTimbreTable.Parse(json));
    }

    [Fact] // illegal — null timbre / blank key are rejected by the renderer
    public void Null_arguments_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => PersonaVoiceSynth.SynthesizePcm(null!, "key", 16000, 0.6));
        Assert.Throws<ArgumentException>(
            () => PersonaVoiceSynth.SynthesizePcm(Table().Narrator, "  ", 16000, 0.6));
    }
}
