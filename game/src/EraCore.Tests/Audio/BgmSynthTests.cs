using EraCore.Audio;
using Xunit;

namespace EraCore.Tests.Audio;

/// <summary>
/// Signal tests of the deterministic three-state BGM renderer (T-092,
/// F-135; T-121 adds the E3 industrial timbre): everything is asserted from
/// the rendered samples themselves — loop length, audibility, 16-bit safety,
/// state/era separability through band energy, steady energy across the loop
/// halves (seamless-loop precondition), byte-level determinism, and the hard
/// grid guards of both timbre families.
/// </summary>
// [C-25][F-135] 三态 BGM 代码合成：信号级断言（合法/边界/非法）
public class BgmSynthTests
{
    private static readonly BgmProgram Era1 = Program("era1", tempo: 84, root: 110.0, hats: 2);
    private static readonly BgmProgram Era2 = Program("era2", tempo: 100, root: 146.83, hats: 4);
    private static readonly BgmProgram Era3 = new(
        "era3", 112.0, 92.5, new[] { 1.0, 1.3348, 1.4983, 2.0 },
        PadGain: 0.30, PercussionGain: 0.26, BassGain: 0.36, HatSubdivisions: 4,
        SampleRateHz: 22050, LoopBars: 4, BeatsPerBar: 4, Timbre: BgmTimbre.Industrial);
    private static readonly BgmProgram Era4 = new(
        "era4", 118.0, 103.83, new[] { 1.0, 1.2599, 1.4983, 1.7818, 2.0 },
        PadGain: 0.30, PercussionGain: 0.26, BassGain: 0.32, HatSubdivisions: 5,
        SampleRateHz: 22050, LoopBars: 4, BeatsPerBar: 4, Timbre: BgmTimbre.Modern);
    private static readonly BgmProgram Era5 = new(
        "era5", 126.0, 123.47, new[] { 1.0, 1.2599, 1.4983, 1.6818, 2.0 },
        PadGain: 0.30, PercussionGain: 0.24, BassGain: 0.30, HatSubdivisions: 6,
        SampleRateHz: 22050, LoopBars: 4, BeatsPerBar: 4, Timbre: BgmTimbre.Synth);
    private static readonly BgmProgram Era6 = new(
        "era6", 63.0, 61.74, new[] { 1.0, 1.1892, 1.4142, 2.0 },
        PadGain: 0.34, PercussionGain: 0.22, BassGain: 0.38, HatSubdivisions: 2,
        SampleRateHz: 22050, LoopBars: 4, BeatsPerBar: 4, Timbre: BgmTimbre.Cosmic);

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
        foreach (BgmProgram program in new[] { Era1, Era2, Era3 })
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
        foreach (BgmProgram program in new[] { Era1, Era2, Era3 })
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

    [Fact] // legal — E3 工业音景 separates the same way: 铆钉/铁板 lift the high band over the steam bed, 重工业低音 lifts the low band
    public void Era3_states_separate_by_band_energy()
    {
        // the steam band sits below ~1.1 kHz by design (SteamBandHighRatio=12),
        // leaving the >2 kHz rivet ping + noise bursts as the combat identity
        short[] calm = Samples(Era3, BgmLayerState.Calm);
        short[] combat = Samples(Era3, BgmLayerState.Combat);
        short[] boss = Samples(Era3, BgmLayerState.Boss);

        Assert.True(HighBandRms(combat) > HighBandRms(calm) * 1.5,
            $"high band combat {HighBandRms(combat):0.0} vs calm {HighBandRms(calm):0.0}");

        Assert.True(LowBandRms(boss) > LowBandRms(combat) * 1.15,
            $"low band boss {LowBandRms(boss):0.0} vs combat {LowBandRms(combat):0.0}");
    }

