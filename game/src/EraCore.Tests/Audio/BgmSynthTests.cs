using EraCore.Audio;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Signal tests of the deterministic three-state BGM renderer (T-092,
/// F-135): everything is asserted from the rendered samples themselves —
/// loop length, audibility, 16-bit safety, state/era separability through
/// band energy, steady energy across the loop halves (seamless-loop
/// precondition), byte-level determinism, and the two hard guards.
/// </summary>
// [C-25][F-135] 三态 BGM 代码合成：信号级断言（合法/边界/非法）
public class BgmSynthTests
{
    private static readonly BgmProgram Era1 = Program("era1", tempo: 84, root: 110.0, hats: 2);
    private static readonly BgmProgram Era2 = Program("era2", tempo: 100, root: 146.83, hats: 4);

    private static BgmProgram Program(string era, double tempo, double root, int hats) =>
        new(era, tempo, root, new[] { 1.0, 1.1892, 1.4983, 2.0 },
            PadGain: 0.34, PercussionGain: 0.20, BassGain: 0.24, HatSubdivisions: hats,
            SampleRateHz: 22050, LoopBars: 2, BeatsPerBar: 4);

    private static short[] Samples(BgmProgram program, BgmLayerState state)
    {
        byte[] pcm = BgmSynth.SynthesizePcm(program, state);
        short[] samples = new short[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(pcm[2 * i] | (pcm[(2 * i) + 1] << 8));
        }

        return samples;
    }

    private static double Rms(short[] s, int start, int count)
    {
        double acc = 0.0;
        for (int i = start; i < start + count; i++)
        {
            acc += (double)s[i] * s[i];
        }

        return Math.Sqrt(acc / count);
    }

    /// <summary>High-frequency proxy: RMS of the first difference (a one-pole high-pass at Nyquist/2).</summary>
    private static double HighBandRms(short[] s)
    {
        double acc = 0.0;
        for (int i = 1; i < s.Length; i++)
        {
            double d = s[i] - s[i - 1];
            acc += d * d;
        }

        return Math.Sqrt(acc / (s.Length - 1));
    }

    /// <summary>Low-frequency proxy: RMS of a 23-sample moving average (~960 Hz ceiling at 22050 Hz).</summary>
    private static double LowBandRms(short[] s)
    {
        double acc = 0.0;
        int window = 23;
        double sum = 0;
        int counted = 0;
        for (int i = 0; i < s.Length; i++)
        {
            sum += s[i];
            if (i >= window)
            {
                sum -= s[i - window];
            }

            if (i >= window - 1)
            {
                double avg = sum / window;
                acc += avg * avg;
                counted++;
            }
        }

        return Math.Sqrt(acc / Math.Max(counted, 1));
    }

    private static short MaxAbs(short[] s)
    {
        short peak = 0;
        foreach (short v in s)
        {
            int magnitude = Math.Abs(v);
            if (magnitude > peak)
            {
                peak = (short)magnitude;
            }
        }

        return peak;
    }

    // ---------------------------------------------------------------- legal

    [Fact] // legal — six loops render at exactly the derived loop length
    public void Every_loop_renders_at_derived_length()
    {
        foreach (BgmProgram program in new[] { Era1, Era2 })
        {
            foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
            {
                byte[] pcm = BgmSynth.SynthesizePcm(program, state);

                Assert.Equal(program.LoopSamples * 2, pcm.Length);
            }
        }
    }

    [Fact] // legal — every loop is audible, peak-normalized, and inside the 16-bit range
    public void Loops_are_audible_and_within_range()
    {
        foreach (BgmProgram program in new[] { Era1, Era2 })
        {
            foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
            {
                short[] s = Samples(program, state);
                short peak = MaxAbs(s);

                Assert.True(peak > 32767 * 0.55, $"{program.Era}/{state}: peak {peak} too quiet");
                Assert.True(peak <= 32767, $"{program.Era}/{state}: peak {peak} clipped");
                foreach (short v in s)
                {
                    Assert.True(Math.Abs(v) <= 32767);
                }
            }
        }
    }

    [Fact] // legal — state separability: percussion lifts the high band, bass lifts the low band
    public void States_separate_by_band_energy()
    {
        short[] calm = Samples(Era1, BgmLayerState.Calm);
        short[] combat = Samples(Era1, BgmLayerState.Combat);
        short[] boss = Samples(Era1, BgmLayerState.Boss);

        // 战斗加鼓点: hats/snare put broadband energy where the pad has none
        // (era1's sparse 2-subdivision grid measures ~1.6x; 1.5x keeps the
        // assertion robust while still proving the layer is audible)
        Assert.True(HighBandRms(combat) > HighBandRms(calm) * 1.5,
            $"high band combat {HighBandRms(combat):0.0} vs calm {HighBandRms(calm):0.0}");

        // Boss 加低音: the bass line lifts the low band over combat
        Assert.True(LowBandRms(boss) > LowBandRms(combat) * 1.15,
            $"low band boss {LowBandRms(boss):0.0} vs combat {LowBandRms(combat):0.0}");
    }

    [Fact] // legal — the two eras differ under the same state (different tempo/root/chord)
    public void Eras_differ_under_same_state()
    {
        foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
        {
            Assert.False(
                BgmSynth.SynthesizePcm(Era1, state).SequenceEqual(BgmSynth.SynthesizePcm(Era2, state)),
                $"{state}: era1 and era2 rendered identical bytes");
        }
    }

    [Fact] // legal — WAV container round-trip: header canonical, payload = PCM
    public void Wav_image_is_canonical()
    {
        byte[] pcm = BgmSynth.SynthesizePcm(Era1, BgmLayerState.Calm);
        byte[] wav = BgmSynth.SynthesizeWav(Era1, BgmLayerState.Calm);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));            // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));            // mono
        Assert.Equal(Era1.SampleRateHz, BitConverter.ToInt32(wav, 24));
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));
        Assert.True(wav.AsSpan(44).SequenceEqual(pcm));
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — energy is steady across the loop halves: no edge ramp → seamless wrap
    public void Loop_halves_carry_balanced_energy()
    {
        foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
        {
            short[] s = Samples(Era1, state);
            int half = s.Length / 2;
            double head = Rms(s, 0, half);
            double tail = Rms(s, half, half);

            Assert.InRange(tail / head, 0.75, 1.35); // ±~25%: the grid repeats, nothing ramps at the seam
        }
    }

    [Fact] // boundary — rendering is deterministic: same program+state ⇒ identical bytes
    public void Synthesis_is_deterministic()
    {
        foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
        {
            Assert.True(
                BgmSynth.SynthesizeWav(Era1, state).SequenceEqual(BgmSynth.SynthesizeWav(Era1, state)),
                $"{state} must render reproducible bytes");
        }
    }

    // -------------------------------------------------------------- illegal

    [Fact] // illegal — undefined state values are rejected
    public void Undefined_state_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => BgmSynth.SynthesizePcm(Era1, (BgmLayerState)99));
        Assert.Throws<ArgumentException>(() => BgmSynth.SynthesizeWav(Era1, (BgmLayerState)(-1)));
    }

    [Fact] // illegal — null program is rejected
    public void Null_program_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => BgmSynth.SynthesizePcm(null!, BgmLayerState.Calm));
    }

    [Fact] // illegal — a beat grid with no loop-end clearance throws instead of clicking
    public void Cramped_grid_is_rejected()
    {
        // tempo 200, 1 bar, 2 beats ⇒ loop 0.6 s; the last hat + 0.3 s tail crosses the seam
        BgmProgram cramped = new("era1", 200.0, 110.0, new[] { 1.0, 2.0 },
            PadGain: 0.3, PercussionGain: 0.2, BassGain: 0.25, HatSubdivisions: 4,
            SampleRateHz: 22050, LoopBars: 1, BeatsPerBar: 2);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => BgmSynth.SynthesizePcm(cramped, BgmLayerState.Combat));
        Assert.Contains("越过循环尾", ex.Message);
    }
}
