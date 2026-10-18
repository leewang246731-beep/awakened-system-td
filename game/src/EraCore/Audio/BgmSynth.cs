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
/// §14; T-121 adds the E3 industrial timbre): renders one era's program into
/// a loop per state. Zero binary assets and zero engine types (D-01). Every
/// tonal component is quantized to an INTEGER number of cycles per loop and
/// every percussion hit is a pure function of its offset from a fixed
/// beat-grid position (noise from a per-hit seeded LCG — no shared
/// <c>Random</c>, CS-1.4), so the rendered sample sequence is exactly
/// loop-periodic (seamless looping) and same-machine reproducible (same
/// program+state ⇒ same bytes; the DingSynth W2 批 3 F7 wording applies — no
/// cross-architecture bit-level claim, the persisted WAV snapshot is the
/// audition/regression anchor).
/// </summary>
/// <remarks>
/// Timbre families (T-121/T-142/T-157, table-driven): <c>Acoustic</c> keeps the
/// T-092 voice set verbatim (byte-identical E1/E2). <c>Industrial</c> renders
/// the E3 soundscape of the 31 号 visual identity card — calm = 蒸汽垫底
/// (steam-hiss additive noise band, exactly periodic) + 低频脉冲 (sub-root
/// pulse throb, one per beat) + 微汽笛 (faint short whistle on the odd
/// bars); combat = +金属打击 (inharmonic-partials clang on beats 1/3) +
/// 铆钉动机 (seeded-noise rivet strikes on the subdivision grid of beats
/// 2/4); boss = +重工业低音 (harder-driven alternating root/fifth bass) +
/// 汽笛长鸣动机 (one-bar long whistle at the loop head with a half-bar
/// fifth reprise mid-loop — call and response, the Bastion's horn).
/// <c>Modern</c> (T-157, E4 现代) — calm = 市电嗡鸣 (detuned dual-winding
/// hum over the root octave) + 城市脉冲 (sub-root load throb, one cycle per
/// two beats) + 无线电点 (bar-head high beacon blips); combat = +电子鼓
/// (150→50 Hz kick / double-tap clap snare / digital hats); boss = +都市
/// 低音 (drive 2.8) + 都市警笛动机 (FM-swept dual-tone siren in opposition
/// with a half-bar fifth reprise).
/// <c>Synth</c> (T-142, E5 未来) — calm = 聚变脉冲 (softer sub-root
/// per-beat throb) + 光流琶音 (chord-tone octave-alternating plucks on the
/// eighth grid) + 全息点 (bar-head high blips); combat = +合成鼓
/// (170→55 Hz kick / glitch snare / metallic ticks); boss = +聚变低音
/// (drive 3.2) + 尖塔鸣响动机 (harmonic-partial chime with fifth reprise).
/// <c>Cosmic</c> (T-142, 终章 深空) — calm = 零点脉冲 (one-cycle-per-bar
/// sub-root drift) + 星尘带 (sparse high additive band, 记忆光尘) + 记忆
/// 钟鸣 (odd-bar faint chimes); combat = +引力鼓 (90→38 Hz long deep kick /
/// ghost snares / sparse ticks); boss = +透镜低音 (drive 2.2) + 星门轰鸣
/// 动机 (detuned fifth-stack drone with sub-octave weight, fifth reprise).
/// </remarks>
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

    // ---- T-121 industrial timbre (E3 音景, 31 号视觉身份卡) ------------------

    /// <summary>低频脉冲基音 = 根音下八度（×0.5）——机器搏动.</summary>
    public const double SubPulseRatio = 0.5;

    /// <summary>蒸汽嘶鸣垫的加性噪声分量数（每个都整数周期化 ⇒ 严格 N-周期）.</summary>
    public const int SteamComponents = 21;

    /// <summary>蒸汽噪声带下沿（×根音）——留在低频，把 &gt;2 kHz 让给交战层.</summary>
    public const double SteamBandLowRatio = 4.0;

    /// <summary>蒸汽噪声带上沿（×根音）.</summary>
    public const double SteamBandHighRatio = 12.0;

    /// <summary>双管汽笛失谐（± ratio）——拍频合唱感.</summary>
    public const double WhistleDetune = 0.0035;

    /// <summary>微汽笛时长（s）——calm 层奇数栏各一声（码头远汽笛）.</summary>
    public const double FaintWhistleSec = 0.9;

    /// <summary>微汽笛起音（s，升余弦窗）.</summary>
    public const double FaintWhistleAttackSec = 0.12;

    /// <summary>微汽笛收音（s，升余弦窗）.</summary>
    public const double FaintWhistleReleaseSec = 0.25;

    /// <summary>汽笛长鸣起音（s）.</summary>
    public const double WhistleAttackSec = 0.15;

    /// <summary>汽笛长鸣收音（s）.</summary>
    public const double WhistleReleaseSec = 0.3;

    /// <summary>长鸣复现的音高（纯五度上方）与增益系数（呼应句）.</summary>
    public const double WhistleRepriseInterval = 1.4983;

    /// <summary>长鸣复现时长 = 半小节；增益 = 长鸣×本系数.</summary>
    public const double WhistleRepriseGain = 0.6;

    /// <summary>金属打击（铁板 clang）时长（s）.</summary>
    public const double ClangSec = 0.22;

    /// <summary>金属打击衰减时间常数（s）.</summary>
    public const double ClangDecaySec = 0.08;

    /// <summary>铁板基音 = 根音上八度（×2）.</summary>
    public const double ClangPitchRatio = 2.0;

    /// <summary>铆钉枪单发时长（s）.</summary>
    public const double RivetSec = 0.045;

    /// <summary>铆钉噪声体衰减时间常数（s）.</summary>
    public const double RivetDecaySec = 0.012;

    /// <summary>铆钉金属泛音（×根音 ≈ 2.4 kHz）——高频段身份.</summary>
    public const double RivetPingRatio = 26.0;

    /// <summary>重工业低音的软削波驱动（acoustic=1.8；更大=更密的谐波）.</summary>
    public const double IndustrialBassDrive = 2.6;

    // ---- T-157 modern timbre (E4 现代音景, 31 号 §1.2 E4 身份卡) ------------

    /// <summary>市电嗡鸣基音 = 根音上八度（×2）——双绕组失谐对的变压器哼鸣.</summary>
    public const double MainsHumRatio = 2.0;

    /// <summary>市电嗡鸣失谐（± ratio）——双绕组拍频（变压器哼鸣的粗粝感）.</summary>
    public const double MainsHumDetune = 0.006;

    /// <summary>城市脉冲基音 = 根音下八度（×0.5）——每两拍一周的电网负载搏动.</summary>
    public const double GridPulseRatio = 0.5;

    /// <summary>无线电点频率（×根音——高频信标闪点）.</summary>
    public const double RadioBlipRatio = 18.0;

    /// <summary>无线电点时长（s）.</summary>
    public const double RadioBlipSec = 0.05;

    /// <summary>无线电点增益系数（×pad_gain）.</summary>
    public const double RadioBlipGainFactor = 0.28;

    /// <summary>电子鼓 kick 扫频起点（Hz）.</summary>
    public const double ModernKickFromHz = 150.0;

    /// <summary>电子鼓 kick 扫频终点（Hz）.</summary>
    public const double ModernKickToHz = 50.0;

    /// <summary>电子鼓 kick 时长（s）.</summary>
    public const double ModernKickSec = 0.1;

    /// <summary>电子拍手军鼓时长（s）.</summary>
    public const double ClapSnareSec = 0.08;

    /// <summary>数字 hat 时长（s）.</summary>
    public const double DigitalHatSec = 0.014;

    /// <summary>都市低音的软削波驱动（声学 1.8/工业 2.6/合成 3.2 之间——都市的稳态重量）.</summary>
    public const double ModernBassDrive = 2.8;

    /// <summary>都市警笛的音高摆幅（± semitones around the two-pipe base）.</summary>
    public const double SirenSweepSemitones = 3.0;

    /// <summary>警笛动机复现时长 = 半小节；增益 = 长鸣×本系数（汽笛长鸣同构）.</summary>
    public const double SirenRepriseGain = 0.6;

    // ---- T-142 synth timbre (E5 未来音景, 31 号 §1.2 E5 行) -----------------

    /// <summary>聚变脉冲基音 = 根音下八度（×0.5）——逐拍升余弦搏动（聚变堆心跳）.</summary>
    public const double SynthPulseRatio = 0.5;

    /// <summary>光流琶音的细分密度（每拍 2 步=八分音网格——光流线路的流动感）.</summary>
    public const int ArpSubdivisionsPerBeat = 2;

    /// <summary>光流琶音增益系数（×pad_gain）.</summary>
    public const double ArpGainFactor = 0.5;

    /// <summary>光流琶音单音时长（s）.</summary>
    public const double ArpPluckSec = 0.22;

    /// <summary>光流琶音起音（s，升余弦窗）.</summary>
    public const double ArpAttackSec = 0.01;

    /// <summary>光流琶音收音（s，升余弦窗——click kill）.</summary>
    public const double ArpReleaseSec = 0.08;

    /// <summary>全息点频率（×根音——高频闪点）.</summary>
    public const double HoloBlipRatio = 16.0;

    /// <summary>全息点时长（s）.</summary>
    public const double HoloBlipSec = 0.06;

    /// <summary>全息点增益系数（×pad_gain）.</summary>
    public const double HoloBlipGainFactor = 0.3;

    /// <summary>合成鼓 kick 扫频起点（Hz）.</summary>
    public const double SynthKickFromHz = 170.0;

    /// <summary>合成鼓 kick 扫频终点（Hz）.</summary>
    public const double SynthKickToHz = 55.0;

    /// <summary>合成鼓 kick 时长（s）.</summary>
    public const double SynthKickSec = 0.09;

    /// <summary>合成军鼓体音（Hz）.</summary>
    public const double SynthSnareToneHz = 240.0;

    /// <summary>合成军鼓时长（s）.</summary>
    public const double SynthSnareSec = 0.06;

    /// <summary>高频 tick 时长（s）——合成 hats 的金属点.</summary>
    public const double SynthTickSec = 0.012;

    /// <summary>高频 tick 金属泛音（×根音 ≈ 高频段身份）.</summary>
    public const double SynthTickPingRatio = 20.0;

    /// <summary>聚变低音的软削波驱动（三档里最密——聚变堆的能量密度）.</summary>
    public const double SynthBassDrive = 3.2;

    /// <summary>尖塔鸣动机音高（×根音）.</summary>
    public const double ChimePitchRatio = 4.0;

    /// <summary>尖塔鸣动机复现音高比（×原动机音高；纯五度 2^(7/12)≈1.4983）.</summary>
    public const double ChimeRepriseInterval = 1.4983;

    /// <summary>尖塔鸣动复现增益（×长鸣）.</summary>
    public const double ChimeRepriseGain = 0.6;

    // ---- T-142 cosmic timbre (终章 深空音景, 31 号 §1.2 终章行) -------------

    /// <summary>零点脉冲基音 = 根音下八度（×0.5）——每小节一周的极慢搏动.</summary>
    public const double CosmicPulseRatio = 0.5;

    /// <summary>星尘带加性噪声分量数（每个整数周期化 ⇒ 严格 N-周期）.</summary>
    public const int StardustComponents = 12;

    /// <summary>星尘带下沿（×根音）——高于蒸汽带（记忆光尘的高频闪烁）.</summary>
    public const double StardustBandLowRatio = 8.0;

    /// <summary>星尘带上沿（×根音）.</summary>
    public const double StardustBandHighRatio = 24.0;

    /// <summary>记忆钟鸣音高（×根音）.</summary>
    public const double MemoryChimeRatio = 4.0;

    /// <summary>记忆钟鸣时长（s）.</summary>
    public const double MemoryChimeSec = 0.7;

    /// <summary>引力鼓 kick 扫频起点（Hz）——深软.</summary>
    public const double GravityKickFromHz = 90.0;

    /// <summary>引力鼓 kick 扫频终点（Hz）.</summary>
    public const double GravityKickToHz = 38.0;

    /// <summary>引力鼓 kick 时长（s）——比声学/合成更长（深空回响）.</summary>
    public const double GravityKickSec = 0.15;

    /// <summary>幽灵军鼓时长（s）.</summary>
    public const double GhostSnareSec = 0.05;

    /// <summary>幽灵军鼓增益系数（×percussion_gain）.</summary>
    public const double GhostSnareGainFactor = 0.35;

    /// <summary>金属 tick 时长（s）——稀疏点缀.</summary>
    public const double CosmicTickSec = 0.01;

    /// <summary>透镜低音的软削波驱动（深而不凶）.</summary>
    public const double CosmicBassDrive = 2.2;

    /// <summary>星门轰鸣音高（×根音）.</summary>
    public const double GateDronePitchRatio = 2.0;

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
        RenderPad(program, samples, count);

        if (program.Timbre == BgmTimbre.Industrial)
        {
            // ---- E3 工业音景（T-121）：三态仍为累积分层 -------------------
            RenderIndustrialBase(program, samples, count, rate);
            if (program.HasPercussion(state))
            {
                RenderIndustrialPercussion(program, samples, count, rate);
            }

            if (program.HasBass(state))
            {
                RenderBass(program, samples, count, rate, IndustrialBassDrive);
                RenderLongWhistleMotif(program, samples, count, rate);
            }
        }
        else if (program.Timbre == BgmTimbre.Modern)
        {
            // ---- E4 现代音景（T-157，31 号 §1.2 E4 身份卡：电网/电子/都市）：
            // 三态累积分层（汽笛动机换警笛，蒸汽垫换市电嗡鸣）。
            RenderModernBase(program, samples, count, rate);
            if (program.HasPercussion(state))
            {
                RenderModernPercussion(program, samples, count, rate);
            }

            if (program.HasBass(state))
            {
                RenderBass(program, samples, count, rate, ModernBassDrive);
                RenderUrbanSirenMotif(program, samples, count, rate);
            }
        }
        else if (program.Timbre == BgmTimbre.Synth)
        {
            // ---- E5 未来音景（T-142，31 号 §1.2 E5 行）：累积分层 ---------
            RenderSynthBase(program, samples, count, rate);
            if (program.HasPercussion(state))
            {
                RenderSynthPercussion(program, samples, count, rate);
            }

            if (program.HasBass(state))
            {
                RenderBass(program, samples, count, rate, SynthBassDrive);
                RenderSpireChimeMotif(program, samples, count, rate);
            }
        }
        else if (program.Timbre == BgmTimbre.Cosmic)
        {
            // ---- 终章 深空音景（T-142，31 号 §1.2 终章行）：累积分层 -------
            RenderCosmicBase(program, samples, count, rate);
            if (program.HasPercussion(state))
            {
                RenderCosmicPercussion(program, samples, count, rate);
            }

            if (program.HasBass(state))
            {
                RenderBass(program, samples, count, rate, CosmicBassDrive);
                RenderGateDroneMotif(program, samples, count, rate);
            }
        }
        else
        {
            // ---- T-092 acoustic path — byte-identical to the E1/E2 render --
            if (program.HasPercussion(state))
            {
                RenderAcousticPercussion(program, samples, count, rate);
            }

            if (program.HasBass(state))
            {
                RenderBass(program, samples, count, rate, drive: 1.8);
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
    /// Harmonic base shared by every era and state: the doubled detuned sine
    /// pad over the chord (the cross-era identity that makes equal-power
    /// crossfades coherent). T-092 math verbatim.
    /// </summary>
    private static void RenderPad(BgmProgram program, double[] samples, int count)
    {
        int voice = 0;
        foreach (double interval in program.ChordIntervals)
        {
            foreach (double detune in new[] { 1.0 + PadDetune, 1.0 - PadDetune })
            {
                double frequency = program.RootHz * interval * detune;
                int cycles = (int)Math.Round(frequency * count / (double)program.SampleRateHz);
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
    }

    /// <summary>
    /// Percussion event list: kick on beats 1/3 of each bar, snare on 2/4,
    /// hats on the subdivision grid. Each event renders once into its own
    /// buffer (envelope × [swept sine | tone+noise | noise], 3 ms release
    /// ramp) and is added at its grid position — the grid repeats identically
    /// every loop, so loop periodicity holds. T-092 math verbatim.
    /// </summary>
    private static void RenderAcousticPercussion(BgmProgram program, double[] samples, int count, int rate)
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

    /// <summary>
    /// Boss bass — alternating root/fifth per bar through a tanh soft clip
    /// (brassy without hard edges); the industrial family drives the clip
    /// harder for the 重工业 weight. T-092 math verbatim at drive 1.8.
    /// </summary>
    private static void RenderBass(BgmProgram program, double[] samples, int count, int rate, double drive)
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
            samples[n] += program.BassGain * Math.Tanh(drive * Math.Sin(TwoPi * cycles * n / (double)count));
        }
    }

    /// <summary>
    /// Industrial calm base (all states): 蒸汽垫底 = the shared pad plus a
    /// sub-root pulse throb (one per beat, integer cycles ⇒ periodic), a
    /// steam-hiss additive noise band (each component integer-cycle
    /// quantized with a seeded phase — exactly N-periodic "noise"), and the
    /// faint dock whistle (微汽笛) on every odd bar.
    /// </summary>
    private static void RenderIndustrialBase(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int totalBeats = program.LoopBars * program.BeatsPerBar;

        // a) 低频脉冲：根音下八度，逐拍升余弦搏动（机器心跳）
        int pulseCycles = (int)Math.Round(program.RootHz * SubPulseRatio * count / (double)rate);
        int throbCycles = Math.Max(1, totalBeats);
        double pulseGain = program.PadGain * 1.15;
        for (int n = 0; n < count; n++)
        {
            double throb = 0.55 + (0.45 * (0.5 - (0.5 * Math.Cos(TwoPi * throbCycles * n / (double)count))));
            samples[n] += pulseGain * throb * Math.Sin(TwoPi * pulseCycles * n / (double)count);
        }

        // b) 蒸汽嘶鸣垫：加性噪声带（分量相位=种子纯函数，整数周期 ⇒ 无缝）
        double lowHz = program.RootHz * SteamBandLowRatio;
        double highHz = program.RootHz * SteamBandHighRatio;
        double hissGain = program.PadGain * 0.055;
        for (int j = 0; j < SteamComponents; j++)
        {
            double nominal = lowHz * Math.Pow(highHz / lowHz, j / (double)(SteamComponents - 1));
            int cycles = (int)Math.Round(nominal * count / (double)rate);
            if (cycles < 1)
            {
                continue;
            }

            uint seed = SteamSeed(j);
            double phase = NextPhase(ref seed);
            int lfoCycles = 1 + (j % 3);
            for (int n = 0; n < count; n++)
            {
                double breathe = 0.7 + (0.3 * Math.Sin((TwoPi * lfoCycles * n / count) + (phase * 0.13)));
                samples[n] += hissGain * breathe * Math.Sin((TwoPi * cycles * n / (double)count) + phase);
            }
        }

        // c) 微汽笛：奇数栏栏首一声（0 栏循环当 1 栏用——参数行守卫覆盖）
        int barSamples = beatSamples * program.BeatsPerBar;
        int faintSamples = (int)Math.Round(FaintWhistleSec * rate);
        int firstFaintBar = program.LoopBars == 1 ? 0 : 1;
        int lastFaintBar = program.LoopBars == 1 ? 0 : (program.LoopBars % 2 == 0 ? program.LoopBars - 1 : program.LoopBars - 2);
        int faintEnd = (lastFaintBar * barSamples) + faintSamples;
        if (faintEnd >= count)
        {
            throw new InvalidOperationException(
                $"{program.Era}: 微汽笛网格尾端（{faintEnd} 样）越过循环尾（{count} 样）——先修参数行");
        }

        double faintPitch = program.RootHz * 4.0 * program.ChordIntervals[Math.Min(1, program.ChordIntervals.Count - 1)];
        for (int bar = firstFaintBar; bar < program.LoopBars; bar += 2)
        {
            double[] whistle = WhistleEvent(
                faintSamples, rate, faintPitch, program.PadGain * 0.5,
                FaintWhistleAttackSec, FaintWhistleReleaseSec);
            AddEvent(samples, bar * barSamples, whistle);
        }
    }

    /// <summary>
    /// Industrial percussion (combat+): 金属打击 = inharmonic-partials clang
    /// on beats 1/3 (iron plate over the root octave); 铆钉动机 = seeded-noise
    /// rivet strikes on the subdivision grid of beats 2/4 (the rivet gun's
    /// machine-gun bursts). Grid guard first — same policy as the acoustic
    /// path: a cramped row throws before anything renders.
    /// </summary>
    private static void RenderIndustrialPercussion(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int totalBeats = program.LoopBars * program.BeatsPerBar;
        int subdivision = Math.Max(1, beatSamples / program.HatSubdivisions);
        int clangSamples = (int)Math.Round(ClangSec * rate);
        int rivetSamples = (int)Math.Round(RivetSec * rate);

        // 守卫：金属打击/铆钉各自最晚事件+尾音不得越过循环尾（先于渲染，修表不修码）
        // （铆钉按最坏情形=任意拍最末细分位估算——铆钉只落在奇数拍，守卫保守不漏）
        int clangEnd = ((totalBeats - 1) * beatSamples) + clangSamples;                       // 偶拍栏位铁板
        int rivetEnd = ((totalBeats - 1) * beatSamples) + (subdivision * (program.HatSubdivisions - 1)) + rivetSamples;
        if (clangEnd >= count || rivetEnd >= count)
        {
            int worst = Math.Max(clangEnd, rivetEnd);
            throw new InvalidOperationException(
                $"{program.Era}: 节拍网格尾端（{worst} 样）越过循环尾（{count} 样）——先修参数行");
        }

        double clangBase = program.RootHz * ClangPitchRatio;
        double rivetPing = program.RootHz * RivetPingRatio;
        for (int beat = 0; beat < totalBeats; beat++)
        {
            int beatStart = beat * beatSamples;
            int beatInBar = beat % program.BeatsPerBar;
            if (beatInBar % 2 == 0)
            {
                double[] clang = ClangEvent(clangSamples, rate, clangBase, program.PercussionGain);
                AddEvent(samples, beatStart, clang);
            }
            else
            {
                for (int sub = 0; sub < program.HatSubdivisions; sub++)
                {
                    int hit = beatStart + (sub * subdivision);
                    if (hit >= beatStart + beatSamples)
                    {
                        break; // subdivision coarser than the beat — no room for this sub
                    }

                    double[] rivet = RivetEvent(rivetSamples, rate, rivetPing, program.PercussionGain * 0.85, HitSeed(beat, sub + 1));
                    AddEvent(samples, hit, rivet);
                }
            }
        }
    }

    /// <summary>
    /// Boss 汽笛长鸣动机: a one-bar root+fourth dual-pipe whistle at the
    /// loop head (the Bastion's horn), answered mid-loop by a half-bar
    /// reprise a fifth up — call and response, and the energy bookkeeping
    /// that keeps the loop halves balanced (seamless-loop precondition).
    /// </summary>
    private static void RenderLongWhistleMotif(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int barSamples = beatSamples * program.BeatsPerBar;
        double gain = program.BassGain * 0.65;
        double fourth = program.ChordIntervals[Math.Min(1, program.ChordIntervals.Count - 1)];

        // 守卫：长鸣（0 栏整栏）与复现（2 栏前半）不得越过循环尾（先于渲染）
        int longEnd = program.LoopBars >= 3 ? (2 * barSamples) + (barSamples / 2) : barSamples;
        if (longEnd >= count)
        {
            throw new InvalidOperationException(
                $"{program.Era}: 汽笛长鸣尾端（{longEnd} 样）越过循环尾（{count} 样）——先修参数行");
        }

        double[] longWhistle = new double[barSamples];
        int half = barSamples / 2;
        for (int i = 0; i < barSamples; i++)
        {
            double t = i / (double)rate;
            double env = EnvelopeWindow(i, barSamples, (int)Math.Round(WhistleAttackSec * rate), (int)Math.Round(WhistleReleaseSec * rate));
            double root440 = Math.Sin(TwoPi * program.RootHz * 2.0 * (1.0 + WhistleDetune) * t)
                + Math.Sin(TwoPi * program.RootHz * 2.0 * (1.0 - WhistleDetune) * t);
            double fourthPipe = Math.Sin(TwoPi * program.RootHz * 2.0 * fourth * (1.0 + WhistleDetune) * t)
                + Math.Sin(TwoPi * program.RootHz * 2.0 * fourth * (1.0 - WhistleDetune) * t);
            longWhistle[i] = gain * env * ((0.55 * root440) + (0.45 * fourthPipe));
        }

        AddEvent(samples, 0, longWhistle);

        if (program.LoopBars >= 3)
        {
            double reprisePitch = program.RootHz * 2.0 * WhistleRepriseInterval;
            double[] reprise = WhistleEvent(half, rate, reprisePitch, gain * WhistleRepriseGain, WhistleAttackSec, WhistleReleaseSec);
            AddEvent(samples, 2 * barSamples, reprise);
        }
    }

    // ---- T-157 E4 modern timbre（E4 现代音景） -------------------------------

    /// <summary>
    /// E4 modern calm base (all states): 市电嗡鸣 = the shared pad plus a
    /// detuned sine pair over the root octave under a slow two-cycle wobble
    /// (the transformer hum of the modern grid); 城市脉冲 = a sub-root
    /// raised-cosine throb with one cycle per TWO beats (the grid's load
    /// pacing); 无线电点 = faint short high blips on every bar head (the
    /// city's radio beacons). Sustained layers are integer-cycle quantized;
    /// blips are fixed-grid events (same periodicity policy as the other
    /// families).
    /// </summary>
    private static void RenderModernBase(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int totalBeats = program.LoopBars * program.BeatsPerBar;

        // a) 市电嗡鸣：根音上八度双绕组失谐对（整数周期化 ⇒ 无缝），2 周慢摆
        double humBase = program.RootHz * MainsHumRatio;
        double humGain = program.PadGain * 0.6;
        int wobbleCycles = 2;
        foreach (double detune in new[] { 1.0 + MainsHumDetune, 1.0 - MainsHumDetune })
        {
            int cycles = (int)Math.Round(humBase * detune * count / (double)rate);
            if (cycles < 1)
            {
                continue;
            }

            for (int n = 0; n < count; n++)
            {
                double wobble = 0.8 + (0.2 * Math.Sin((TwoPi * wobbleCycles * n / (double)count) + (detune > 1 ? 0 : Math.PI / 2)));
                samples[n] += (humGain / 2) * wobble * Math.Sin(TwoPi * cycles * n / (double)count);
            }
        }

        // b) 城市脉冲：根音下八度，每两拍一周的升余弦搏动（电网负载节律）
        int pulseCycles = (int)Math.Round(program.RootHz * GridPulseRatio * count / (double)rate);
        int throbCycles = Math.Max(1, totalBeats / 2);
        double pulseGain = program.PadGain * 1.05;
        for (int n = 0; n < count; n++)
        {
            double throb = 0.5 + (0.5 * (0.5 - (0.5 * Math.Cos(TwoPi * throbCycles * n / (double)count))));
            samples[n] += pulseGain * throb * Math.Sin(TwoPi * pulseCycles * n / (double)count);
        }

        // c) 无线电点：每小节栏首一粒高频信标闪点（快速衰减正弦）
        int barSamples = beatSamples * program.BeatsPerBar;
        int blipSamples = (int)Math.Round(RadioBlipSec * rate);
        int blipEnd = ((program.LoopBars - 1) * barSamples) + blipSamples;
        if (blipEnd >= count)
        {
            throw new InvalidOperationException(
                $"{program.Era}: 无线电点网格尾端（{blipEnd} 样）越过循环尾（{count} 样）——先修参数行");
        }

        double blipPitch = program.RootHz * RadioBlipRatio;
        for (int bar = 0; bar < program.LoopBars; bar++)
        {
            AddEvent(samples, bar * barSamples,
                RadioBlipEvent(blipSamples, rate, blipPitch, program.PadGain * RadioBlipGainFactor));
        }
    }

    /// <summary>
    /// E4 modern percussion (combat+): 电子鼓 = swept-sine kick (150→50 Hz) on
    /// beats 1/3; 拍手军鼓 = a double-tap seeded-noise clap on beats 2/4 (the
    /// city crowd's pulse); 数字 hat = very short seeded noise on the
    /// subdivision grid. Grid guard first (same policy).
    /// </summary>
    private static void RenderModernPercussion(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int totalBeats = program.LoopBars * program.BeatsPerBar;
        int subdivision = Math.Max(1, beatSamples / program.HatSubdivisions);
        int kickSamples = (int)Math.Round(ModernKickSec * rate);
        int clapSamples = (int)Math.Round(ClapSnareSec * rate);
        int hatSamples = (int)Math.Round(DigitalHatSec * rate);

        int kickEnd = ((totalBeats - 1) * beatSamples) + kickSamples;
        int hatEnd = ((totalBeats - 1) * beatSamples) + (subdivision * (program.HatSubdivisions - 1)) + hatSamples;
        if (kickEnd >= count || hatEnd >= count)
        {
            int worst = Math.Max(kickEnd, hatEnd);
            throw new InvalidOperationException(
                $"{program.Era}: 电子鼓网格尾端（{worst} 样）越过循环尾（{count} 样）——先修参数行");
        }

        for (int beat = 0; beat < totalBeats; beat++)
        {
            int beatStart = beat * beatSamples;
            int beatInBar = beat % program.BeatsPerBar;
            double[] hit = beatInBar % 2 == 0
                ? ModernKickEvent(kickSamples, rate, program.PercussionGain)
                : ClapSnareEvent(clapSamples, rate, program.PercussionGain * 0.9, HitSeed(beat, 0));
            AddEvent(samples, beatStart, hit);

            for (int sub = 0; sub < program.HatSubdivisions; sub++)
            {
                int offset = beatStart + (sub * subdivision);
                if (offset >= beatStart + beatSamples)
                {
                    break;
                }

                AddEvent(samples, offset,
                    DigitalHatEvent(hatSamples, rate, program.PercussionGain * 0.45, HitSeed(beat, sub + 1)));
            }
        }
    }

    /// <summary>
    /// E4 Boss 都市警笛动机: a one-bar FM-swept dual-tone siren (the city's
    /// emergency voice — two detuned carriers pitch-cycled in opposition) at
    /// the loop head, answered mid-loop by a half-bar reprise up a fifth —
    /// call and response（汽笛长鸣动机同构，音色族=都市警笛）. Envelope
    /// windows pin exact zero endpoints (click kill — the whistle policy).
    /// </summary>
    private static void RenderUrbanSirenMotif(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int barSamples = beatSamples * program.BeatsPerBar;
        double gain = program.BassGain * 0.6;

        int longEnd = program.LoopBars >= 3 ? (2 * barSamples) + (barSamples / 2) : barSamples;
        if (longEnd >= count)
        {
            throw new InvalidOperationException(
                $"{program.Era}: 都市警笛尾端（{longEnd} 样）越过循环尾（{count} 样）——先修参数行");
        }

        AddEvent(samples, 0, SirenEvent(barSamples, rate, program.RootHz * MainsHumRatio, gain));

        if (program.LoopBars >= 3)
        {
            AddEvent(samples, 2 * barSamples,
                SirenEvent(barSamples / 2, rate, program.RootHz * MainsHumRatio * WhistleRepriseInterval,
                    gain * SirenRepriseGain));
        }
    }

    /// <summary>One radio beacon blip: a fast-decaying high sine (fixed grid ⇒ loop-periodic).</summary>
    private static double[] RadioBlipEvent(int lengthSamples, int rate, double pitchHz, double gain)
    {
        var buf = new double[lengthSamples];
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            buf[i] = gain * Math.Exp(-t / (RadioBlipSec / 2.5)) * Math.Sin(TwoPi * pitchHz * t)
                * ReleaseRamp(i, lengthSamples, rate);
        }

        return buf;
    }

    /// <summary>电子鼓 kick：150→50 Hz 指数衰减扫频正弦（声学与合成之间的稳态档）.</summary>
    private static double[] ModernKickEvent(int lengthSamples, int rate, double gain)
    {
        var buf = new double[lengthSamples];
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            double sweep = ModernKickFromHz * Math.Pow(ModernKickToHz / ModernKickFromHz, t / ModernKickSec);
            buf[i] = gain * Math.Exp(-t / (ModernKickSec / 2.0)) * Math.Sin(TwoPi * sweep * t)
                * ReleaseRamp(i, lengthSamples, rate);
        }

        return buf;
    }

    /// <summary>拍手军鼓：双击种子噪声体（第二击 12 ms 后×0.7——拍手的复击感）.</summary>
    private static double[] ClapSnareEvent(int lengthSamples, int rate, double gain, uint seed)
    {
        var buf = new double[lengthSamples];
        int secondTap = (int)Math.Round(0.012 * rate);
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            uint firstState = seed;
            uint secondState = seed ^ 0x5F356495u; // 第二击独立种子族（确定性）
            double first = Math.Exp(-t / (ClapSnareSec / 1.5)) * NextNoise(ref firstState);
            double second = 0;
            if (i >= secondTap)
            {
                double t2 = (i - secondTap) / (double)rate;
                second = 0.7 * Math.Exp(-t2 / (ClapSnareSec / 1.5)) * NextNoise(ref secondState);
            }

            buf[i] = gain * 0.8 * (first + second) * ReleaseRamp(i, lengthSamples, rate);
        }

        return buf;
    }

    /// <summary>数字 hat：极短种子噪声（数字味的稀疏高频点）.</summary>
    private static double[] DigitalHatEvent(int lengthSamples, int rate, double gain, uint seed)
    {
        var buf = new double[lengthSamples];
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            buf[i] = gain * Math.Exp(-t / (DigitalHatSec / 2.0)) * NextNoise(ref seed)
                * ReleaseRamp(i, lengthSamples, rate);
        }

        return buf;
    }

    /// <summary>
    /// One urban siren event: two detuned carriers whose pitch swings ±
    /// <see cref="SirenSweepSemitones"/> in opposition (phase-accumulated FM —
    /// the emergency wail) under a raised-cosine window with exact zero
    /// endpoints（click kill——WhistleEvent 同律：窗端归零即无缝，事件网格固定
    /// ⇒ 循环周期成立）.
    /// </summary>
    private static double[] SirenEvent(int lengthSamples, int rate, double baseHz, double gain)
    {
        var buf = new double[lengthSamples];
        int attack = (int)Math.Round(WhistleAttackSec * rate);
        int release = (int)Math.Round(WhistleReleaseSec * rate);
        double sweepHz = 0.8; // one full up-down wail per 1.25 s — the siren's cadence
        double upRatio = Math.Pow(2, SirenSweepSemitones / 12.0);
        double downRatio = Math.Pow(2, -SirenSweepSemitones / 12.0);
        double phaseA = 0;
        double phaseB = 0;
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            double env = EnvelopeWindow(i, lengthSamples, attack, release);
            double swing = Math.Sin(TwoPi * sweepHz * t);
            double freqA = baseHz * (1.0 + MainsHumDetune) * Math.Pow(upRatio, swing);
            double freqB = baseHz * (1.0 - MainsHumDetune) * Math.Pow(downRatio, swing); // 反向摆——双车警笛的对驶
            phaseA += (TwoPi * freqA) / rate;
            phaseB += (TwoPi * freqB) / rate;
            buf[i] = gain * env * (0.5 * Math.Sin(phaseA) + 0.5 * Math.Sin(phaseB));
        }

        return buf;
    }

    // ---- T-142 E5 synth timbre（E5 未来音景） ------------------------------

    /// <summary>
    /// E5 synth calm base (all states): 聚变脉冲 = the shared pad plus a
    /// sub-root per-beat raised-cosine throb (the fusion core's heartbeat);
    /// 光流琶音 = chord-tone sine plucks cycling octaves on the eighth-note
    /// grid (the light-flow conduit texture, deterministic per index); 全息点
    /// = faint high blips on every bar head. Sustained layers are
    /// integer-cycle quantized; plucks/blips are fixed-grid events (same
    /// periodicity policy as the industrial events).
    /// </summary>
    private static void RenderSynthBase(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int totalBeats = program.LoopBars * program.BeatsPerBar;

        // a) 聚变脉冲：根音下八度，逐拍升余弦搏动（聚变堆心跳——比工业脉动柔）
        int pulseCycles = (int)Math.Round(program.RootHz * SynthPulseRatio * count / (double)rate);
        int throbCycles = Math.Max(1, totalBeats);
        double pulseGain = program.PadGain * 1.05;
        for (int n = 0; n < count; n++)
        {
            double throb = 0.5 + (0.5 * (0.5 - (0.5 * Math.Cos(TwoPi * throbCycles * n / (double)count))));
            samples[n] += pulseGain * throb * Math.Sin(TwoPi * pulseCycles * n / (double)count);
        }

        // b) 光流琶音：八分音网格上的和弦音 pluck（八度交替上行流）
        int arpStep = Math.Max(1, beatSamples / ArpSubdivisionsPerBeat);
        int arpTotal = totalBeats * ArpSubdivisionsPerBeat;
        int pluckSamples = (int)Math.Round(ArpPluckSec * rate);
        int arpEnd = ((arpTotal - 1) * arpStep) + pluckSamples;
        if (arpEnd >= count)
        {
            throw new InvalidOperationException(
                $"{program.Era}: 光流琶音网格尾端（{arpEnd} 样）越过循环尾（{count} 样）——先修参数行");
        }

        double arpGain = program.PadGain * ArpGainFactor / ArpSubdivisionsPerBeat;
        for (int i = 0; i < arpTotal; i++)
        {
            double interval = program.ChordIntervals[i % program.ChordIntervals.Count];
            double octave = (i % (ArpSubdivisionsPerBeat * 2)) < ArpSubdivisionsPerBeat ? 2.0 : 4.0;
            double pitch = program.RootHz * interval * octave;
            AddEvent(samples, i * arpStep,
                ArpPluckEvent(pluckSamples, rate, pitch, arpGain));
        }

        // c) 全息点：每小节栏首一粒高频闪点
        int barSamples = beatSamples * program.BeatsPerBar;
        int blipSamples = (int)Math.Round(HoloBlipSec * rate);
        int blipEnd = ((program.LoopBars - 1) * barSamples) + blipSamples;
        if (blipEnd >= count)
        {
            throw new InvalidOperationException(
                $"{program.Era}: 全息点网格尾端（{blipEnd} 样）越过循环尾（{count} 样）——先修参数行");
        }

        double blipPitch = program.RootHz * HoloBlipRatio;
        for (int bar = 0; bar < program.LoopBars; bar++)
        {
            AddEvent(samples, bar * barSamples,
                HoloBlipEvent(blipSamples, rate, blipPitch, program.PadGain * HoloBlipGainFactor));
        }
    }

    /// <summary>
    /// One light-flow pluck: fundamental + second harmonic under a raised
    /// cosine window with exact zero endpoints — a soft synth pluck whose
    /// brightness rides the octave alternation.
    /// </summary>
    private static double[] ArpPluckEvent(int lengthSamples, int rate, double pitchHz, double gain)
    {
        var buf = new double[lengthSamples];
        int attack = (int)Math.Round(ArpAttackSec * rate);
        int release = (int)Math.Round(ArpReleaseSec * rate);
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            double env = EnvelopeWindow(i, lengthSamples, attack, release);
            buf[i] = gain * env * (Math.Sin(TwoPi * pitchHz * t) + (0.25 * Math.Sin(TwoPi * pitchHz * 2.0 * t)));
        }

        return buf;
    }

    /// <summary>One holo blip: a fast-decaying high sine (the lattice dot).</summary>
    private static double[] HoloBlipEvent(int lengthSamples, int rate, double pitchHz, double gain)
    {
        var buf = new double[lengthSamples];
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            buf[i] = gain * Math.Exp(-t / (HoloBlipSec / 3.0)) * Math.Sin(TwoPi * pitchHz * t)
                * ReleaseRamp(i, lengthSamples, rate);
        }

        return buf;
    }

    /// <summary>
    /// E5 synth percussion (combat+): 合成鼓 = swept-sine kick (170→55 Hz) on
    /// beats 1/3; glitch snare (240 Hz tone + seeded noise) on beats 2/4;
    /// 高频 tick = short seeded noise with a metallic ping partial on the
    /// subdivision grid. Grid guard first — same policy as the other
    /// families: a cramped row throws before anything renders.
    /// </summary>
    private static void RenderSynthPercussion(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int totalBeats = program.LoopBars * program.BeatsPerBar;
        int subdivision = Math.Max(1, beatSamples / program.HatSubdivisions);
        int kickSamples = (int)Math.Round(SynthKickSec * rate);
        int snareSamples = (int)Math.Round(SynthSnareSec * rate);
        int tickSamples = (int)Math.Round(SynthTickSec * rate);

        int kickEnd = ((totalBeats - 1) * beatSamples) + kickSamples;
        int tickEnd = ((totalBeats - 1) * beatSamples) + (subdivision * (program.HatSubdivisions - 1)) + tickSamples;
        if (kickEnd >= count || tickEnd >= count)
        {
            int worst = Math.Max(kickEnd, tickEnd);
            throw new InvalidOperationException(
                $"{program.Era}: 合成鼓网格尾端（{worst} 样）越过循环尾（{count} 样）——先修参数行");
        }

        double pingHz = program.RootHz * SynthTickPingRatio;
        for (int beat = 0; beat < totalBeats; beat++)
        {
            int beatStart = beat * beatSamples;
            int beatInBar = beat % program.BeatsPerBar;
            double[] hit = beatInBar % 2 == 0
                ? SynthKickEvent(kickSamples, rate, program.PercussionGain)
                : SynthSnareEvent(snareSamples, rate, program.PercussionGain, HitSeed(beat, 0));
            AddEvent(samples, beatStart, hit);

            for (int sub = 0; sub < program.HatSubdivisions; sub++)
            {
                int offset = beatStart + (sub * subdivision);
                if (offset >= beatStart + beatSamples)
                {
                    break;
                }

                AddEvent(samples, offset,
                    SynthTickEvent(tickSamples, rate, pingHz, program.PercussionGain * 0.5, HitSeed(beat, sub + 1)));
            }
        }
    }

    /// <summary>合成鼓 kick：170→55 Hz 指数衰减扫频正弦（比声学 kick 亮起）.</summary>
    private static double[] SynthKickEvent(int lengthSamples, int rate, double gain)
    {
        var buf = new double[lengthSamples];
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            double sweep = SynthKickFromHz * Math.Pow(SynthKickToHz / SynthKickFromHz, t / SynthKickSec);
            buf[i] = gain * Math.Exp(-t / (SynthKickSec / 2.0)) * Math.Sin(TwoPi * sweep * t)
                * ReleaseRamp(i, lengthSamples, rate);
        }

        return buf;
    }

    /// <summary>Glitch snare：240 Hz 体音 + 种子噪声（数字味军鼓）.</summary>
    private static double[] SynthSnareEvent(int lengthSamples, int rate, double gain, uint seed)
    {
        var buf = new double[lengthSamples];
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            buf[i] = gain * 0.85 * Math.Exp(-t / (SynthSnareSec / 2.0)) *
                ((0.5 * Math.Sin(TwoPi * SynthSnareToneHz * t)) + (0.5 * NextNoise(ref seed))) *
                ReleaseRamp(i, lengthSamples, rate);
        }

        return buf;
    }

    /// <summary>高频 tick：短种子噪声体 + 金属 ping（光流线路的高频身份）.</summary>
    private static double[] SynthTickEvent(int lengthSamples, int rate, double pingHz, double gain, uint seed)
    {
        var buf = new double[lengthSamples];
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            double body = Math.Exp(-t / (SynthTickSec / 2.0)) * NextNoise(ref seed);
            double ping = 0.6 * Math.Exp(-t / (SynthTickSec * 2.0)) * Math.Sin(TwoPi * pingHz * t);
            buf[i] = gain * 0.75 * (body + ping) * ReleaseRamp(i, lengthSamples, rate);
        }

        return buf;
    }

    /// <summary>
    /// E5 Boss 尖塔鸣鸣动机: a one-bar harmonic-partial chime (the energy
    /// spire's voice — 1.0/2.0/3.0 harmonic stack vs the industrial plate's
    /// inharmonic family) at the loop head, answered mid-loop by a half-bar
    /// reprise a fifth up — call and response（长鸣动机同构，音色族不同）.
    /// </summary>
    private static void RenderSpireChimeMotif(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int barSamples = beatSamples * program.BeatsPerBar;
        double gain = program.BassGain * 0.65;

        int longEnd = program.LoopBars >= 3 ? (2 * barSamples) + (barSamples / 2) : barSamples;
        if (longEnd >= count)
        {
            throw new InvalidOperationException(
                $"{program.Era}: 尖塔鸣动尾端（{longEnd} 样）越过循环尾（{count} 样）——先修参数行");
        }

        AddEvent(samples, 0, ChimeEvent(barSamples, rate, program.RootHz * ChimePitchRatio, gain));

        if (program.LoopBars >= 3)
        {
            AddEvent(samples, 2 * barSamples,
                ChimeEvent(barSamples / 2, rate, program.RootHz * ChimePitchRatio * ChimeRepriseInterval,
                    gain * ChimeRepriseGain));
        }
    }

    /// <summary>
    /// One harmonic chime: 1.0/2.0/3.0 partials under a raised-cosine
    /// attack/release window — the synth family's sustained event（click kill
    /// 同律：窗口端点精确归零）.
    /// </summary>
    private static double[] ChimeEvent(int lengthSamples, int rate, double baseHz, double gain)
    {
        double[] ratios = { 1.0, 2.0, 3.0 };
        double[] weights = { 1.0, 0.45, 0.22 };
        var buf = new double[lengthSamples];
        int attack = (int)Math.Round(0.08 * rate);
        int release = (int)Math.Round(0.2 * rate);
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            double env = EnvelopeWindow(i, lengthSamples, attack, release);
            double partials = 0.0;
            for (int p = 0; p < ratios.Length; p++)
            {
                partials += weights[p] * Math.Sin(TwoPi * baseHz * ratios[p] * t);
            }

            buf[i] = gain * env * (partials / 1.67);
        }

        return buf;
    }

    // ---- T-142 终章 cosmic timbre（深空音景） --------------------------------

    /// <summary>
    /// Final-chapter cosmic calm base (all states): 零点脉冲 = sub-root sine
    /// under an extremely slow one-cycle-per-BAR throb (zero-point drift);
    /// 星尘带 = a sparse additive seeded noise band high above the root
    /// （记忆光尘——比蒸汽带更高更稀）; 记忆钟鸣 = faint harmonic chimes on
    /// every odd bar head（回望六时代的钟声）.
    /// </summary>
    private static void RenderCosmicBase(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int barSamples = beatSamples * program.BeatsPerBar;

        // a) 零点脉冲：每小节一周的极慢搏动（深空的呼吸）
        int pulseCycles = (int)Math.Round(program.RootHz * CosmicPulseRatio * count / (double)rate);
        int throbCycles = Math.Max(1, program.LoopBars);
        double pulseGain = program.PadGain * 1.1;
        for (int n = 0; n < count; n++)
        {
            double throb = 0.55 + (0.45 * (0.5 - (0.5 * Math.Cos(TwoPi * throbCycles * n / (double)count))));
            samples[n] += pulseGain * throb * Math.Sin(TwoPi * pulseCycles * n / (double)count);
        }

        // b) 星尘带：加性噪声带（分量相位=种子纯函数，整数周期 ⇒ 无缝）
        double lowHz = program.RootHz * StardustBandLowRatio;
        double highHz = program.RootHz * StardustBandHighRatio;
        double hissGain = program.PadGain * 0.05;
        for (int j = 0; j < StardustComponents; j++)
        {
            double nominal = lowHz * Math.Pow(highHz / lowHz, j / (double)(StardustComponents - 1));
            int cycles = (int)Math.Round(nominal * count / (double)rate);
            if (cycles < 1)
            {
                continue;
            }

            uint seed = SteamSeed(j + 64); // 星尘带与蒸汽带种子族分离（确定性不耦合）
            double phase = NextPhase(ref seed);
            int lfoCycles = 1 + (j % 2);
            for (int n = 0; n < count; n++)
            {
                double breathe = 0.65 + (0.35 * Math.Sin((TwoPi * lfoCycles * n / count) + (phase * 0.11)));
                samples[n] += hissGain * breathe * Math.Sin((TwoPi * cycles * n / (double)count) + phase);
            }
        }

        // c) 记忆钟鸣：奇数栏栏首一声（0 栏循环当 1 栏用——参数行守卫覆盖）
        int chimeSamples = (int)Math.Round(MemoryChimeSec * rate);
        int firstChimeBar = program.LoopBars == 1 ? 0 : 1;
        int lastChimeBar = program.LoopBars == 1 ? 0 : (program.LoopBars % 2 == 0 ? program.LoopBars - 1 : program.LoopBars - 2);
        int chimeEnd = (lastChimeBar * barSamples) + chimeSamples;
        if (chimeEnd >= count)
        {
            throw new InvalidOperationException(
                $"{program.Era}: 记忆钟鸣网格尾端（{chimeEnd} 样）越过循环尾（{count} 样）——先修参数行");
        }

        double chimePitch = program.RootHz * MemoryChimeRatio;
        for (int bar = firstChimeBar; bar < program.LoopBars; bar += 2)
        {
            AddEvent(samples, bar * barSamples,
                ChimeEvent(chimeSamples, rate, chimePitch, program.PadGain * 0.4));
        }
    }

    /// <summary>
    /// Cosmic percussion (combat+): 引力鼓 = a deep long-swept kick (90→38 Hz
    /// over 0.15 s) on beats 1/3; 幽灵军鼓 = a low-gain seeded-noise snare on
    /// beats 2/4; 金属 tick = very short sparse pings on the subdivision
    /// grid. Grid guard first (same policy).
    /// </summary>
    private static void RenderCosmicPercussion(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int totalBeats = program.LoopBars * program.BeatsPerBar;
        int subdivision = Math.Max(1, beatSamples / program.HatSubdivisions);
        int kickSamples = (int)Math.Round(GravityKickSec * rate);
        int snareSamples = (int)Math.Round(GhostSnareSec * rate);
        int tickSamples = (int)Math.Round(CosmicTickSec * rate);

        int kickEnd = ((totalBeats - 1) * beatSamples) + kickSamples;
        int tickEnd = ((totalBeats - 1) * beatSamples) + (subdivision * (program.HatSubdivisions - 1)) + tickSamples;
        if (kickEnd >= count || tickEnd >= count)
        {
            int worst = Math.Max(kickEnd, tickEnd);
            throw new InvalidOperationException(
                $"{program.Era}: 引力鼓网格尾端（{worst} 样）越过循环尾（{count} 样）——先修参数行");
        }

        double pingHz = program.RootHz * SynthTickPingRatio;
        for (int beat = 0; beat < totalBeats; beat++)
        {
            int beatStart = beat * beatSamples;
            int beatInBar = beat % program.BeatsPerBar;
            double[] hit = beatInBar % 2 == 0
                ? GravityKickEvent(kickSamples, rate, program.PercussionGain)
                : SynthSnareEvent(snareSamples, rate, program.PercussionGain * GhostSnareGainFactor, HitSeed(beat, 0));
            AddEvent(samples, beatStart, hit);

            for (int sub = 0; sub < program.HatSubdivisions; sub++)
            {
                int offset = beatStart + (sub * subdivision);
                if (offset >= beatStart + beatSamples)
                {
                    break;
                }

                AddEvent(samples, offset,
                    SynthTickEvent(tickSamples, rate, pingHz, program.PercussionGain * 0.3, HitSeed(beat, sub + 1)));
            }
        }
    }

    /// <summary>引力鼓 kick：90→38 Hz 长扫频深 kick（深空回响）.</summary>
    private static double[] GravityKickEvent(int lengthSamples, int rate, double gain)
    {
        var buf = new double[lengthSamples];
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            double sweep = GravityKickFromHz * Math.Pow(GravityKickToHz / GravityKickFromHz, t / GravityKickSec);
            buf[i] = gain * Math.Exp(-t / (GravityKickSec / 1.5)) * Math.Sin(TwoPi * sweep * t)
                * ReleaseRamp(i, lengthSamples, rate);
        }

        return buf;
    }

    /// <summary>
    /// Final-chapter Boss 星门轰鸣动机: a one-bar low fifth-stack drone (the
    /// gate opening — detuned sine pair over the root octave plus a sub
    /// octave for weight) at the loop head, answered mid-loop by a half-bar
    /// reprise a fifth up（汽笛长鸣/尖塔鸣动同构，音色族=深空低鸣）.
    /// </summary>
    private static void RenderGateDroneMotif(BgmProgram program, double[] samples, int count, int rate)
    {
        int beatSamples = Math.Max(1, (int)Math.Round(program.BeatSec * rate));
        int barSamples = beatSamples * program.BeatsPerBar;
        double gain = program.BassGain * 0.7;

        int longEnd = program.LoopBars >= 3 ? (2 * barSamples) + (barSamples / 2) : barSamples;
        if (longEnd >= count)
        {
            throw new InvalidOperationException(
                $"{program.Era}: 星门轰鸣尾端（{longEnd} 样）越过循环尾（{count} 样）——先修参数行");
        }

        AddEvent(samples, 0, GateDroneEvent(barSamples, rate, program.RootHz * GateDronePitchRatio, gain));

        if (program.LoopBars >= 3)
        {
            AddEvent(samples, 2 * barSamples,
                GateDroneEvent(barSamples / 2, rate, program.RootHz * GateDronePitchRatio * WhistleRepriseInterval,
                    gain * ChimeRepriseGain));
        }
    }

    /// <summary>
    /// One gate-drone event: detuned sine pair (beating) + sub-octave weight
    /// under a raised-cosine window with exact zero endpoints.
    /// </summary>
    private static double[] GateDroneEvent(int lengthSamples, int rate, double pitchHz, double gain)
    {
        var buf = new double[lengthSamples];
        int attack = (int)Math.Round(0.2 * rate);
        int release = (int)Math.Round(0.35 * rate);
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            double env = EnvelopeWindow(i, lengthSamples, attack, release);
            double pipes = 0.5 * (Math.Sin(TwoPi * pitchHz * (1.0 + WhistleDetune) * t)
                + Math.Sin(TwoPi * pitchHz * (1.0 - WhistleDetune) * t));
            double sub = 0.4 * Math.Sin(TwoPi * pitchHz * 0.5 * t);
            buf[i] = gain * env * (pipes + sub);
        }

        return buf;
    }

    /// <summary>
    /// One dual-pipe whistle event: detuned sine pair (beating chorus) plus
    /// a faint second harmonic for the metal-pipe edge, under a raised-cosine
    /// attack/release window with exact zero endpoints (click kill).
    /// </summary>
    private static double[] WhistleEvent(int lengthSamples, int rate, double pitchHz, double gain, double attackSec, double releaseSec)
    {
        var buf = new double[lengthSamples];
        int attack = (int)Math.Round(attackSec * rate);
        int release = (int)Math.Round(releaseSec * rate);
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            double env = EnvelopeWindow(i, lengthSamples, attack, release);
            double pipes = 0.5 * (Math.Sin(TwoPi * pitchHz * (1.0 + WhistleDetune) * t)
                + Math.Sin(TwoPi * pitchHz * (1.0 - WhistleDetune) * t));
            double edge = 0.18 * Math.Sin(TwoPi * pitchHz * 2.0 * t);
            buf[i] = gain * env * (pipes + edge);
        }

        return buf;
    }

    /// <summary>
    /// Iron-plate clang: three inharmonic partials (the bell/plate family —
    /// 1.0 / 2.756 / 5.404) under an exponential decay with the 3 ms
    /// release ramp. Event buffers live on the fixed grid ⇒ loop-periodic.
    /// </summary>
    private static double[] ClangEvent(int lengthSamples, int rate, double baseHz, double gain)
    {
        double[] ratios = { 1.0, 2.756, 5.404 };
        double[] weights = { 1.0, 0.6, 0.35 };
        var buf = new double[lengthSamples];
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            double partials = 0.0;
            for (int p = 0; p < ratios.Length; p++)
            {
                partials += weights[p] * Math.Sin(TwoPi * baseHz * ratios[p] * t);
            }

            buf[i] = gain * Math.Exp(-t / ClangDecaySec) * (partials / 1.95) * ReleaseRamp(i, lengthSamples, rate);
        }

        return buf;
    }

    /// <summary>
    /// Rivet-gun strike: a fast-decaying seeded-noise body (the "tak") plus
    /// a short metallic ping around 2.4 kHz (the "叮") — the combat layer's
    /// high-frequency identity above the steam band.
    /// </summary>
    private static double[] RivetEvent(int lengthSamples, int rate, double pingHz, double gain, uint seed)
    {
        var buf = new double[lengthSamples];
        for (int i = 0; i < lengthSamples; i++)
        {
            double t = i / (double)rate;
            double body = Math.Exp(-t / RivetDecaySec) * NextNoise(ref seed);
            double ping = 0.5 * Math.Exp(-t / (RivetDecaySec * 3.0)) * Math.Sin(TwoPi * pingHz * t);
            buf[i] = gain * 0.8 * (body + ping) * ReleaseRamp(i, lengthSamples, rate);
        }

        return buf;
    }

    /// <summary>Adds one event buffer at its grid offset (clipped at the loop end by the guard policy).</summary>
    private static void AddEvent(double[] samples, int start, double[] contribution)
    {
        for (int i = 0; i < contribution.Length && start + i < samples.Length; i++)
        {
            samples[start + i] += contribution[i];
        }
    }

    /// <summary>
    /// Raised-cosine attack/release window with exact zero endpoints — the
    /// click kill for sustained events (whistles).
    /// </summary>
    private static double EnvelopeWindow(int index, int length, int attackSamples, int releaseSamples)
    {
        double attackPhase = attackSamples <= 0 ? 1.0 : Math.Min(1.0, index / (double)attackSamples);
        double fromEnd = length - index;
        double releasePhase = releaseSamples <= 0 ? 1.0 : Math.Min(1.0, fromEnd / (double)releaseSamples);
        double attack = (1.0 - Math.Cos(Math.PI * attackPhase)) / 2.0;
        double release = (1.0 - Math.Cos(Math.PI * releasePhase)) / 2.0;
        return attack * release;
    }

    /// <summary>
    /// Percussion event list: kick on beats 1/3 of each bar, snare on 2/4,
    /// hats on the subdivision grid. Each event renders once into its own
    /// buffer (envelope × [swept sine | tone+noise | noise], 3 ms release
    /// ramp) and is added at its grid position — the grid repeats identically
    /// every loop, so loop periodicity holds. T-092 math verbatim.
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

    /// <summary>Per-hit deterministic noise seed (beat × voice — no cross-hit coupling).</summary>
    private static uint HitSeed(int beat, int voice) =>
        0x9E3779B9u ^ ((uint)beat * 0x85EBCA6Bu) ^ ((uint)voice * 0xC2B2AE35u);

    /// <summary>Per-component deterministic steam seed (component index — no cross-component coupling).</summary>
    private static uint SteamSeed(int component) =>
        0x85EBCA6Bu ^ ((uint)component * 0x9E3779B9u) ^ 0x27D4EB2Fu;

    /// <summary>One LCG step mapped to [0, 2π) — the seeded phase of one steam component.</summary>
    private static double NextPhase(ref uint state)
    {
        state = (state * 1664525u) + 1013904223u;
        return (state / (double)uint.MaxValue) * TwoPi;
    }

    /// <summary>One 32-bit LCG step mapped to [-1, 1] — same seed ⇒ same sequence, every render.</summary>
    private static double NextNoise(ref uint state)
    {
        state = (state * 1664525u) + 1013904223u;
        return (state / (double)uint.MaxValue) * 2.0 - 1.0;
    }
}
