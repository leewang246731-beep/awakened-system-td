using System.Text.Json;

namespace EraCore.Audio;

/// <summary>
/// Thrown when an audio content table violates its schema (T-092). Mirrors
/// <c>NarrativeSchemaException</c>: one exception type per content domain,
/// carrying the source label for the loader log.
/// </summary>
public sealed class AudioSchemaException : Exception
{
    /// <summary>Creates the exception with a fully-formed message.</summary>
    public AudioSchemaException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Instrument family of one era's program (T-121; T-142 grows the set two
/// rungs): the closed timbre set the synthesizer renders. <c>Acoustic</c> =
/// the T-092 voice set (detuned sine pad / kick-snare-hat / soft-clipped
/// bass); <c>Industrial</c> = the E3 soundscape of the 31 号 visual identity
/// card — 蒸汽垫底低频脉冲+微汽笛 / 金属打击+铆钉动机 / 重工业低音+汽笛长鸣
/// 动机; <c>Synth</c> = the E5 future soundscape (T-142, 31 号 §1.2 E5 行) —
/// 聚变脉冲+光流琶音+全息点 / 合成鼓+高频 tick / 聚变低音+尖塔鸣响动机;
/// <c>Cosmic</c> = the final-chapter deep-space soundscape (T-142, 31 号
/// §1.2 终章行) — 零点脉冲+星尘带+记忆钟鸣 / 引力鼓+幽灵军鼓 / 透镜低音+
/// 星门轰鸣动机.
/// </summary>
public enum BgmTimbre
{
    /// <summary>E1/E2 — the T-092 acoustic voice set.</summary>
    Acoustic = 0,

    /// <summary>E3 工业 — steam pad + metal percussion + whistle motif.</summary>
    Industrial = 1,

    /// <summary>E4 现代 (T-157) — mains-hum pad + radio blips + electro drums + urban siren motif.</summary>
    Modern = 2,

    /// <summary>E5 未来 (T-142) — fusion pulse + light-flow arpeggio + spire chime.</summary>
    Synth = 3,

    /// <summary>终章 深空 (T-142) — zero-point pulse + stardust band + gate drone.</summary>
    Cosmic = 4,
}

/// <summary>
/// One era's three-state BGM program (T-092, F-135 E1/E2 薄版, v3.2 §14
/// 三态分层): the harmonic facts the deterministic synthesizer renders the
/// three cumulative layers from — calm = 环境垫底 (detuned sine pad over the
/// chord), combat = +鼓点 (kick/snare/hat on the beat grid), boss = +低音
/// (soft-clipped low note per bar). Pure data parsed strictly from
/// <c>game/content/audio/bgm-programs.json</c>; every timing fact is derived,
/// never stored twice. T-121 adds the <paramref name="Timbre"/> column so the
/// E3 industrial voice set is a table row, parallel to E1/E2 — not code.
/// </summary>
/// <param name="Era">Era id (<c>era1</c>~<c>era6</c> 全六行 — 表行键，与关卡侧时代路由约定一致).</param>
/// <param name="TempoBpm">Tempo in beats per minute (40~200).</param>
/// <param name="RootHz">Chord root fundamental in Hz (55~440).</param>
/// <param name="ChordIntervals">Chord ratios over the root, ascending, first = 1.0.</param>
/// <param name="PadGain">Linear gain of the calm pad layer.</param>
/// <param name="PercussionGain">Linear gain of the combat percussion layer.</param>
/// <param name="BassGain">Linear gain of the boss bass layer.</param>
/// <param name="HatSubdivisions">Hi-hat subdivisions per beat (1~8; denser = later eras).</param>
/// <param name="SampleRateHz">Render sample rate (table-wide).</param>
/// <param name="LoopBars">Loop length in bars (table-wide).</param>
/// <param name="BeatsPerBar">Beats per bar (table-wide).</param>
/// <param name="Timbre">Instrument family (T-121 column; default keeps the T-092 acoustic voice set for direct constructions).</param>
public sealed record BgmProgram(
    string Era,
    double TempoBpm,
    double RootHz,
    IReadOnlyList<double> ChordIntervals,
    double PadGain,
    double PercussionGain,
    double BassGain,
    int HatSubdivisions,
    int SampleRateHz,
    int LoopBars,
    int BeatsPerBar,
    BgmTimbre Timbre = BgmTimbre.Acoustic)
{
    /// <summary>One beat in seconds.</summary>
    public double BeatSec => 60.0 / TempoBpm;

    /// <summary>Loop length in seconds (bars × beats × beat seconds).</summary>
    public double LoopSec => LoopBars * BeatsPerBar * BeatSec;

    /// <summary>Loop length in samples (duration × rate, rounded once — the synthesis period).</summary>
    public int LoopSamples => (int)Math.Round(LoopSec * SampleRateHz);

    /// <summary>Whether the state's cumulative layering includes percussion (combat and boss).</summary>
    public bool HasPercussion(BgmLayerState state) => state >= BgmLayerState.Combat;

    /// <summary>Whether the state's cumulative layering includes the bass line (boss only).</summary>
    public bool HasBass(BgmLayerState state) => state >= BgmLayerState.Boss;
}

/// <summary>
/// Strict parser of the BGM program table (schema
/// <c>urn:eratd:content:audio:bgm-programs:2</c> — v2 adds the per-row
/// <c>timbre</c> column, T-121; T-142 appends the era5/era6 rows): closed
/// marker, bounded globals, unique era ids, ascending chords rooted at 1.0,
/// gains in (0, 1], closed timbre set. Rows = exactly
/// <see cref="ProgramCount"/> (T-121 froze E1/E2/E3; T-142 grew to five; T-157 completes all six eras
/// to five with the E5 future + final-chapter deep-space rows — further
/// eras land by appending rows here, never in code).
/// </summary>
public sealed class BgmProgramTable
{
    /// <summary>Content schema URN of the BGM program table.</summary>
    public const string SchemaUrn = "urn:eratd:content:audio:bgm-programs:2";

