namespace EraCore.Audio;

/// <summary>
/// The three BGM states of v3.2 §14 (C-25, F-135): one cumulative layer per
/// state — the crossfade bus (T-092) morphs between the matching rendered
/// loops with equal-power fades (三态分层淡入淡出).
/// </summary>
public enum BgmLayerState
{
    /// <summary>平静 — the ambient pad layer alone (环境垫底).</summary>
    Calm = 0,

    /// <summary>交战 — pad + the beat-grid percussion layer (战斗加鼓点).</summary>
    Combat = 1,

    /// <summary>Boss — pad + percussion + the low bass line (Boss 加低音).</summary>
    Boss = 2,
}

/// <summary>
/// Deterministic three-state BGM loop renderer (T-092, F-135 E1/E2, v3.2
/// §14): renders one era's program into a loop per state. Zero binary assets
/// and zero engine types (D-01). Every tonal component is quantized to an
/// INTEGER number of cycles per loop and every percussion hit is a pure
/// function of its offset from a fixed beat-grid position (noise from a
/// per-hit seeded LCG — no shared <c>Random</c>, CS-1.4), so the rendered
/// sample sequence is exactly loop-periodic (seamless looping) and
/// same-machine reproducible (same program+state ⇒ same bytes; the DingSynth
/// W2 批 3 F7 wording applies — no cross-architecture bit-level claim, the
/// persisted WAV snapshot is the audition/regression anchor).
/// </summary>
public static class BgmSynth
{
    /// <summary>Peak every loop is normalized to (linear, 16-bit full scale = 1.0) — one loudness across states and eras so crossfades do not pump.</summary>
    public const double NormalizedPeak = 0.72;

    /// <summary>Detune of the doubled pad voices (± ratio) — the classic warm-pad beating.</summary>
    public const double PadDetune = 0.0018;

    /// <summary>Kick pitch sweep start (Hz).</summary>
    public const double KickFromHz = 130.0;

    /// <summary>Kick pitch sweep end (Hz).</summary>
    public const double KickToHz = 45.0;

    /// <summary>Kick length (s).</summary>
    public const double KickSec = 0.11;

    /// <summary>Snare length (s).</summary>
    public const double SnareSec = 0.07;

    /// <summary>Snare body tone (Hz).</summary>
    public const double SnareToneHz = 190.0;

    /// <summary>Hi-hat length (s).</summary>
    public const double HatSec = 0.015;

    /// <summary>Click-kill release ramp of every percussion event (s).</summary>
    public const double PercReleaseSec = 0.003;

    /// <summary>
    /// Longest percussion tail (s) — the loop-end clearance guard. The kick
    /// is the longest event (0.11 s + release); anything longer has decayed
    /// below audibility.
    /// </summary>
    public const double MaxPercussionTailSec = 0.12;

    private const short FullScale = 32767;
    private const double TwoPi = 2.0 * Math.PI;

