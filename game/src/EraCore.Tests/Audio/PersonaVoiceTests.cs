using EraCore.Audio;
using EraCore.Narrative;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Tests of the persona timbre table and voice-token renderer (T-092
/// F-137; v2=T-126 era×persona full banks): strict parsing of the closed
/// era/persona sets (16 rows + narrator), sixteen-row separability through
/// real signal measures (per-syllable zero-crossing rates), per-persona
/// contour signatures that hold across all four eras, the narrator/off-mode
/// downgrade policy, determinism, and the guards.
/// </summary>
// [C-25][F-137] 军师人格语音音色（T-126 v2：时代×人格 16 行）：参数表+合成+降级
public class PersonaVoiceTests
{
    private const string LegalJson = """
        {
          "content_type": "PersonaTimbreTable",
          "sample_rate_hz": 16000,
          "peak_amplitude": 0.6,
          "timbres": [
            { "id": "advisor",  "era": "era1", "zh": "参谋", "base_freq_hz": 415.3, "contour": "steady",  "wave": "sine",     "syllable_ms": 95,  "gap_ms": 50, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.0,   "gain": 0.5 },
            { "id": "advisor",  "era": "era2", "zh": "参谋", "base_freq_hz": 440.0, "contour": "steady",  "wave": "sine",     "syllable_ms": 88,  "gap_ms": 44, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.0,   "gain": 0.5 },
            { "id": "advisor",  "era": "era3", "zh": "参谋", "base_freq_hz": 392.0, "contour": "steady",  "wave": "sine",     "syllable_ms": 82,  "gap_ms": 40, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.0,   "gain": 0.5 },
            { "id": "advisor",  "era": "era4", "zh": "参谋", "base_freq_hz": 466.2, "contour": "steady",  "wave": "sine",     "syllable_ms": 76,  "gap_ms": 36, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.0,   "gain": 0.5 },
            { "id": "snarky",   "era": "era1", "zh": "毒舌", "base_freq_hz": 622.3, "contour": "sharp",   "wave": "square",   "syllable_ms": 64,  "gap_ms": 28, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.06,  "gain": 0.30 },
            { "id": "snarky",   "era": "era2", "zh": "毒舌", "base_freq_hz": 660.0, "contour": "sharp",   "wave": "square",   "syllable_ms": 58,  "gap_ms": 25, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.06,  "gain": 0.30 },
            { "id": "snarky",   "era": "era3", "zh": "毒舌", "base_freq_hz": 587.3, "contour": "sharp",   "wave": "square",   "syllable_ms": 54,  "gap_ms": 22, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.06,  "gain": 0.30 },
            { "id": "snarky",   "era": "era4", "zh": "毒舌", "base_freq_hz": 740.0, "contour": "sharp",   "wave": "square",   "syllable_ms": 50,  "gap_ms": 20, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.06,  "gain": 0.30 },
            { "id": "goofy",    "era": "era1", "zh": "沙雕", "base_freq_hz": 349.2, "contour": "bounce",  "wave": "triangle", "syllable_ms": 76,  "gap_ms": 38, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.335, "gain": 0.5 },
            { "id": "goofy",    "era": "era2", "zh": "沙雕", "base_freq_hz": 392.0, "contour": "bounce",  "wave": "triangle", "syllable_ms": 70,  "gap_ms": 35, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.335, "gain": 0.5 },
            { "id": "goofy",    "era": "era3", "zh": "沙雕", "base_freq_hz": 369.9, "contour": "bounce",  "wave": "triangle", "syllable_ms": 64,  "gap_ms": 32, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.335, "gain": 0.48 },
            { "id": "goofy",    "era": "era4", "zh": "沙雕", "base_freq_hz": 440.0, "contour": "bounce",  "wave": "triangle", "syllable_ms": 58,  "gap_ms": 28, "vibrato_hz": 0.0, "vibrato_depth": 0.0,  "step_ratio": 1.335, "gain": 0.48 },
            { "id": "tsundere", "era": "era1", "zh": "傲娇", "base_freq_hz": 466.2, "contour": "wobble",  "wave": "sine",     "syllable_ms": 104, "gap_ms": 44, "vibrato_hz": 5.5, "vibrato_depth": 0.04,  "step_ratio": 1.0,   "gain": 0.5 },
            { "id": "tsundere", "era": "era2", "zh": "傲娇", "base_freq_hz": 494.0, "contour": "wobble",  "wave": "sine",     "syllable_ms": 98,  "gap_ms": 41, "vibrato_hz": 6.0, "vibrato_depth": 0.04,  "step_ratio": 1.0,   "gain": 0.5 },
            { "id": "tsundere", "era": "era3", "zh": "傲娇", "base_freq_hz": 440.0, "contour": "wobble",  "wave": "sine",     "syllable_ms": 92,  "gap_ms": 38, "vibrato_hz": 6.5, "vibrato_depth": 0.045, "step_ratio": 1.0,   "gain": 0.5 },
            { "id": "tsundere", "era": "era4", "zh": "傲娇", "base_freq_hz": 523.3, "contour": "wobble",  "wave": "sine",     "syllable_ms": 86,  "gap_ms": 34, "vibrato_hz": 7.0, "vibrato_depth": 0.05,  "step_ratio": 1.0,   "gain": 0.5 },
            { "id": "narrator", "zh": "旁白", "base_freq_hz": 262.0, "contour": "steady", "wave": "sine", "syllable_ms": 140, "gap_ms": 60, "vibrato_hz": 0.0, "vibrato_depth": 0.0, "step_ratio": 1.0, "gain": 0.45 }
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

    [Fact] // legal — v2 装载：16 时代×人格行+1 旁白行，(era,persona) 全对解析
    public void Legal_table_parses_the_sixteen_era_persona_rows()
    {
        PersonaTimbreTable table = Table();

        Assert.Equal(17, table.Timbres.Count);
        Assert.Equal(16, table.PersonaRows.Count);
        Assert.Equal(4, PersonaTimbreTable.Eras.Count);
        Assert.NotNull(table.Find("narrator"));
        Assert.NotNull(table.Find("era1:advisor"));
        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            foreach (string era in PersonaTimbreTable.Eras)
            {
                Assert.Equal($"{era}:{EraCore.Presentation.PersonaChoice.IdOf(persona)}", table.Of(persona, era).Id);
            }
        }

        Assert.Equal("参谋", table.Of(SystemPersona.Advisor, "era2").Zh);
        Assert.Same(table.Find("narrator"), table.Narrator);
    }

    [Fact] // legal — 16 人格行两两字节相异（同一行 key 上时代×人格全可分）
    public void All_sixteen_persona_rows_are_byte_distinct()
    {
        PersonaTimbreTable table = Table();

        var distinct = table.PersonaRows
            .Select(row => Convert.ToBase64String(Render(row, "e1.end.handover")))
            .Distinct()
            .Count();

        Assert.Equal(16, distinct);
        Assert.All(table.PersonaRows, row =>
        {
            bool audible = Render(row, "e1.end.handover").Any(b => b != 0);
            Assert.True(audible);
        });
    }

    [Fact] // legal — 时代身份：同人格跨四时代互异；人格身份：同时代跨四人格互异
    public void Era_and_persona_identities_both_hold()
    {
        PersonaTimbreTable table = Table();

        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            int eras = PersonaTimbreTable.Eras
                .Select(era => Convert.ToBase64String(Render(table.Of(persona, era), "e1.open.narrator")))
                .Distinct()
                .Count();
            Assert.Equal(4, eras); // 时代变体真实可闻，非复制行
        }

        foreach (string era in PersonaTimbreTable.Eras)
        {
            int personas = Enum.GetValues<SystemPersona>()
                .Select(persona => Convert.ToBase64String(Render(table.Of(persona, era), "e1.open.narrator")))
                .Distinct()
                .Count();
            Assert.Equal(4, personas);
        }
    }

    [Theory] // legal — 参谋 steady：每时代每音节同高（轮廓身份跨时代恒定）
    [InlineData("era1")]
    [InlineData("era2")]
    [InlineData("era3")]
    [InlineData("era4")]
    public void Advisor_contour_is_steady_in_every_era(string era)
    {
        PersonaTimbre advisor = Table().Of(SystemPersona.Advisor, era);
        short[] s = Samples(Render(advisor, "e1.end.handover"));
        (int start, int length) = SyllableWindow(advisor, "e1.end.handover", 0);
        double first = Zcr(s, start, length);
        (int start2, int length2) = SyllableWindow(advisor, "e1.end.handover", 1);
        double second = Zcr(s, start2, length2);

        Assert.Equal(first, second, 1); // same frequency ⇒ same crossing rate
    }

    [Theory] // legal — 毒舌 sharp：每时代音节逐个爬升
    [InlineData("era1")]
    [InlineData("era2")]
    [InlineData("era3")]
    [InlineData("era4")]
    public void Snarky_contour_climbs_in_every_era(string era)
    {
        PersonaTimbre snarky = Table().Of(SystemPersona.Snarky, era);
        string key = "e1.end.handover";
        short[] s = Samples(Render(snarky, key));
        int syllables = PersonaVoiceSynth.SyllableCount(key);

        (int firstStart, int firstLen) = SyllableWindow(snarky, key, 0);
        (int lastStart, int lastLen) = SyllableWindow(snarky, key, syllables - 1);
        double first = Zcr(s, firstStart, firstLen);
        double last = Zcr(s, lastStart, lastLen);

        Assert.True(last > first * 1.1, $"last {last:0} vs first {first:0} — sharp contour must climb ({era})");
    }

    [Theory] // legal — 沙雕 bounce：每时代奇数音节跳纯四度
    [InlineData("era1")]
    [InlineData("era2")]
    [InlineData("era3")]
    [InlineData("era4")]
    public void Goofy_contour_bounces_in_every_era(string era)
    {
        PersonaTimbre goofy = Table().Of(SystemPersona.Goofy, era);
        string key = "e1.end.handover";
        short[] s = Samples(Render(goofy, key));

        (int evenStart, int evenLen) = SyllableWindow(goofy, key, 0);
        (int oddStart, int oddLen) = SyllableWindow(goofy, key, 1);
        double evenHz = Zcr(s, evenStart, evenLen) / 2.0;
        double oddHz = Zcr(s, oddStart, oddLen) / 2.0;

        // step_ratio 1.335 ≈ perfect fourth; ZCR tolerance is coarse — ±20%
        Assert.InRange(oddHz / evenHz, 1.335 * 0.8, 1.335 * 1.2);
    }

    [Fact] // legal — 中性旁白与全部 16 人格行相异（off 模式降级目标独立可辨）
    public void Narrator_is_distinct_from_every_persona_row()
    {
        PersonaTimbreTable table = Table();
        byte[] narrator = Render(table.Narrator, "e1.end.handover");

        foreach (PersonaTimbre row in table.PersonaRows)
        {
            Assert.False(
                Render(row, "e1.end.handover").SequenceEqual(narrator),
                $"{row.Id} token equals the narrator token");
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

    [Theory] // legal — off-mode 降级（era 感知）：系统开={era}:{persona}，关=旁白
    [InlineData(SystemPersona.Advisor)]
    [InlineData(SystemPersona.Snarky)]
    [InlineData(SystemPersona.Goofy)]
    [InlineData(SystemPersona.Tsundere)]
    public void Offmode_downgrade_resolves(SystemPersona persona)
    {
        foreach (string era in PersonaTimbreTable.Eras)
        {
            Assert.Equal(PersonaTimbreTable.NarratorId, VoiceDowngrade.Resolve(persona, era, systemEnabled: false));
            Assert.Equal($"{era}:{EraCore.Presentation.PersonaChoice.IdOf(persona)}", VoiceDowngrade.Resolve(persona, era, systemEnabled: true));
        }
    }

    // -------------------------------------------------------------- boundary

    [Theory] // boundary — syllable counts stay inside the frozen 3~6 window
    [InlineData("")]
    [InlineData("a")]
    [InlineData("e1.open.narrator")]
    [InlineData("advisor.sys.alarm")]
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
        PersonaTimbre timbre = Table().Of(SystemPersona.Tsundere, "era3");

        Assert.True(
            Render(timbre, "e1.open.narrator").SequenceEqual(Render(timbre, "e1.open.narrator")));
        Assert.False(
            Render(timbre, "e1.open.narrator").SequenceEqual(Render(timbre, "e1.end.handover"))); // key drives the token
    }

    [Fact] // boundary — 未知时代解析拒绝（Of 的封闭时代集）
    public void Unknown_era_resolution_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Table().Of(SystemPersona.Advisor, "era5"));
        Assert.Throws<ArgumentException>(() => Table().Of(SystemPersona.Advisor, "e1"));
    }

    [Fact] // boundary — WAV images wrap the PCM payload at the table rate
    public void Wav_image_wraps_payload()
    {
        PersonaTimbre timbre = Table().Of(SystemPersona.Advisor, "era1");
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
        string json = MutateFirstRow(LegalJson, mutation);

        Assert.Throws<AudioSchemaException>(() => PersonaTimbreTable.Parse(json));
    }

    [Fact] // illegal — era 列封闭集：era5 拒绝
    public void Illegal_era_rejected()
    {
        string json = LegalJson.Replace("\"era\": \"era1\"", "\"era\": \"era5\"", StringComparison.Ordinal);

        Assert.Throws<AudioSchemaException>(() => PersonaTimbreTable.Parse(json));
    }

    [Fact] // illegal — 人格行缺 era 列拒绝（v2 必填）
    public void Persona_row_without_era_is_rejected()
    {
        string json = LegalJson.Replace(
            "{ \"id\": \"advisor\",  \"era\": \"era1\",", "{ \"id\": \"advisor\", ", StringComparison.Ordinal);

        Assert.Throws<AudioSchemaException>(() => PersonaTimbreTable.Parse(json));
    }

    [Fact] // illegal — narrator 行不得携带 era（中性旁白不分时代）
    public void Narrator_row_with_era_is_rejected()
    {
        string json = LegalJson.Replace(
            "{ \"id\": \"narrator\", \"zh\": \"旁白\"", "{ \"id\": \"narrator\", \"era\": \"era1\", \"zh\": \"旁白\"", StringComparison.Ordinal);

        Assert.Throws<AudioSchemaException>(() => PersonaTimbreTable.Parse(json));
    }

    [Fact] // illegal — an id outside the closed set (4 personas + narrator)
    public void Illegal_id_rejected()
    {
        string json = LegalJson.Replace("\"id\": \"narrator\"", "\"id\": \"pirate\"");

        Assert.Throws<AudioSchemaException>(() => PersonaTimbreTable.Parse(json));
    }

    [Fact] // illegal — wrong row count
    public void Illegal_row_count_rejected()
    {
        int insertAt = LegalJson.LastIndexOf(']'); // closing bracket of the timbres array
        string json = LegalJson.Insert(insertAt,
            ",{ \"id\": \"extra\", \"era\": \"era1\", \"zh\": \"额外\", \"base_freq_hz\": 300.0, \"contour\": \"steady\", " +
            "\"wave\": \"sine\", \"syllable_ms\": 90, \"gap_ms\": 40, \"vibrato_hz\": 0.0, \"vibrato_depth\": 0.0, \"step_ratio\": 1.0, \"gain\": 0.5 }");

        Assert.Throws<AudioSchemaException>(() => PersonaTimbreTable.Parse(json));
    }

    [Fact] // illegal — (era, persona) 对重复：同对两行拒绝
    public void Duplicate_era_persona_pair_is_rejected()
    {
        string json = LegalJson.Replace(
            "{ \"id\": \"goofy\",    \"era\": \"era3\",", "{ \"id\": \"goofy\",    \"era\": \"era2\",", StringComparison.Ordinal);

        Assert.Throws<AudioSchemaException>(() => PersonaTimbreTable.Parse(json));
    }

    [Fact] // illegal — null timbre / blank key are rejected by the renderer
    public void Null_arguments_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => PersonaVoiceSynth.SynthesizePcm(null!, "key", 16000, 0.6));
        Assert.Throws<ArgumentException>(
            () => PersonaVoiceSynth.SynthesizePcm(Table().Narrator, "  ", 16000, 0.6));
    }

    /// <summary>Applies one mutation to the first timbre row (era1:advisor) of the legal fixture.</summary>
    private static string MutateFirstRow(string json, string mutation)
    {
        string key = mutation.Split(':')[0].Trim().Trim('"');
        string original = key switch
        {
            "content_type" => "\"content_type\": \"PersonaTimbreTable\"",
            "syllable_ms" => "\"syllable_ms\": 95",
            "base_freq_hz" => "\"base_freq_hz\": 415.3",
            "contour" => "\"contour\": \"steady\"",
            "wave" => "\"wave\": \"sine\"",
            "gain" => "\"gain\": 0.5",
            _ => throw new InvalidOperationException($"unhandled mutation key {key}"),
        };

        return json.Replace(original, mutation);
    }
}
