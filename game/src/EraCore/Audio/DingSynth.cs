using System.Text;

namespace EraCore.Audio;

/// <summary>
/// Code-synthesized three-tier cue generator (C-25, T-060, F-112): renders
/// the §10.3 cue shapes — 情报 "叮" = 880 Hz sine with an exponential decay,
/// 奖励 "叮叮" = two strikes with a short silent gap, 危机 "叮——警告！" =
/// 220 Hz square wave — into 16-bit mono PCM at a fixed sample rate. No
/// binary assets enter the repository: the Godot adapter synthesizes the
/// streams at startup and persists the WAV files for human audition; T-092
/// replaces this table with licensed CC0 assets and personality timbres
/// behind the same <see cref="DingTier"/> key. Pure code (D-01): bytes in,
/// bytes out, fully deterministic — same tier ⇒ same bytes on every machine.
/// </summary>
public static class DingSynth
{
    /// <summary>Render sample rate (Hz) — 16 kHz mono is ample for three signaling cues.</summary>
    public const int SampleRateHz = 16000;

    /// <summary>情报 cue fundamental (§10.3 "叮").</summary>
    public const double InfoFrequencyHz = 880.0;

    /// <summary>情报 strike length (s).</summary>
    public const double InfoDurationSec = 0.080;

    /// <summary>Exponential decay time constant of a strike (s) — the "short decay" of the card.</summary>
    public const double InfoDecayTauSec = 0.020;

    /// <summary>Silent gap between the two 奖励 strikes (s).</summary>
    public const double RewardGapSec = 0.090;

    /// <summary>危机 fundamental (§10.3 "叮——警告！").</summary>
    public const double CrisisFrequencyHz = 220.0;

    /// <summary>危机 alarm length (s).</summary>
    public const double CrisisDurationSec = 0.300;

    /// <summary>Linear peak amplitude of every cue (16-bit full scale = 1.0).</summary>
    public const double PeakAmplitude = 0.6;

    /// <summary>Fade-in/out ramp (s) — kills startup clicks without shaping the tone.</summary>
    public const double FadeSec = 0.002;

    private const short FullScale = 32767;

    /// <summary>Total cue duration in seconds (奖励 = strike ×2 + gap).</summary>
    /// <exception cref="ArgumentException">tier is not a defined <see cref="DingTier"/> value.</exception>
    public static double DurationSec(DingTier tier) => tier switch
    {
        DingTier.Info => InfoDurationSec,
        DingTier.Reward => InfoDurationSec * 2.0 + RewardGapSec,
        DingTier.Crisis => CrisisDurationSec,
        _ => throw new ArgumentException($"Unknown ding tier {tier}.", nameof(tier)),
    };

    /// <summary>Cue length in samples (duration × <see cref="SampleRateHz"/>).</summary>
    public static int SampleCount(DingTier tier) =>
        (int)Math.Round(DurationSec(tier) * SampleRateHz);

    /// <summary>
    /// Renders one cue as raw PCM: 16-bit little-endian mono samples, no
    /// container — the exact payload <c>AudioStreamWav.Data</c> expects.
    /// </summary>
    /// <exception cref="ArgumentException">tier is not a defined value.</exception>
    public static byte[] SynthesizePcm(DingTier tier)
    {
        int count = SampleCount(tier);
        byte[] pcm = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            double value = tier switch
            {
                DingTier.Info => Strike(i),
                DingTier.Reward => RewardSample(i),
                DingTier.Crisis => Alarm(i),
                _ => throw new ArgumentException($"Unknown ding tier {tier}.", nameof(tier)),
            };

            short quantized = (short)Math.Clamp((int)Math.Round(value * FullScale), -FullScale, FullScale);
            pcm[2 * i] = (byte)(quantized & 0xFF);
            pcm[(2 * i) + 1] = (byte)((quantized >> 8) & 0xFF);
        }

        return pcm;
    }

    /// <summary>
    /// Renders one cue as a canonical 44-byte-header WAV file (RIFF/WAVE,
    /// PCM16, mono) — the on-disk artifact a human auditions (可听测).
    /// </summary>
    /// <exception cref="ArgumentException">tier is not a defined value.</exception>
    public static byte[] SynthesizeWav(DingTier tier)
    {
        byte[] pcm = SynthesizePcm(tier);
        using MemoryStream stream = new(44 + pcm.Length);
        using BinaryWriter writer = new(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);                      // fmt chunk size
        writer.Write((short)1);                // PCM
        writer.Write((short)1);                // mono
        writer.Write(SampleRateHz);
        writer.Write(SampleRateHz * 2);        // byte rate = rate × 1 ch × 2 B
        writer.Write((short)2);                // block align
        writer.Write((short)16);               // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>One 880 Hz strike sample at index <paramref name="i"/>: exp-decayed sine with a short attack ramp.</summary>
    private static double Strike(int i)
    {
        double t = i / (double)SampleRateHz;
        double envelope = Math.Exp(-t / InfoDecayTauSec);
        if (t < FadeSec)
        {
            envelope *= t / FadeSec;
        }

        // t = 0 lands on sin(0) = 0 → the cue starts at digital silence.
        return PeakAmplitude * envelope * Math.Sin(2.0 * Math.PI * InfoFrequencyHz * t);
    }

    /// <summary>奖励 sample at index <paramref name="i"/>: strike, gap of digital silence, strike again.</summary>
    private static double RewardSample(int i)
    {
        int strikeSamples = (int)Math.Round(InfoDurationSec * SampleRateHz);
        int gapSamples = (int)Math.Round(RewardGapSec * SampleRateHz);
        if (i < strikeSamples)
        {
            return Strike(i);
        }

        int secondStart = strikeSamples + gapSamples;
        return i < secondStart ? 0.0 : Strike(i - secondStart);
    }

    /// <summary>危机 sample at index <paramref name="i"/>: 220 Hz square wave with click-free edges.</summary>
    private static double Alarm(int i)
    {
        double t = i / (double)SampleRateHz;
        double envelope = 1.0;
        if (t < FadeSec)
        {
            envelope *= t / FadeSec;
        }

        double remain = CrisisDurationSec - t;
        if (remain < FadeSec)
        {
            envelope *= Math.Max(remain, 0.0) / FadeSec;
        }

        return PeakAmplitude * envelope * Math.Sign(Math.Sin(2.0 * Math.PI * CrisisFrequencyHz * t));
    }
}