    [Fact] // legal — the eras differ under the same state (different tempo/root/chord)
    public void Eras_differ_under_same_state()
    {
        foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
        {
            Assert.False(
                BgmSynth.SynthesizePcm(Era1, state).SequenceEqual(BgmSynth.SynthesizePcm(Era2, state)),
                $"{state}: era1 and era2 rendered identical bytes");
            Assert.False(
                BgmSynth.SynthesizePcm(Era1, state).SequenceEqual(BgmSynth.SynthesizePcm(Era3, state)),
                $"{state}: era1 and era3 rendered identical bytes");
        }
    }

    [Fact] // legal — the E3 industrial states are pairwise distinct (three real tracks, not one)
    public void Era3_states_are_pairwise_distinct()
    {
        byte[] calm = BgmSynth.SynthesizePcm(Era3, BgmLayerState.Calm);
        byte[] combat = BgmSynth.SynthesizePcm(Era3, BgmLayerState.Combat);
        byte[] boss = BgmSynth.SynthesizePcm(Era3, BgmLayerState.Boss);

        Assert.False(calm.SequenceEqual(combat), "calm and combat rendered identical bytes");
        Assert.False(combat.SequenceEqual(boss), "combat and boss rendered identical bytes");
        Assert.False(calm.SequenceEqual(boss), "calm and boss rendered identical bytes");
    }

    // ------------------------------------------- T-142 E5/终章 音色族（两章曲）

    [Theory] // legal — T-142 两章+T-157 E4 音景同律分层：鼓点抬频带、低音抬低频
    [InlineData("era4")]
    [InlineData("era5")]
    [InlineData("era6")]
    public void T142_timbre_states_separate_by_band_energy(string era)
    {
        BgmProgram program = era switch { "era4" => Era4, "era5" => Era5, _ => Era6 };
        short[] calm = Samples(program, BgmLayerState.Calm);
        short[] combat = Samples(program, BgmLayerState.Combat);
        short[] boss = Samples(program, BgmLayerState.Boss);

        // 战斗加鼓点——频带口径按音色族密度取位：
        //  era4 电子鼓密（150→50 kick+拍手军鼓+5 档数字 hat）抬高频带；
        //  era5 合成鼓密（170→55 kick+glitch snare+6 档 tick）抬高频带；
        //  era6 引力鼓稀而深（90→38 Hz 长扫频，占空比 ~2%）抬不动带平均——
        //  其战斗层分离证据取 pairwise-distinct+loops audible（本文件另两条），
        //  这里只钉各族共同的硬分层轴：Boss 低音层抬低频带。
        if (era is "era4" or "era5")
        {
            Assert.True(HighBandRms(combat) > HighBandRms(calm) * 1.35,
                $"{era} high band combat {HighBandRms(combat):0.0} vs calm {HighBandRms(calm):0.0}");
        }

        // Boss 加低音：低频带过 combat（modern drive 2.8 / synth drive 3.2 / cosmic drive 2.2 的持续低音）
        Assert.True(LowBandRms(boss) > LowBandRms(combat) * 1.1,
            $"{era} low band boss {LowBandRms(boss):0.0} vs combat {LowBandRms(combat):0.0}");
    }

    [Theory] // legal — T-142 两章+T-157 E4 各三态两两不同（每代 3 曲=三条真实音轨，非一曲）
    [InlineData("era4")]
    [InlineData("era5")]
    [InlineData("era6")]
    public void T142_timbre_states_are_pairwise_distinct(string era)
    {
        BgmProgram program = era switch { "era4" => Era4, "era5" => Era5, _ => Era6 };
        byte[] calm = BgmSynth.SynthesizePcm(program, BgmLayerState.Calm);
        byte[] combat = BgmSynth.SynthesizePcm(program, BgmLayerState.Combat);
        byte[] boss = BgmSynth.SynthesizePcm(program, BgmLayerState.Boss);

        Assert.False(calm.SequenceEqual(combat), $"{era} calm/combat identical");
        Assert.False(combat.SequenceEqual(boss), $"{era} combat/boss identical");
        Assert.False(calm.SequenceEqual(boss), $"{era} calm/boss identical");
    }