    /// <summary>Content type marker the loader requires.</summary>
    public const string ContentTypeMarker = "BgmProgramTable";

    /// <summary>Frozen program count of the T-142 card (era1~era3 T-121 + era5/era6 T-142; E4 BGM 未立项——扩表走本常量).</summary>
    public const int ProgramCount = 6; // ci-allow: R7 T-157 扩表口径=E1/E2/E3+T-142 两章+T-157 era4 六行（08 任务卡；6<10 永不入锚点集，防御性标注）

    private readonly Dictionary<string, BgmProgram> _byEra;

    private BgmProgramTable(int sampleRateHz, int loopBars, int beatsPerBar, IReadOnlyList<BgmProgram> programs)
    {
        SampleRateHz = sampleRateHz;
        LoopBars = loopBars;
        BeatsPerBar = beatsPerBar;
        Programs = programs;
        _byEra = programs.ToDictionary(program => program.Era, StringComparer.Ordinal);
    }

    /// <summary>Render sample rate (Hz), table-wide.</summary>
    public int SampleRateHz { get; }

    /// <summary>Loop length in bars, table-wide.</summary>
    public int LoopBars { get; }

    /// <summary>Beats per bar, table-wide.</summary>
    public int BeatsPerBar { get; }

    /// <summary>All programs in file order.</summary>
    public IReadOnlyList<BgmProgram> Programs { get; }

    /// <summary>Program of one era; null when absent.</summary>
    public BgmProgram? Find(string era) =>
        _byEra.TryGetValue(era, out BgmProgram? program) ? program : null;

    /// <summary>
    /// Strict parse: closed schema/marker, exactly <see cref="ProgramCount"/>
    /// rows, unique bounded eras, ascending chord rooted at 1.0, bounded
    /// gains and subdivisions.
    /// </summary>
    /// <param name="json">Raw table JSON text.</param>
    /// <param name="sourceName">Source label for error messages.</param>
    /// <exception cref="AudioSchemaException">Any schema violation.</exception>
    public static BgmProgramTable Parse(string json, string sourceName = "bgm programs")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AudioSchemaException($"{sourceName}: 根必须是对象");
        }

        RequireMarker(root, "content_type", ContentTypeMarker, sourceName);

        int sampleRate = RequireInt(root, "sample_rate_hz", sourceName);
        if (sampleRate is < 8000 or > 48000)
        {
            throw new AudioSchemaException($"{sourceName}: sample_rate_hz={sampleRate} 越界（8000~48000）");
        }

        int loopBars = RequireInt(root, "loop_bars", sourceName);
        int beatsPerBar = RequireInt(root, "beats_per_bar", sourceName);
        if (loopBars is < 1 or > 16)
        {
            throw new AudioSchemaException($"{sourceName}: loop_bars={loopBars} 越界（1~16）");
        }

        if (beatsPerBar is < 2 or > 8)
        {
            throw new AudioSchemaException($"{sourceName}: beats_per_bar={beatsPerBar} 越界（2~8）");
        }

        if (!root.TryGetProperty("programs", out JsonElement programsEl) || programsEl.ValueKind != JsonValueKind.Array)
        {
            throw new AudioSchemaException($"{sourceName}: 缺少 programs 数组");
        }

        if (programsEl.GetArrayLength() != ProgramCount)
        {
            throw new AudioSchemaException(
                $"{sourceName}: programs 必须 {ProgramCount} 行（T-121 冻结=E1/E2/E3，T-142 扩=E5+era6，T-157 补=E4——再扩时代走改表+本常量）");
        }