    /// <summary>
    /// Renders one state's loop as raw PCM: 16-bit little-endian mono at the
    /// program's sample rate — the exact payload
    /// <c>AudioStreamWav.Data</c> expects.
    /// </summary>
    /// <exception cref="ArgumentNullException">program is null.</exception>
    /// <exception cref="ArgumentException">state is not a defined value.</exception>
    /// <exception cref="InvalidOperationException">
    /// The beat grid leaves no clearance for the last percussion tail (tempo
    /// too fast for the loop length) — fix the program row, not the renderer.
    /// </exception>
    public static byte[] SynthesizePcm(BgmProgram program, BgmLayerState state)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentException($"Unknown BGM state {state}.", nameof(state));
        }

        int rate = program.SampleRateHz;
        int count = program.LoopSamples;
        double[] samples = new double[count];

        // ---- layer 1: pad — doubled detuned sines + per-voice tremolo -----
        // Every k is integer cycles per loop ⇒ sin(2π·k·n/N) is N-periodic.
        int voice = 0;
        foreach (double interval in program.ChordIntervals)
        {
            foreach (double detune in new[] { 1.0 + PadDetune, 1.0 - PadDetune })
            {
                double frequency = program.RootHz * interval * detune;
                int cycles = (int)Math.Round(frequency * count / (double)rate);
                if (cycles < 1)
                {
                    continue; // sub-Hz after quantization — inaudible, skip
                }

                int lfoCycles = 1 + (voice % 2); // slow 1-or-2 cycle breathing, integer ⇒ periodic
                double voiceGain = program.PadGain / program.ChordIntervals.Count;
                double phase = voice * 0.31;
                for (int n = 0; n < count; n++)
                {
                    double tremolo = 0.75 + 0.25 * Math.Sin((TwoPi * lfoCycles * n / count) + phase);
                    samples[n] += voiceGain * tremolo * Math.Sin(TwoPi * cycles * n / (double)count);
                }

                voice++;
            }
        }

        // ---- layer 2: percussion events on the beat grid (combat+) --------
        if (program.HasPercussion(state))
        {
            int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
            int totalBeats = program.LoopBars * program.BeatsPerBar;
            int subdivision = Math.Max(1, beatSamples / program.HatSubdivisions);
            int tailSamples = (int)Math.Round(MaxPercussionTailSec * rate);
            int lastHit = (totalBeats - 1) * beatSamples + (subdivision * (program.HatSubdivisions - 1));
            if (lastHit + tailSamples >= count)
            {
                throw new InvalidOperationException(
                    $"{program.Era}: 节拍网格尾端（{lastHit + tailSamples} 样）越过循环尾（{count} 样）——先修参数行");
            }

            foreach ((int start, double[] contribution) in PercussionEvents(program, beatSamples, totalBeats, subdivision, rate))
            {
                for (int i = 0; i < contribution.Length && start + i < count; i++)
                {
                    samples[start + i] += contribution[i];
                }
            }
        }

        // ---- layer 3: boss bass — alternating root/fifth per bar ----------
        if (program.HasBass(state))
        {
            int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
            int barSamples = beatSamples * program.BeatsPerBar;
            double fifthRatio = program.ChordIntervals[Math.Min(2, program.ChordIntervals.Count - 1)];
            int rootCycles = (int)Math.Round(program.RootHz * count / (double)rate);
            int fifthCycles = (int)Math.Round(program.RootHz * fifthRatio * count / (double)rate);
            for (int n = 0; n < count; n++)
            {
                int bar = Math.Min(program.LoopBars - 1, n / barSamples);
                int cycles = bar % 2 == 0 ? rootCycles : fifthCycles;
                samples[n] += program.BassGain * SoftClip(Math.Sin(TwoPi * cycles * n / (double)count));
            }
        }

        // ---- normalize to one loudness, quantize ----------------------------
        double peak = 0.0;
        foreach (double sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        double scale = peak > 1e-12 ? NormalizedPeak / peak : 0.0;
        byte[] pcm = new byte[count * 2];
        for (int n = 0; n < count; n++)
        {
            short quantized = (short)Math.Clamp((int)Math.Round(samples[n] * scale * FullScale), -FullScale, FullScale);
            pcm[2 * n] = (byte)(quantized & 0xFF);
            pcm[(2 * n) + 1] = (byte)((quantized >> 8) & 0xFF);
        }

        return pcm;
    }

    /// <summary>
    /// Renders one state's loop as a canonical WAV file image (see
    /// <see cref="WavContainer"/>) — the on-disk artifact a human auditions
    /// (可听测).
    /// </summary>
    /// <exception cref="ArgumentNullException">program is null.</exception>
    /// <exception cref="ArgumentException">state is not a defined value.</exception>
    public static byte[] SynthesizeWav(BgmProgram program, BgmLayerState state) =>
        WavContainer.Wrap(program.SampleRateHz, SynthesizePcm(program, state));

    /// <summary>
    /// Percussion event list: kick on beats 1/3 of each bar, snare on 2/4,
    /// hats on the subdivision grid. Each event renders once into its own
    /// buffer (envelope × [swept sine | tone+noise | noise], 3 ms release
    /// ramp) and is added at its grid position — the grid repeats identically
    /// every loop, so loop periodicity holds.
    /// </summary>
    private static IEnumerable<(int Start, double[] Contribution)> PercussionEvents(
        BgmProgram program, int beatSamples, int totalBeats, int subdivision, int rate)
    {
        int kickSamples = (int)Math.Round(KickSec * rate);
        int snareSamples = (int)Math.Round(SnareSec * rate);
        int hatSamples = (int)Math.Round(HatSec * rate);
        double gain = program.PercussionGain;

        for (int beat = 0; beat < totalBeats; beat++)
        {
            int beatStart = beat * beatSamples;
            int beatInBar = beat % program.BeatsPerBar;

            if (beatInBar % 2 == 0)
            {
                double[] kick = new double[kickSamples];
                for (int i = 0; i < kickSamples; i++)
                {
                    double t = i / (double)rate;
                    double sweep = KickFromHz * Math.Pow(KickToHz / KickFromHz, t / KickSec);
                    kick[i] = gain * Math.Exp(-t / (KickSec / 2.0)) * Math.Sin(TwoPi * sweep * t)
                        * ReleaseRamp(i, kickSamples, rate);
                }

                yield return (beatStart, kick);
            }
            else
            {
                double[] snare = new double[snareSamples];
                uint seed = HitSeed(beat, 0);
                for (int i = 0; i < snareSamples; i++)
                {
                    double t = i / (double)rate;
                    snare[i] = gain * 0.8 * Math.Exp(-t / (SnareSec / 2.0)) *
                        ((0.45 * Math.Sin(TwoPi * SnareToneHz * t)) + (0.55 * NextNoise(ref seed))) *
                        ReleaseRamp(i, snareSamples, rate);
                }

                yield return (beatStart, snare);
            }

            for (int sub = 0; sub < program.HatSubdivisions; sub++)
            {
                int hit = beatStart + (sub * subdivision);
                if (hit >= beatStart + beatSamples)
                {
                    break; // subdivision coarser than the beat — no room for this sub
                }

                double[] hat = new double[hatSamples];
                uint seed = HitSeed(beat, sub + 1);
                for (int i = 0; i < hatSamples; i++)
                {
                    double t = i / (double)rate;
                    hat[i] = gain * 0.6 * Math.Exp(-t / (HatSec / 2.0)) * NextNoise(ref seed) *
                        ReleaseRamp(i, hatSamples, rate);
                }

                yield return (hit, hat);
            }
        }
    }

    /// <summary>Linear-to-zero ramp over the last <see cref="PercReleaseSec"/> of an event (click kill).</summary>
    private static double ReleaseRamp(int index, int length, int rate)
    {
        int release = (int)Math.Round(PercReleaseSec * rate);
        int remaining = length - index;
        return remaining < release ? Math.Max(remaining, 0) / (double)Math.Max(release, 1) : 1.0;
    }

    /// <summary>Soft saturation for the boss bass — brassy without hard edges.</summary>
    private static double SoftClip(double x) => Math.Tanh(1.8 * x);

    /// <summary>Per-hit deterministic noise seed (beat × voice — no cross-hit coupling).</summary>
    private static uint HitSeed(int beat, int voice) =>
        0x9E3779B9u ^ ((uint)beat * 0x85EBCA6Bu) ^ ((uint)voice * 0xC2B2AE35u);

    /// <summary>One 32-bit LCG step mapped to [-1, 1] — same seed ⇒ same sequence, every render.</summary>
    private static double NextNoise(ref uint state)
    {
        state = (state * 1664525u) + 1013904223u;
        return (state / (double)uint.MaxValue) * 2.0 - 1.0;
    }
}
