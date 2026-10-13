using System.Text;
using EraCore.Audio;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Tests of the code-synthesized three-tier cues (T-060, C-25, F-112): the
/// canonical PCM16-mono WAV container, the §10.3 cue shapes (880 Hz decaying
/// sine / double strike with gap / 220 Hz square), and the undefined-tier
/// guards. Signal checks run on the rendered samples themselves — nothing
/// is asserted from canned bytes.
/// </summary>
// [C-25][F-112] 三级音效代码合成：RIFF/WAV 容器 + 音色（880 正弦衰减/双击/220 方波）
public class DingSynthTests
{
    private const int BytesPerSample = 2;

    private static short[] Samples(DingTier tier)
    {
        byte[] pcm = DingSynth.SynthesizePcm(tier);
        short[] samples = new short[pcm.Length / BytesPerSample];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(pcm[2 * i] | (pcm[(2 * i) + 1] << 8));
        }

        return samples;
    }

    private static int CountSignChanges(short[] s)
    {
        int changes = 0;
        int lastSign = 0;
        foreach (short v in s)
        {
            int sign = Math.Sign(v);
            if (sign == 0)
            {
                continue; // attack/edge sample at digital silence
            }

            if (lastSign != 0 && sign != lastSign)
            {
                changes++;
            }

            lastSign = sign;
        }

        return changes;
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

    // ---- legal — durations and frame counts ----------------------------------

    [Theory] // legal — three tiers render three distinct cue lengths (0.080 / 0.250 / 0.300 s)
    [InlineData(DingTier.Info, 0.080)]
    [InlineData(DingTier.Reward, 0.250)] // strike 0.080 ×2 + gap 0.090
    [InlineData(DingTier.Crisis, 0.300)]
    public void Tier_durations_and_frame_counts(DingTier tier, double expectedSec)
    {
        Assert.Equal(expectedSec, DingSynth.DurationSec(tier), 3); // tolerance 1 ms = declared constants only

        int expectedSamples = (int)Math.Round(expectedSec * DingSynth.SampleRateHz);
        Assert.Equal(expectedSamples, DingSynth.SampleCount(tier));
        Assert.Equal(expectedSamples * BytesPerSample, DingSynth.SynthesizePcm(tier).Length);
    }

    [Theory] // legal — the WAV container is canonical PCM16 mono every player opens
    [InlineData(DingTier.Info)]
    [InlineData(DingTier.Reward)]
    [InlineData(DingTier.Crisis)]
    public void Wav_container_is_canonical_pcm16_mono(DingTier tier)
    {
        byte[] wav = DingSynth.SynthesizeWav(tier);
        byte[] pcm = DingSynth.SynthesizePcm(tier);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));                    // fmt chunk size
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));                     // PCM format
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));                     // mono
        Assert.Equal(DingSynth.SampleRateHz, BitConverter.ToInt32(wav, 24));
        Assert.Equal(DingSynth.SampleRateHz * BytesPerSample, BitConverter.ToInt32(wav, 28)); // byte rate
        Assert.Equal(BytesPerSample, BitConverter.ToInt16(wav, 32));        // block align
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));                    // bits per sample
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));
        Assert.Equal(36 + pcm.Length, BitConverter.ToInt32(wav, 4));        // RIFF size
        Assert.Equal(44 + pcm.Length, wav.Length);

        // the data chunk is exactly the PCM payload
        Assert.True(wav.AsSpan(44).SequenceEqual(pcm));
    }

    [Theory] // legal — every cue is audible (peak above half-scale) and inside the 16-bit range
    [InlineData(DingTier.Info)]
    [InlineData(DingTier.Reward)]
    [InlineData(DingTier.Crisis)]
    public void Cues_are_audible_and_within_range(DingTier tier)
    {
        short[] s = Samples(tier);

        Assert.True(MaxAbs(s) > 32767 * 0.5, $"peak {MaxAbs(s)} too quiet");

        foreach (short v in s)
        {
            Assert.True(Math.Abs(v) <= 32767, $"sample {v} outside 16-bit range");
        }
    }

    // ---- legal — the §10.3 timbres --------------------------------------------

    [Fact] // legal — 情报 "叮": a decaying ~880 Hz tone
    public void Info_cue_is_decaying_880hz_tone()
    {
        short[] s = Samples(DingTier.Info);

        // 880 Hz × 0.080 s × 2 crossings ≈ 141; ±12% tolerance covers the quantized decay tail
        Assert.InRange(CountSignChanges(s), 124, 158);

        // exponential decay: the last quarter carries far less energy than the first
        int quarter = s.Length / 4;
        double head = Rms(s, 0, quarter);
        double tail = Rms(s, s.Length - quarter, quarter);
        Assert.True(tail < head / 2.0, $"decay envelope: tail {tail:0.0} vs head {head:0.0}");
    }

    [Fact] // legal — 奖励 "叮叮": two strikes around one silent gap
    public void Reward_cue_is_double_strike_with_gap()
    {
        short[] s = Samples(DingTier.Reward);
        Assert.Equal(4000, s.Length); // 1280 strike + 1440 gap + 1280 strike

        // longest run of digital silence ≈ the 1440-sample gap (tolerance ±40 samples)
        int run = 0, best = 0, bestEnd = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == 0)
            {
                run++;
                if (run > best)
                {
                    best = run;
                    bestEnd = i;
                }
            }
            else
            {
                run = 0;
            }
        }

        Assert.InRange(best, 1400, 1480);

        int gapEnd = bestEnd + 1;
        int gapStart = (bestEnd - best) + 1;
        Assert.True(gapStart > 0 && gapEnd < s.Length, "the gap must sit strictly inside the cue");
        Assert.True(MaxAbs(s[..gapStart]) > 32767 * 0.5, "strike 1 before the gap must carry energy");
        Assert.True(MaxAbs(s[gapEnd..]) > 32767 * 0.5, "strike 2 after the gap must carry energy");
    }

    [Fact] // legal — 危机 "叮——警告！": a ~220 Hz square wave pinned near the rails
    public void Crisis_cue_is_220hz_square_wave()
    {
        short[] s = Samples(DingTier.Crisis);

        // 220 Hz × 0.300 s × 2 crossings = 132; ±10% tolerance
        Assert.InRange(CountSignChanges(s), 119, 146);

        short peak = MaxAbs(s);
        Assert.True(peak > 32767 * 0.5, $"peak {peak}");

        int atRails = 0;
        foreach (short v in s)
        {
            if (Math.Abs(v) >= 0.8 * peak)
            {
                atRails++;
            }
        }

        Assert.True(atRails >= (s.Length * 9) / 10, $"rail fraction {atRails}/{s.Length} too low for a square wave");
    }

    // ---- boundary — click-free edges ------------------------------------------

    [Fact] // boundary — every cue starts at digital silence (the fade ramp kills startup clicks)
    public void Every_cue_starts_at_digital_silence()
    {
        Assert.Equal(0, Samples(DingTier.Info)[0]);
        Assert.Equal(0, Samples(DingTier.Reward)[0]); // reward begins with strike 1's ramp
        Assert.Equal(0, Samples(DingTier.Crisis)[0]);
    }

    [Fact] // boundary — rendering is deterministic: same tier ⇒ identical bytes
    public void Synthesis_is_deterministic()
    {
        foreach (DingTier tier in Enum.GetValues<DingTier>())
        {
            Assert.True(
                DingSynth.SynthesizeWav(tier).SequenceEqual(DingSynth.SynthesizeWav(tier)),
                $"tier {tier} must render reproducible bytes");
        }
    }

    // ---- illegal — undefined tiers ---------------------------------------------

    [Theory] // illegal — undefined tier values are rejected by every entry point
    [InlineData(99)]
    [InlineData(-1)]
    public void Undefined_tier_is_rejected(int rawTier)
    {
        DingTier tier = (DingTier)rawTier;

        Assert.Throws<ArgumentException>(() => DingSynth.DurationSec(tier));
        Assert.Throws<ArgumentException>(() => DingSynth.SampleCount(tier));
        Assert.Throws<ArgumentException>(() => DingSynth.SynthesizePcm(tier));
        Assert.Throws<ArgumentException>(() => DingSynth.SynthesizeWav(tier));
    }
}
