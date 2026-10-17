using System.Text.Json;
using EraCore.Narrative;

namespace EraCore.Audio;

/// <summary>Pitch contour family of a persona timbre (T-092, F-137).</summary>
public enum VoiceContour
{
    /// <summary>平稳 — constant pitch per syllable (参谋).</summary>
    Steady = 0,

    /// <summary>尖锐短促 — short syllables stepping up in pitch (毒舌).</summary>
    Sharp = 1,

    /// <summary>滑稽跳音 — alternating pitch jumps between syllables (沙雕).</summary>
    Bounce = 2,

    /// <summary>起伏 — vibrato wobble within each syllable (傲娇).</summary>
    Wobble = 3,
}

/// <summary>Waveform family of a persona timbre.</summary>
public enum VoiceWave
{
    /// <summary>Soft sine — neutral and narration voices.</summary>
    Sine = 0,

    /// <summary>Triangle — a reed-like plucked color (沙雕).</summary>
    Triangle = 1,

    /// <summary>Soft-clipped sine — a buzzy edge (毒舌).</summary>
    Square = 2,
}

/// <summary>
/// One voice timbre row (T-092, F-137; v2=T-126 full era×persona banks):
/// the synthesis facts behind one speaker's audible identity. Sixteen
/// persona rows — four personas × four eras (时代×人格音色变体, v1.0 E1~E4
/// 冻结口径) — plus the neutral narrator row the off-mode downgrade
/// switches to (关闭系统换中性旁白, v3.2 §10.7/§14).
/// </summary>
/// <param name="Id">Timbre id (<c>{era}:{persona}</c>, e.g. <c>era3:snarky</c>; narrator = <c>narrator</c>).</param>
/// <param name="Zh">Chinese label (UI decoration).</param>
/// <param name="Era">Era key of the row (<c>era1</c>~<c>era4</c>); null on the narrator row (era-agnostic).</param>
/// <param name="PersonaId">Persona id of the row; null on the narrator row.</param>
/// <param name="BaseFreqHz">Fundamental of the first syllable (Hz).</param>
/// <param name="Contour">Pitch contour family.</param>
/// <param name="Wave">Waveform family.</param>
/// <param name="SyllableSec">One syllable's length (s).</param>
/// <param name="GapSec">Silent gap between syllables (s).</param>
/// <param name="VibratoHz">Wobble rate (Hz; 0 = none).</param>
/// <param name="VibratoDepth">Wobble depth (ratio of the base frequency).</param>
/// <param name="StepRatio">Per-syllable pitch step (sharp) or jump ratio (bounce).</param>
/// <param name="Gain">Linear gain of the rendered token.</param>
public sealed record PersonaTimbre(
    string Id,
    string Zh,
    string? Era,
    string? PersonaId,
    double BaseFreqHz,
    VoiceContour Contour,
    VoiceWave Wave,
    double SyllableSec,
    double GapSec,
    double VibratoHz,
    double VibratoDepth,
    double StepRatio,
    double Gain);

/// <summary>
/// Strict parser of the persona timbre table (schema
/// <c>urn:eratd:content:audio:persona-timbres:2</c>, T-092 参数表数据化,
/// T-126 v2 加 era 列与 16 人格行): closed marker, exactly one row per
/// (era, persona) pair of the frozen sets plus the narrator row, ids
/// matching the closed <see cref="SystemPersona"/> name set, bounded
/// frequencies/timings/gains.
/// </summary>
public sealed class PersonaTimbreTable
{
    /// <summary>Content schema URN of the persona timbre table (v2: era column).</summary>
    public const string SchemaUrn = "urn:eratd:content:audio:persona-timbres:2";

    /// <summary>Content type marker the loader requires.</summary>
    public const string ContentTypeMarker = "PersonaTimbreTable";

    /// <summary>Timbre id of the neutral narrator (off-mode downgrade target).</summary>
    public const string NarratorId = "narrator";

    /// <summary>Frozen era set of the voice table (v1.0 = E1~E4; T-126 全量口径).</summary>
    public static readonly IReadOnlyList<string> FrozenEras = new[] { "era1", "era2", "era3", "era4" };