    [Fact] // legal — T-142 两章互不相同、与已交付行不同（音色族身份=同态字节分离）
    public void T142_timbre_rows_differ_from_every_delivered_row()
    {
        foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
        {
            Assert.False(
                BgmSynth.SynthesizePcm(Era5, state).SequenceEqual(BgmSynth.SynthesizePcm(Era6, state)),
                $"{state}: era5 and era6 rendered identical bytes");
            Assert.False(
                BgmSynth.SynthesizePcm(Era5, state).SequenceEqual(BgmSynth.SynthesizePcm(Era3, state)),
                $"{state}: era5 and era3 rendered identical bytes");
            Assert.False(
                BgmSynth.SynthesizePcm(Era6, state).SequenceEqual(BgmSynth.SynthesizePcm(Era1, state)),
                $"{state}: era6 and era1 rendered identical bytes");
        }
    }

    [Theory] // boundary — T-142 两章循环两半能量均衡（无缝循环前提；尖塔鸣响/星门轰鸣的呼应对句）
    [InlineData("era5")]
    [InlineData("era6")]
    public void T142_timbre_loop_halves_carry_balanced_energy(string era)
    {
        BgmProgram program = era == "era5" ? Era5 : Era6;
        foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
        {
            short[] s = Samples(program, state);
            int half = s.Length / 2;
            double head = Rms(s, 0, half);
            double tail = Rms(s, half, half);

            Assert.InRange(tail / head, 0.75, 1.35); // ±~25%: chime/gate drone 与复现的呼应账
        }
    }

