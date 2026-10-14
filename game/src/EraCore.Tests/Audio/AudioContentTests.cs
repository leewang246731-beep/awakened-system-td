using EraCore.Audio;
using EraCore.Narrative;
using EraCore.Tests.Narrative;
using Xunit;

namespace EraCore.Tests.Audio;

// T-092 (C-25) 测试基建+内容集成：仓库相对寻址的音频内容装载（RL9：目录上
// 溯，同 NarrativeContent 模式）+ 三个入库参数表的真实内容断言（含旁白锚
// 与叙事章台词 key 的跨域一致性——F-138 与 T-089 内容面的链接）。纯装载辅
// 助不含测试方法（T3 不适用）；AudioContentTests 为其对应测试文件。

/// <summary>Repo-relative loader for the audio content tables.</summary>
internal static class AudioContent
{
    private static string FindContentDir(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/{string.Join("/", relative)}（测试须在仓库内运行）");
    }

    /// <summary>Raw text of one file under game/content/audio/.</summary>
    public static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(FindContentDir("game", "content", "audio"), fileName));

    /// <summary>The shipped BGM program table.</summary>
    public static BgmProgramTable BgmPrograms() =>
        BgmProgramTable.Parse(Read("bgm-programs.json"), "audio/bgm-programs.json");

    /// <summary>The shipped persona timbre table.</summary>
    public static PersonaTimbreTable Timbres() =>
        PersonaTimbreTable.Parse(Read("persona-timbres.json"), "audio/persona-timbres.json");

    /// <summary>The shipped narration anchor table.</summary>
    public static NarrationTable Narrations() =>
        NarrationTable.Parse(Read("narration.json"), "audio/narration.json");
}

/// <summary>
/// Content-integration tests of the three shipped audio tables (T-092):
/// the E1/E2 programs render end to end, the timbre set covers the four
/// personas + narrator, and every narration anchor exists as a real line
/// key in its story chapter (F-138 ↔ T-089 linkage).
/// </summary>
// [C-25][F-135][F-137][F-138] 入库音频参数表集成断言
public class AudioContentTests
{
    [Fact] // legal — the shipped BGM table is the frozen E1/E2 pair and renders all six loops
    public void Shipped_bgm_table_renders_six_loops()
    {
        BgmProgramTable table = AudioContent.BgmPrograms();

        Assert.Equal(2, table.Programs.Count);
        Assert.NotNull(table.Find("era1"));
        Assert.NotNull(table.Find("era2"));

        foreach (BgmProgram program in table.Programs)
        {
            foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
            {
                byte[] pcm = BgmSynth.SynthesizePcm(program, state);
                Assert.Equal(program.LoopSamples * 2, pcm.Length);
                Assert.Contains(pcm, b => b != 0); // audible, not digital silence
            }
        }
    }

    [Fact] // legal — the shipped timbre table covers the four personas + narrator
    public void Shipped_timbre_table_covers_closed_set()
    {
        PersonaTimbreTable table = AudioContent.Timbres();

        Assert.Equal(5, table.Timbres.Count);
        Assert.NotNull(table.Narrator);
        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            _ = table.Of(persona); // throws when a persona row is missing
        }

        // the four personas are pairwise distinct on a real E1 line key
        var distinct = Enum.GetValues<SystemPersona>()
            .Select(persona => Convert.ToBase64String(PersonaVoiceSynth.SynthesizePcm(
                table.Of(persona), "e1.open.narrator", table.SampleRateHz, table.PeakAmplitude)))
            .Distinct()
            .Count();
        Assert.Equal(4, distinct);
    }

    [Fact] // legal — every narration anchor is a real line key of its story chapter (F-138 ↔ T-089)
    public void Narration_anchors_exist_in_story_chapters()
    {
        NarrationTable narrations = AudioContent.Narrations();
        var chapters = new Dictionary<string, StoryChapter>
        {
            ["e1"] = StoryChapter.Parse(NarrativeContent.Read("story-e1.json"), "narrative/story-e1.json"),
            ["e2"] = StoryChapter.Parse(NarrativeContent.Read("story-e2.json"), "narrative/story-e2.json"),
        };

        Assert.Equal(4, narrations.Rows.Count);
        foreach (NarrationCue cue in narrations.Rows)
        {
            string era = cue.Id.Split('.')[0];
            StoryChapter chapter = chapters[era];

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (DialogueLine line in chapter.Opening)
            {
                _ = keys.Add(line.Key);
            }

            foreach (DialogueLine line in chapter.Ending.Lines)
            {
                _ = keys.Add(line.Key);
            }

            Assert.Contains(cue.Id, keys); // the one-time narration points at a shipped line
            NarrationKind expected = cue.Id.Contains(".open.") ? NarrationKind.Opening : NarrationKind.Victory;
            Assert.Equal(expected, cue.Kind);
        }
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — the two shipped tables agree on domain constants (rates within WAV-legal band)
    public void Shipped_rates_are_wav_legal()
    {
        Assert.InRange(AudioContent.BgmPrograms().SampleRateHz, 8000, 48000);
        Assert.InRange(AudioContent.Timbres().SampleRateHz, 8000, 48000);
        Assert.InRange(AudioContent.Timbres().PeakAmplitude, 0.0, 1.0);
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — the shipped files are strict: swapping two markers fails loud
    public void Cross_wired_content_rejected()
    {
        string bgm = AudioContent.Read("bgm-programs.json");
        string timbres = AudioContent.Read("persona-timbres.json");

        Assert.Throws<EraCore.Audio.AudioSchemaException>(
            () => PersonaTimbreTable.Parse(bgm, "cross-wired"));
        Assert.Throws<EraCore.Audio.AudioSchemaException>(
            () => BgmProgramTable.Parse(timbres, "cross-wired"));
    }
}