    /// <summary>Frozen persona-row count: four eras × four personas (T-126; smoke tones=16).</summary>
    public const int PersonaRowCount = 16; // ci-allow: R7 T-126 冻结口径=时代×人格 4×4=16 行（§14；E5 扩容走改表）

    /// <summary>Frozen row count: 16 era×persona rows + the narrator (T-126).</summary>
    public const int RowCount = PersonaRowCount + 1; // ci-allow: R7 T-126 冻结口径=16 人格行+1 中性旁白（§14；扩容走改表）

    private readonly Dictionary<string, PersonaTimbre> _byId;

    private PersonaTimbreTable(int sampleRateHz, double peakAmplitude, IReadOnlyList<PersonaTimbre> timbres)
    {
        SampleRateHz = sampleRateHz;
        PeakAmplitude = peakAmplitude;
        Timbres = timbres;
        PersonaRows = timbres.Where(row => row.PersonaId is not null).ToArray();
        _byId = timbres.ToDictionary(timbre => timbre.Id, StringComparer.Ordinal);
    }

    /// <summary>Render sample rate (Hz), table-wide.</summary>
    public int SampleRateHz { get; }

    /// <summary>Linear peak amplitude of rendered tokens, table-wide.</summary>
    public double PeakAmplitude { get; }

    /// <summary>All timbre rows in file order.</summary>
    public IReadOnlyList<PersonaTimbre> Timbres { get; }

    /// <summary>The sixteen era×persona rows (narrator excluded).</summary>
    public IReadOnlyList<PersonaTimbre> PersonaRows { get; }

    /// <summary>The closed era set the table must cover (frozen, <see cref="FrozenEras"/>).</summary>
    public static IReadOnlyList<string> Eras => FrozenEras;

    /// <summary>Timbre by composed id; null when absent.</summary>
    public PersonaTimbre? Find(string id) =>
        _byId.TryGetValue(id, out PersonaTimbre? timbre) ? timbre : null;

    /// <summary>Timbre of one persona in one era (closed sets).</summary>
    /// <exception cref="ArgumentException">persona/era pair has no timbre row.</exception>
    public PersonaTimbre Of(SystemPersona persona, string era)
    {
        if (!FrozenEras.Contains(era, StringComparer.Ordinal))
        {
            throw new ArgumentException($"未登记音色时代 {era}（封闭表 {string.Join("/", FrozenEras)}）", nameof(era));
        }

        return Find($"{era}:{persona.Name()}") ?? throw new ArgumentException(
            $"人格 {persona}@{era} 无音色行（表须含 时代×人格 16 行）", nameof(persona));
    }

    /// <summary>The neutral narrator timbre (off-mode downgrade target).</summary>
    /// <exception cref="AudioSchemaException">Table was parsed without a narrator row (parser forbids this).</exception>
    public PersonaTimbre Narrator =>
        Find(NarratorId) ?? throw new AudioSchemaException($"音色表缺 {NarratorId} 行（解析器应已拦截）");