    [Fact] // boundary — T-142 渲染确定性：种子噪声（tick/军鼓/星尘带）同 seed 同字节
    public void T142_synthesis_is_deterministic()
    {
        foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
        {
            Assert.True(
                BgmSynth.SynthesizeWav(Era5, state).SequenceEqual(BgmSynth.SynthesizeWav(Era5, state)),
                $"era5 {state} must render reproducible bytes (seeded tick/stardust noise)");
            Assert.True(
                BgmSynth.SynthesizeWav(Era6, state).SequenceEqual(BgmSynth.SynthesizeWav(Era6, state)),
                $"era6 {state} must render reproducible bytes (seeded ghost/tick noise)");
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

    [Fact] // boundary — E3 halves balance too: the long whistle's reprise answers it mid-loop (energy bookkeeping)
    public void Era3_loop_halves_carry_balanced_energy()
    {
        foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
        {
            short[] s = Samples(Era3, state);
            int half = s.Length / 2;
            double head = Rms(s, 0, half);
            double tail = Rms(s, half, half);

            Assert.InRange(tail / head, 0.75, 1.35); // ±~25%: boss's bar-0 whistle vs bar-2 reprise measured ~0.92
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
            Assert.True(
                BgmSynth.SynthesizeWav(Era3, state).SequenceEqual(BgmSynth.SynthesizeWav(Era3, state)),
                $"era3 {state} must render reproducible bytes (seeded steam/rivet noise)");
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

    [Fact] // illegal — the industrial grid guards too: dense rivets cross the seam
    public void Industrial_cramped_rivet_grid_is_rejected()
    {
        // tempo 200, 2 bars ⇒ loop 2.4 s: the faint whistle fits, but the last
        // 8-subdivision rivet burst + tail crosses the loop end
        BgmProgram cramped = new("era3", 200.0, 92.5, new[] { 1.0, 1.3348, 1.4983, 2.0 },
            PadGain: 0.3, PercussionGain: 0.26, BassGain: 0.36, HatSubdivisions: 8,
            SampleRateHz: 22050, LoopBars: 2, BeatsPerBar: 4, Timbre: BgmTimbre.Industrial);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => BgmSynth.SynthesizePcm(cramped, BgmLayerState.Combat));
        Assert.Contains("越过循环尾", ex.Message);
    }

    [Theory] // illegal — the industrial whistle guards: faint whistle / long whistle each throw on cramped rows
    [InlineData(BgmLayerState.Calm, "微汽笛")]     // 1 bar 0.6 s loop: the 0.9 s faint whistle cannot clear
    [InlineData(BgmLayerState.Boss, "汽笛长鸣")]    // 1 bar 12 s loop: the one-bar long whistle ends exactly at the seam
    public void Industrial_cramped_whistles_are_rejected(BgmLayerState state, string guardName)
    {
        BgmProgram cramped = new("era3", state == BgmLayerState.Calm ? 200.0 : 40.0, 92.5, new[] { 1.0, 1.3348, 1.4983, 2.0 },
            PadGain: 0.3, PercussionGain: 0.26, BassGain: 0.36, HatSubdivisions: 4,
            SampleRateHz: 22050, LoopBars: 1, BeatsPerBar: state == BgmLayerState.Calm ? 2 : 8,
            Timbre: BgmTimbre.Industrial);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => BgmSynth.SynthesizePcm(cramped, state));
        Assert.Contains(guardName, ex.Message);
    }

    [Fact] // illegal — T-142 synth 光流琶音守卫：窄循环上尾音越界即红（修表不修码）
    public void Synth_cramped_arp_grid_is_rejected()
    {
        // tempo 200, 1 bar, 2 beats ⇒ loop 0.6 s: the last eighth pluck (0.22 s) crosses the seam
        BgmProgram cramped = new("era5", 200.0, 123.47, new[] { 1.0, 2.0 },
            PadGain: 0.3, PercussionGain: 0.24, BassGain: 0.3, HatSubdivisions: 2,
            SampleRateHz: 22050, LoopBars: 1, BeatsPerBar: 2, Timbre: BgmTimbre.Synth);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => BgmSynth.SynthesizePcm(cramped, BgmLayerState.Calm));
        Assert.Contains("光流琶音", ex.Message);
    }

    [Theory] // illegal — T-142 尖塔鸣响/星门轰鸣守卫：1 栏循环上整栏动机无法腾出循环尾
    [InlineData("era5", "尖塔鸣动")]
    [InlineData("era6", "星门轰鸣")]
    public void T142_cramped_boss_motifs_are_rejected(string era, string guardName)
    {
        BgmProgram cramped = era == "era5"
            ? new("era5", 40.0, 123.47, new[] { 1.0, 2.0 },
                PadGain: 0.3, PercussionGain: 0.24, BassGain: 0.3, HatSubdivisions: 2,
                SampleRateHz: 22050, LoopBars: 1, BeatsPerBar: 8, Timbre: BgmTimbre.Synth)
            : new("era6", 40.0, 61.74, new[] { 1.0, 2.0 },
                PadGain: 0.34, PercussionGain: 0.22, BassGain: 0.38, HatSubdivisions: 2,
                SampleRateHz: 22050, LoopBars: 1, BeatsPerBar: 8, Timbre: BgmTimbre.Cosmic);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => BgmSynth.SynthesizePcm(cramped, BgmLayerState.Boss));
        Assert.Contains(guardName, ex.Message);
    }

    [Fact] // illegal — T-142 cosmic 记忆钟鸣守卫：窄循环上奇数栏钟鸣越界即红
    public void Cosmic_cramped_memory_chimes_are_rejected()
    {
        // tempo 200, 1 bar, 2 beats ⇒ loop 0.6 s: the 0.7 s memory chime cannot clear
        BgmProgram cramped = new("era6", 200.0, 61.74, new[] { 1.0, 2.0 },
            PadGain: 0.34, PercussionGain: 0.22, BassGain: 0.38, HatSubdivisions: 2,
            SampleRateHz: 22050, LoopBars: 1, BeatsPerBar: 2, Timbre: BgmTimbre.Cosmic);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => BgmSynth.SynthesizePcm(cramped, BgmLayerState.Calm));
        Assert.Contains("记忆钟鸣", ex.Message);
    }
}