        var programs = new List<BgmProgram>(ProgramCount);
        var seenEras = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in programsEl.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                throw new AudioSchemaException($"{sourceName}: program 行必须是对象");
            }

            string era = RequireString(row, "era", sourceName);
            if (!seenEras.Add(era))
            {
                throw new AudioSchemaException($"{sourceName}: 时代重复 {era}");
            }

            double tempo = RequireNumber(row, "tempo_bpm", sourceName);
            if (tempo is < 40.0 or > 200.0)
            {
                throw new AudioSchemaException($"{sourceName}: {era} tempo_bpm={tempo} 越界（40~200）");
            }

            double rootHz = RequireNumber(row, "root_hz", sourceName);
            if (rootHz is < 55.0 or > 440.0)
            {
                throw new AudioSchemaException($"{sourceName}: {era} root_hz={rootHz} 越界（55~440）");
            }

            if (!row.TryGetProperty("chord_intervals", out JsonElement chordEl) || chordEl.ValueKind != JsonValueKind.Array)
            {
                throw new AudioSchemaException($"{sourceName}: {era} 缺少 chord_intervals 数组");
            }

            var intervals = new List<double>();
            foreach (JsonElement intervalEl in chordEl.EnumerateArray())
            {
                if (intervalEl.ValueKind != JsonValueKind.Number)
                {
                    throw new AudioSchemaException($"{sourceName}: {era} chord_intervals 项必须是数字");
                }

                double interval = intervalEl.GetDouble();
                if (interval is < 1.0 or > 2.5)
                {
                    throw new AudioSchemaException($"{sourceName}: {era} 和弦音程 {interval} 越界（1.0~2.5）");
                }

                intervals.Add(interval);
            }

            if (intervals.Count is < 2 or > 6)
            {
                throw new AudioSchemaException($"{sourceName}: {era} 和弦须 2~6 音，实为 {intervals.Count}");
            }

            if (Math.Abs(intervals[0] - 1.0) > 1e-9)
            {
                throw new AudioSchemaException($"{sourceName}: {era} 和弦第一音必须=1.0（根音）");
            }

            for (int i = 1; i < intervals.Count; i++)
            {
                if (intervals[i] <= intervals[i - 1])
                {
                    throw new AudioSchemaException($"{sourceName}: {era} 和弦音程必须严格递增");
                }
            }

            double padGain = RequireGain(row, "pad_gain", era, sourceName);
            double percussionGain = RequireGain(row, "percussion_gain", era, sourceName);
            double bassGain = RequireGain(row, "bass_gain", era, sourceName);
            int hatSubdivisions = RequireInt(row, "hat_subdivisions", sourceName);
            if (hatSubdivisions is < 1 or > 8)
            {
                throw new AudioSchemaException($"{sourceName}: {era} hat_subdivisions={hatSubdivisions} 越界（1~8）");
            }

            string timbreText = RequireString(row, "timbre", sourceName);
            BgmTimbre timbre = timbreText switch
            {
                "acoustic" => BgmTimbre.Acoustic,
                "industrial" => BgmTimbre.Industrial,
                "modern" => BgmTimbre.Modern,
                "synth" => BgmTimbre.Synth,
                "cosmic" => BgmTimbre.Cosmic,
                _ => throw new AudioSchemaException(
                    $"{sourceName}: {era} timbre={timbreText} 不在封闭集（acoustic|industrial|modern|synth|cosmic）"),
            };

            programs.Add(new BgmProgram(
                era, tempo, rootHz, intervals, padGain, percussionGain, bassGain,
                hatSubdivisions, sampleRate, loopBars, beatsPerBar, timbre));
        }

        return new BgmProgramTable(sampleRate, loopBars, beatsPerBar, programs);
    }

    private static void RequireMarker(JsonElement root, string key, string expected, string sourceName)
    {
        if (!root.TryGetProperty(key, out JsonElement marker) ||
            marker.ValueKind != JsonValueKind.String ||
            marker.GetString() != expected)
        {
            throw new AudioSchemaException($"{sourceName}: {key} 必须为 {expected}");
        }
    }

    private static string RequireString(JsonElement row, string key, string sourceName)
    {
        if (!row.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            throw new AudioSchemaException($"{sourceName}: 缺少字符串字段 {key}");
        }

        string value = el.GetString()!;
        return string.IsNullOrWhiteSpace(value)
            ? throw new AudioSchemaException($"{sourceName}: {key} 不能为空白")
            : value;
    }

    private static int RequireInt(JsonElement row, string key, string sourceName)
    {
        if (!row.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
        {
            throw new AudioSchemaException($"{sourceName}: 缺少数值字段 {key}");
        }

        return el.TryGetInt32(out int value)
            ? value
            : throw new AudioSchemaException($"{sourceName}: {key} 必须是整数");
    }

    private static double RequireNumber(JsonElement row, string key, string sourceName)
    {
        if (!row.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
        {
            throw new AudioSchemaException($"{sourceName}: 缺少数值字段 {key}");
        }

        return el.GetDouble();
    }

    private static double RequireGain(JsonElement row, string key, string era, string sourceName)
    {
        double gain = RequireNumber(row, key, sourceName);
        return gain is <= 0.0 or > 1.0
            ? throw new AudioSchemaException($"{sourceName}: {era} {key}={gain} 越界（(0,1]）")
            : gain;
    }
}