    /// <summary>
    /// Strict parse: closed marker, exactly one row per (era, persona) pair
    /// of the frozen sets plus the <see cref="NarratorId"/> row, bounded
    /// values, contour/wave names from the closed enums. Persona rows carry
    /// an <c>era</c> key from <see cref="FrozenEras"/>; the narrator row
    /// carries none (era-agnostic off-mode target).
    /// </summary>
    /// <param name="json">Raw table JSON text.</param>
    /// <param name="sourceName">Source label for error messages.</param>
    /// <exception cref="AudioSchemaException">Any schema violation.</exception>
    public static PersonaTimbreTable Parse(string json, string sourceName = "persona timbres")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AudioSchemaException($"{sourceName}: 根必须是对象");
        }

        RequireString(root, "content_type", sourceName, expected: ContentTypeMarker);

        if (!root.TryGetProperty("sample_rate_hz", out JsonElement rateEl) ||
            rateEl.ValueKind != JsonValueKind.Number || !rateEl.TryGetInt32(out int sampleRate) ||
            sampleRate is < 8000 or > 48000)
        {
            throw new AudioSchemaException($"{sourceName}: sample_rate_hz 缺失或越界（8000~48000）");
        }

        if (!root.TryGetProperty("peak_amplitude", out JsonElement peakEl) ||
            peakEl.ValueKind != JsonValueKind.Number)
        {
            throw new AudioSchemaException($"{sourceName}: 缺少 peak_amplitude");
        }

        double peak = peakEl.GetDouble();
        if (peak is <= 0.0 or > 1.0)
        {
            throw new AudioSchemaException($"{sourceName}: peak_amplitude={peak} 越界（(0,1]）");
        }

        if (!root.TryGetProperty("timbres", out JsonElement rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
        {
            throw new AudioSchemaException($"{sourceName}: 缺少 timbres 数组");
        }

        if (rowsEl.GetArrayLength() != RowCount)
        {
            throw new AudioSchemaException($"{sourceName}: timbres 必须 {RowCount} 行（时代×人格 16 行+1 中性旁白）");
        }

        var closedPersonas = new HashSet<string>(Narrative.DialogueLine.PersonaNames(), StringComparer.Ordinal);
        var seenPairs = new HashSet<string>(StringComparer.Ordinal);
        var timbres = new List<PersonaTimbre>(RowCount);
        foreach (JsonElement row in rowsEl.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                throw new AudioSchemaException($"{sourceName}: timbre 行必须是对象");
            }

            string id = RequireString(row, "id", sourceName);
            string zh = RequireString(row, "zh", sourceName);

            // era column (v2): persona rows must carry a frozen-era key; the
            // narrator row must not (era-agnostic downgrade target).
            string? era = null;
            if (row.TryGetProperty("era", out JsonElement eraEl) && eraEl.ValueKind == JsonValueKind.String)
            {
                era = eraEl.GetString();
            }

            string? personaId;
            if (id == NarratorId)
            {
                if (era is not null)
                {
                    throw new AudioSchemaException($"{sourceName}: {NarratorId} 行不得携带 era（中性旁白不分时代）");
                }

                personaId = null;
            }
            else
            {
                if (!closedPersonas.Contains(id))
                {
                    throw new AudioSchemaException($"{sourceName}: 音色 id {id} 不在封闭集（四人格+{NarratorId}）");
                }

                if (era is null || !FrozenEras.Contains(era, StringComparer.Ordinal))
                {
                    throw new AudioSchemaException(
                        $"{sourceName}: 人格行 {id} 缺 era 或 era 未登记（{era ?? "缺失"}，封闭表 {string.Join("/", FrozenEras)}）");
                }

                personaId = id;
                if (!seenPairs.Add($"{era}:{id}"))
                {
                    throw new AudioSchemaException($"{sourceName}: 音色时代对重复 {era}:{id}");
                }
            }

            string rowLabel = personaId is null ? NarratorId : $"{era}:{personaId}";
            double baseFreq = RequireNumber(row, "base_freq_hz", sourceName);
            if (baseFreq is < 80.0 or > 1000.0)
            {
                throw new AudioSchemaException($"{sourceName}: {rowLabel} base_freq_hz={baseFreq} 越界（80~1000）");
            }

            VoiceContour contour = ParseEnum<VoiceContour>(RequireString(row, "contour", sourceName), sourceName);
            VoiceWave wave = ParseEnum<VoiceWave>(RequireString(row, "wave", sourceName), sourceName);
            double syllableMs = RequireNumber(row, "syllable_ms", sourceName);
            if (syllableMs is < 40.0 or > 300.0)
            {
                throw new AudioSchemaException($"{sourceName}: {rowLabel} syllable_ms={syllableMs} 越界（40~300）");
            }

            double gapMs = RequireNumber(row, "gap_ms", sourceName);
            if (gapMs is < 0.0 or > 200.0)
            {
                throw new AudioSchemaException($"{sourceName}: {rowLabel} gap_ms={gapMs} 越界（0~200）");
            }

            double vibratoHz = RequireNumber(row, "vibrato_hz", sourceName);
            double vibratoDepth = RequireNumber(row, "vibrato_depth", sourceName);
            if (vibratoHz < 0 || vibratoHz > 12 || vibratoDepth < 0 || vibratoDepth > 0.1)
            {
                throw new AudioSchemaException($"{sourceName}: {rowLabel} 颤音参数越界（0~12 Hz / 0~0.1）");
            }

            double stepRatio = RequireNumber(row, "step_ratio", sourceName);
            if (stepRatio is < 1.0 or > 2.0)
            {
                throw new AudioSchemaException($"{sourceName}: {rowLabel} step_ratio={stepRatio} 越界（1.0~2.0）");
            }

            double gain = RequireNumber(row, "gain", sourceName);
            if (gain is <= 0.0 or > 1.0)
            {
                throw new AudioSchemaException($"{sourceName}: {rowLabel} gain={gain} 越界（(0,1]）");
            }

            timbres.Add(new PersonaTimbre(
                personaId is null ? NarratorId : $"{era}:{personaId}",
                zh, era, personaId, baseFreq, contour, wave,
                syllableMs / 1000.0, gapMs / 1000.0, vibratoHz, vibratoDepth, stepRatio, gain));
        }

        if (seenPairs.Count != PersonaRowCount)
        {
            throw new AudioSchemaException(
                $"{sourceName}: 时代×人格行必须 {PersonaRowCount} 行（得到 {seenPairs.Count}——每对恰好一行）");
        }

        if (timbres.All(row => row.PersonaId is not null))
        {
            throw new AudioSchemaException($"{sourceName}: 缺少中性旁白行 {NarratorId}（关闭系统降级目标）");
        }

        return new PersonaTimbreTable(sampleRate, peak, timbres);
    }

    private static string RequireString(JsonElement row, string key, string sourceName, string? expected = null)
    {
        if (!row.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            throw new AudioSchemaException($"{sourceName}: 缺少字符串字段 {key}");
        }

        string value = el.GetString()!;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AudioSchemaException($"{sourceName}: {key} 不能为空白");
        }

        return expected is not null && value != expected
            ? throw new AudioSchemaException($"{sourceName}: {key} 必须为 {expected}")
            : value;
    }

    private static double RequireNumber(JsonElement row, string key, string sourceName)
    {
        if (!row.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
        {
            throw new AudioSchemaException($"{sourceName}: 缺少数值字段 {key}");
        }

        return el.GetDouble();
    }

    private static T ParseEnum<T>(string name, string sourceName)
        where T : struct, Enum =>
        Enum.TryParse<T>(name, ignoreCase: true, out T value) && Enum.IsDefined(value)
            ? value
            : throw new AudioSchemaException($"{sourceName}: 未登记枚举值 {typeof(T).Name}.{name}");
}

