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
/// Timbre families (T-121, table-driven): <c>Acoustic</c> keeps the T-092
/// voice set verbatim (byte-identical E1/E2). <c>Industrial</c> renders the
/// E3 soundscape of the 31 号 visual identity card — calm = 蒸汽垫底
/// (steam-hiss additive noise band, exactly periodic) + 低频脉冲 (sub-root
/// pulse throb, one per beat) + 微汽笛 (faint short whistle on the odd
/// bars); combat = +金属打击 (inharmonic-partials clang on beats 1/3) +
/// 铆钉动机 (seeded-noise rivet strikes on the subdivision grid of beats
/// 2/4); boss = +重工业低音 (harder-driven alternating root/fifth bass) +
/// 汽笛长鸣动机 (one-bar long whistle at the loop head with a half-bar
/// fifth reprise mid-loop — call and response, the Bastion's horn).
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