/// <summary>
/// Persona voice-token renderer (T-092, F-137): turns one
/// <see cref="PersonaTimbre"/> + one line key into a short deterministic
/// syllable sequence — the audible identity of the advisor persona (code
/// synthesis; human voiceover remains the optional outsourcing track of
/// §14). Syllable count comes from an FNV-1a hash of the key (same line ⇒
/// same voice, no random, CS-1.4); pitch/wave/envelope come from the table
/// row, so switching persona changes ONLY the timbre — wording and
/// capability are owned elsewhere (人格只改表达不改能力, §10.7).
/// </summary>
public static class PersonaVoiceSynth
{
    /// <summary>Minimum syllables per token (bounded, keeps tokens snappy).</summary>
    public const int MinSyllables = 3;

    /// <summary>Maximum syllables per token.</summary>
    public const int MaxSyllables = 6;

    /// <summary>Syllable attack ramp (s).</summary>
    public const double AttackSec = 0.008;

    private const short FullScale = 32767;
    private const double TwoPi = 2.0 * Math.PI;

    /// <summary>
    /// Syllable count of one line key: 3~6 from the FNV-1a hash —
    /// deterministic across machines (integer arithmetic only).
    /// </summary>
    public static int SyllableCount(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return MinSyllables + (int)(Fnv1a(key) % (MaxSyllables - MinSyllables + 1));
    }

    /// <summary>
    /// Renders the voice token of one line under one timbre as raw PCM
    /// (16-bit little-endian mono at the table's rate).
    /// </summary>
    /// <exception cref="ArgumentNullException">timbre or key is null/blank.</exception>
    public static byte[] SynthesizePcm(PersonaTimbre timbre, string key, int sampleRateHz, double peakAmplitude)
    {
        ArgumentNullException.ThrowIfNull(timbre);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        int syllables = SyllableCount(key);
        int syllableSamples = Math.Max(1, (int)Math.Round(timbre.SyllableSec * sampleRateHz));
        int gapSamples = Math.Max(0, (int)Math.Round(timbre.GapSec * sampleRateHz));
        int count = (syllables * syllableSamples) + ((syllables - 1) * gapSamples);
        byte[] pcm = new byte[count * 2];
        int written = 0;

        for (int syllable = 0; syllable < syllables; syllable++)
        {
            double syllableFreq = timbre.BaseFreqHz * Math.Pow(timbre.StepRatio,
                timbre.Contour == VoiceContour.Sharp ? syllable : 0.0);
            if (timbre.Contour == VoiceContour.Bounce && syllable % 2 == 1)
            {
                syllableFreq = timbre.BaseFreqHz * timbre.StepRatio;
            }

            for (int i = 0; i < syllableSamples; i++)
            {
                double t = i / (double)sampleRateHz;
                double frequency = timbre.VibratoHz > 0
                    ? syllableFreq * (1.0 + (timbre.VibratoDepth * Math.Sin(TwoPi * timbre.VibratoHz * t)))
                    : syllableFreq;

                double oscillator = timbre.Wave switch
                {
                    VoiceWave.Sine => Math.Sin(TwoPi * frequency * t),
                    VoiceWave.Triangle => (2.0 / Math.PI) * Math.Asin(Math.Sin(TwoPi * frequency * t)),
                    VoiceWave.Square => Math.Tanh(3.0 * Math.Sin(TwoPi * frequency * t)),
                    _ => throw new InvalidOperationException($"未登记波形 {timbre.Wave}"),
                };

                double envelope = Math.Exp(-t / (timbre.SyllableSec / 3.0));
                if (t < AttackSec)
                {
                    envelope *= t / AttackSec; // click-free onset
                }

                double value = timbre.Gain * envelope * oscillator;
                short quantized = (short)Math.Clamp(
                    (int)Math.Round(value * peakAmplitude * FullScale), -FullScale, FullScale);
                pcm[(2 * written) + (2 * i)] = (byte)(quantized & 0xFF);
                pcm[(2 * written) + (2 * i) + 1] = (byte)((quantized >> 8) & 0xFF);
            }

            written += syllableSamples + gapSamples;
        }

        return pcm;
    }

    /// <summary>Renders the token as a canonical WAV file image (audition artifact).</summary>
    /// <exception cref="ArgumentNullException">timbre or key is null/blank.</exception>
    public static byte[] SynthesizeWav(PersonaTimbre timbre, string key, int sampleRateHz, double peakAmplitude) =>
        WavContainer.Wrap(sampleRateHz, SynthesizePcm(timbre, key, sampleRateHz, peakAmplitude));

    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261u;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }
}

/// <summary>
/// Off-mode voice downgrade policy (T-092, v3.2 §10.7/§14 关闭系统换中性旁白):
/// resolves the effective timbre id — the active persona's era row while
/// the system is on, the neutral narrator row once it is off (T-126 v2:
/// era×persona resolution). Pure mapping; the alarm channel (危机警报) is
/// NOT downgraded (警报不缺失 — that seam lives in OffModeDowngrade of the
/// panel domain).
/// </summary>
public static class VoiceDowngrade
{
    /// <summary>
    /// Effective timbre id for the advisor voice under the system switch
    /// (era-aware since T-126 v2: <c>{era}:{persona}</c> or
    /// <see cref="PersonaTimbreTable.NarratorId"/>).
    /// </summary>
    public static string Resolve(SystemPersona persona, string era, bool systemEnabled) =>
        systemEnabled ? $"{era}:{persona.Name()}" : PersonaTimbreTable.NarratorId;
}
