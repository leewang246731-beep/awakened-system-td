using EraCore.Audio;
using EraCore.Narrative;
using Godot;

namespace EraTD.Audio;

// T-092 (C-25 音频全量 E1/E2) 场景侧音频导演：EraCore/Audio 纯逻辑的场景接线。
// 职责边界（CS-1.6：表现层零业务规则）——本节点只做四件事：
//   1) 内容装载：game/content/audio/ 三参数表（bgm-programs/persona-timbres/
//      narration）；
//   2) BGM 三态：六循环代码合成（2 时代 × 3 态，确定性；WAV 落
//      res://audio/generated/ 供可听测）+ CrossFadeBus 等功率淡入淡出；现场
//      态驱动=只读快照（场上有敌→交战；Boss 波经 SetBossWave 由波计划接线）；
//   3) 人格语音/一次性旁白：四人格音色 token（台词 key 驱动，人格只改音色）；
//      一次性旁白（开场白/胜利交底）= CC0 短乐（res://audio/cc0/，台账 26 号）
//      + 合成音调，一次性标记经 IFileStore 入 settings/narration.json；关闭
//      系统时军师语音降级中性旁白音色（VoiceDowngrade，OffModeDowngrade 联动）；
//   4) 冒烟：headless 合成序列走三态迁移/四音色/两旁白/三通道音量+叮三级
//      回归，打印 AUDIO_OK——smoke 音频段判据（失败 Quit(1)）。
// 并行纪律：Main.cs 只追加一行 Instantiate（冲突由后提交者 rebase）。

/// <summary>
/// Scene-side audio director: renders and drives the full C-25 slice —
/// three-state BGM loops with equal-power crossfades, four persona voice
/// timbres with the neutral-narrator off-mode downgrade, one-time narrations
/// accented by CC0 stingers, and the three-channel mixer (BGM / 音效 / 军师
/// 语音) that also syncs the T-060 ding bus.
/// </summary>
public sealed partial class AudioDirector : Node
{
    private const string GeneratedDir = "res://audio/generated";
    private const string Cc0Dir = "res://audio/cc0";

    /// <summary>Crossfade window (s) — the §14 三态淡入淡出 beat.</summary>
    private const double FadeSeconds = 3.0;

    /// <summary>Acceptance-probe line key (an E1 victory handover line from T-089).</summary>
    private const string ProbeLineKey = "e1.end.handover";

    private SimHost _host = null!;
    private DingAudioPlayer _ding = null!;

    private AudioMixer _mixer = null!;
    private CrossFadeBus _fades = null!;
    private BgmProgramTable _programs = null!;
    private PersonaTimbreTable _timbres = null!;
    private NarrationTable _narrations = null!;

    private readonly Dictionary<string, AudioStreamPlayer> _bgmPlayers = new(); // key "{era}:{state}"
    private readonly Dictionary<string, AudioStreamWav> _bgmStreams = new();
    private AudioStreamPlayer _voicePlayer = null!;
    private readonly Dictionary<string, AudioStreamWav> _voiceCache = new(); // key "{timbre}:{lineKey}"
    private readonly Dictionary<string, AudioStreamOggVorbis> _stingers = new();
    private AudioStreamPlayer _stingerPlayer = null!;

    private string _activeEra = "era1";
    private bool _bossWave;
    private long _smokeTick;
    private int _statePlays;

    /// <summary>Mount helper (Main.cs single-line wiring, same as the other HUDs).</summary>
    internal static AudioDirector Instantiate(Main main, SimHost host)
    {
        var director = new AudioDirector { _host = host };
        main.AddChild(director); // _Ready builds the graph (and smokes headless)
        return director;
    }

    /// <summary>BGM era routing seam — the sandbox runs E1 levels; E2 routing lands with the E2 level work (Era2TerrainAtlas note applies).</summary>
    public void SetActiveEra(string era)
    {
        if (_programs.Find(era) is null)
        {
            throw new ArgumentException($"未登记 BGM 时代 {era}", nameof(era));
        }

        _activeEra = era;
    }

    /// <summary>Boss-wave flag from the wave plan seam (WavePlan.HasBoss binding lands with the campaign host; smoke drives it directly).</summary>
    public void SetBossWave(bool active) => _bossWave = active;

    public override void _Ready()
    {
        try
        {
            LoadContent();
        }
        catch (Exception ex)
        {
            GD.Print($"AUDIO_FAIL load={ex.Message}");
            GD.PushError($"audio content load failed: {ex.Message}");
            GetTree().Quit(1);
            return;
        }

        // ---- mixer + ding regression wiring --------------------------------
        _mixer = new AudioMixer();
        _ding = GetParent().GetNode<DingAudioPlayer>("DingAudio");

        // ---- BGM: render six loops, persist WAVs, start on Calm -----------
        int filesWritten = 0;
        foreach (BgmProgram program in _programs.Programs)
        {
            foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
            {
                byte[] pcm = BgmSynth.SynthesizePcm(program, state);
                filesWritten += PersistWav(
                    $"bgm-{program.Era}-{state.ToString().ToLowerInvariant()}.wav",
                    BgmSynth.SynthesizeWav(program, state));

                var stream = new AudioStreamWav
                {
                    Format = AudioStreamWav.FormatEnum.Format16Bits,
                    MixRate = program.SampleRateHz,
                    Stereo = false,
                    Data = pcm,
                    LoopMode = AudioStreamWav.LoopModeEnum.Forward,
                    LoopEnd = pcm.Length / 2, // sample count — the seamless integer-cycle loop
                };

                var player = new AudioStreamPlayer
                {
                    Name = $"Bgm{program.Era}{state}",
                    Stream = stream,
                };
                AddChild(player);
                _bgmPlayers[$"{program.Era}:{state}"] = player;
                _bgmStreams[$"{program.Era}:{state}"] = stream;
            }
        }

        _fades = new CrossFadeBus(BgmLayerState.Calm, FadeTicks());
        StartBgm(BgmLayerState.Calm);

        // ---- voice + narration players --------------------------------------
        _voicePlayer = new AudioStreamPlayer { Name = "PersonaVoice" };
        _stingerPlayer = new AudioStreamPlayer { Name = "NarrationStinger" };
        AddChild(_voicePlayer);
        AddChild(_stingerPlayer);

        foreach (NarrationCue cue in _narrations.Rows)
        {
            if (_stingers.ContainsKey(cue.StingerFile))
            {
                continue;
            }

            // T-096 (F-141 export fix): exported packs serve imported OGGs via
            // the .import remap, not the raw res:// source path — raw
            // FileAccess.GetFileAsBytes returns empty there. Load through
            // ResourceLoader (remap-aware in exports, source-aware in dev);
            // keep the raw-bytes path as dev fallback for un-imported trees.
            var stream = ResourceLoader.Load<AudioStreamOggVorbis>($"{Cc0Dir}/{cue.StingerFile}");
            if (stream is null)
            {
                byte[] bytes = Godot.FileAccess.GetFileAsBytes($"{Cc0Dir}/{cue.StingerFile}");
                if (bytes.Length == 0)
                {
                    GD.PushError($"CC0 stinger missing: {cue.StingerFile}（台账 docs/engineering/26-t092-audio.md）");
                    continue;
                }

                stream = AudioStreamOggVorbis.LoadFromBuffer(bytes);
            }

            _stingers[cue.StingerFile] = stream;
        }

        // ---- headless acceptance --------------------------------------------
        RunAcceptance(filesWritten);
    }

    /// <summary>Live path: one frame poll — BGM state follows the read-only snapshot beats.</summary>
    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_fades is null)
        {
            return;
        }

        _smokeTick++;
        int enemies = _host.SnapshotSource.Latest?.Units.Count ?? 0;
        BgmLayerState target = _bossWave ? BgmLayerState.Boss : enemies > 0 ? BgmLayerState.Combat : BgmLayerState.Calm;
        _ = _fades.Request(target, _smokeTick);
        ApplyBgmGains();
    }

    // ---- public seams (future bindings own their producers) ----------------

    /// <summary>
    /// Plays the advisor-voice token of one dialogue line under the active
    /// persona — the bubble/subtitle binding surface (T-093/T-126 land the
    /// UI hook; capability/wording stay owned by the narrative domain).
    /// </summary>
    public bool PlayLineVoice(DialogueLine line, SystemPersona persona, bool systemEnabled)
    {
        ArgumentNullException.ThrowIfNull(line);
        string timbreId = VoiceDowngrade.Resolve(persona, systemEnabled);
        PersonaTimbre timbre = _timbres.Find(timbreId)
            ?? throw new ArgumentException($"未登记音色 {timbreId}");
        return PlayVoiceToken(timbre, line.Key);
    }

    /// <summary>
    /// One-time narration of one anchor line: first sighting plays the CC0
    /// stinger plus the neutral narrator tone and marks the flag (persisted
    /// on <see cref="SaveNarration"/>); later sightings stay silent.
    /// </summary>
    public bool PlayNarration(NarrationBook book, string anchorId)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (!book.MarkPlayed(anchorId))
        {
            return false; // 一次性：已播过——静默
        }

        NarrationCue cue = _narrations.Find(anchorId)
            ?? throw new ArgumentException($"未登记旁白锚 {anchorId}", nameof(anchorId));
        if (_stingers.TryGetValue(cue.StingerFile, out AudioStreamOggVorbis? stinger))
        {
            _stingerPlayer.Stream = stinger;
            _stingerPlayer.VolumeDb = LinearToDb(_mixer.EffectiveGain(AudioChannel.Voice));
            _stingerPlayer.Play();
        }

        return PlayVoiceToken(_timbres.Narrator, anchorId);
    }

    /// <summary>Persists the one-time narration flags through the D-03 store.</summary>
    public void SaveNarration(NarrationBook book) => book.Save(_host.Store);

    /// <summary>Mixer of the three §14 channels (settings-panel binding surface).</summary>
    public AudioMixer Mixer => _mixer;

    /// <summary>Crossfade bus under direction (smoke probe).</summary>
    internal CrossFadeBus Fades => _fades;

    private bool PlayVoiceToken(PersonaTimbre timbre, string lineKey)
    {
        string cacheKey = $"{timbre.Id}:{lineKey}";
        if (!_voiceCache.TryGetValue(cacheKey, out AudioStreamWav? stream))
        {
            byte[] pcm = PersonaVoiceSynth.SynthesizePcm(
                timbre, lineKey, _timbres.SampleRateHz, _timbres.PeakAmplitude);
            _ = PersistWav(
                $"voice-{timbre.Id}-{PersonaVoiceSynth.SyllableCount(lineKey)}.wav",
                PersonaVoiceSynth.SynthesizeWav(timbre, lineKey, _timbres.SampleRateHz, _timbres.PeakAmplitude));
            stream = new AudioStreamWav
            {
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = _timbres.SampleRateHz,
                Stereo = false,
                Data = pcm,
            };
            _voiceCache[cacheKey] = stream;
        }

        _voicePlayer.Stream = stream;
        _voicePlayer.VolumeDb = LinearToDb(_mixer.EffectiveGain(AudioChannel.Voice));
        _voicePlayer.Play();
        return true;
    }

    private void StartBgm(BgmLayerState state)
    {
        AudioStreamPlayer player = BgmPlayer(state);
        player.VolumeDb = LinearToDb(_mixer.EffectiveGain(AudioChannel.Bgm));
        player.Play();
        _statePlays++;
    }

    private void ApplyBgmGains()
    {
        double master = _mixer.EffectiveGain(AudioChannel.Bgm);
        foreach (BgmLayerState state in Enum.GetValues<BgmLayerState>())
        {
            AudioStreamPlayer player = BgmPlayer(state);
            double gain = master * _fades.GainOf(state, _smokeTick);
            player.VolumeDb = gain <= 0.0001 ? -80.0f : LinearToDb(gain);
            if (gain > 0.0001 && !player.Playing)
            {
                player.Play();
                _statePlays++;
            }
        }
    }

    private AudioStreamPlayer BgmPlayer(BgmLayerState state) =>
        _bgmPlayers[$"{_activeEra}:{state}"];

    private int FadeTicks() =>
        (int)Math.Round(FadeSeconds * EraCore.Sim.SimClock.TicksPerSecond);

    private static float LinearToDb(double linear) =>
        linear <= 0.0 ? -80.0f : Mathf.LinearToDb((float)linear);

    // ---- headless acceptance (smoke 音频段判据) ------------------------------

    /// <summary>
    /// Full acceptance chain: three-state transitions with equal-power
    /// checkpoints, four persona timbres, two one-time narrations (plus the
    /// replay-suppression and persistence round-trip), off-mode downgrade,
    /// three-channel mixer readback, and the T-060 ding regression against
    /// the new bus. Prints <c>AUDIO_OK</c> or fails the run.
    /// </summary>
    private void RunAcceptance(int filesWritten)
    {
        long t = 0;
        int fadeTicks = FadeTicks();

        // 1) three-state chain Calm → Combat → Boss → Calm with midpoint math
        int transitions = 0;
        transitions += Convert.ToInt32(_fades.Request(BgmLayerState.Combat, t));
        long mid = t + (fadeTicks / 2);
        CrossFadeGains midGains = _fades.GainsAt(mid);
        bool equalPower = Math.Abs((midGains.FromGain * midGains.FromGain) + (midGains.ToGain * midGains.ToGain) - 1.0) < 1e-9;
        bool midIsSqrtHalf = Math.Abs(midGains.ToGain - Math.Sqrt(0.5)) < 1e-3;
        t += fadeTicks;
        bool combatDone = !_fades.IsFading(t) && _fades.Current == BgmLayerState.Combat;

        transitions += Convert.ToInt32(_fades.Request(BgmLayerState.Boss, t));
        t += fadeTicks;
        bool bossDone = !_fades.IsFading(t) && _fades.Current == BgmLayerState.Boss;

        transitions += Convert.ToInt32(_fades.Request(BgmLayerState.Calm, t));
        t += fadeTicks;
        bool calmDone = !_fades.IsFading(t) && _fades.Current == BgmLayerState.Calm;
        bool sameTargetNoOp = !_fades.Request(BgmLayerState.Calm, t);
        ApplyBgmGains(); // apply gains once so players reflect the final state

        // 2) four persona timbres — distinct bytes per persona on one line key
        var personaBytes = new Dictionary<string, byte[]>();
        foreach (SystemPersona persona in Enum.GetValues<SystemPersona>())
        {
            PersonaTimbre timbre = _timbres.Of(persona);
            personaBytes[timbre.Id] = PersonaVoiceSynth.SynthesizePcm(
                timbre, ProbeLineKey, _timbres.SampleRateHz, _timbres.PeakAmplitude);
        }

        bool personasDistinct = personaBytes.Values
            .Select(pcm => Convert.ToBase64String(pcm))
            .Distinct()
            .Count() == personaBytes.Count;
        bool personaAudible = personaBytes.Values.All(p => p.Any(v => v != 0));

        // off-mode downgrade: persona voice swaps to the neutral narrator row
        bool downgrade = VoiceDowngrade.Resolve(SystemPersona.Snarky, systemEnabled: false) == PersonaTimbreTable.NarratorId
            && VoiceDowngrade.Resolve(SystemPersona.Snarky, systemEnabled: true) == _timbres.Of(SystemPersona.Snarky).Id;
        bool narratorDistinct = !personaBytes[_timbres.Of(SystemPersona.Snarky).Id]
            .SequenceEqual(PersonaVoiceSynth.SynthesizePcm(
                _timbres.Narrator, ProbeLineKey, _timbres.SampleRateHz, _timbres.PeakAmplitude));

        // 3) two one-time narrations + replay suppression + persistence
        var book = new NarrationBook(_narrations);
        bool openFirst = PlayNarration(book, "e1.open.narrator");
        bool victoryFirst = PlayNarration(book, "e1.end.handover");
        bool replaySilent = !PlayNarration(book, "e1.open.narrator");
        SaveNarration(book);
        NarrationBook reloaded = NarrationBook.Load(_host.Store, _narrations);
        bool persisted = reloaded.HasPlayed("e1.open.narrator") && reloaded.HasPlayed("e1.end.handover");
        int narrated = Convert.ToInt32(openFirst) + Convert.ToInt32(victoryFirst);

        // 4) three-channel mixer readback + ding regression on the SFX channel
        _mixer.SetVolume(AudioChannel.Bgm, 0.8);
        _mixer.SetVolume(AudioChannel.Sfx, 0.6);
        _mixer.SetVolume(AudioChannel.Voice, 0.9);
        (double bgmVol, double sfxVol, double voiceVol) = (
            _mixer.EffectiveGain(AudioChannel.Bgm),
            _mixer.EffectiveGain(AudioChannel.Sfx),
            _mixer.EffectiveGain(AudioChannel.Voice));
        bool busIndependent = Math.Abs(bgmVol - 0.8) < 1e-9
            && Math.Abs(sfxVol - 0.6) < 1e-9
            && Math.Abs(voiceVol - 0.9) < 1e-9;

        _ding.SetVolume(sfxVol); // SFX channel drives the T-060 ding bus (总线混音)
        DingOutcome? slotInfo = _ding.PlayCueSlot("ding_info", tick: 0);
        DingOutcome? slotUnknown = _ding.PlayCueSlot("ding_nope", tick: 0);
        int dingTiers = DingBus.TierMap.Values.Distinct().Count();
        bool dingRegression = slotInfo == DingOutcome.Played && slotUnknown is null && dingTiers == 3;

        int loops = _bgmStreams.Count;
        int stingers = _stingers.Count;
        // T-096 release fix: the WAV audition dump under res://audio/generated
        // is a dev-only convenience — exported packs have a read-only res://,
        // so the filesWritten criterion (and the dump itself) applies to
        // editor runs only. The "template" feature is present exactly in
        // exported builds (Godot docs: feature tags), never under the editor.
        bool editorRun = !OS.HasFeature("template");
        bool ok = transitions == 3 && equalPower && midIsSqrtHalf && combatDone && bossDone && calmDone && sameTargetNoOp
            && personasDistinct && personaAudible && downgrade && narratorDistinct
            && openFirst && victoryFirst && replaySilent && persisted
            && busIndependent && dingRegression
            && loops == 6 && stingers == 2 && (!editorRun || filesWritten >= 6);

        if (ok)
        {
            GD.Print(
                $"AUDIO_OK bgm=3states persona=4 narrated={narrated} bus=3vol " +
                $"cc0={stingers} eras={_programs.Programs.Count} loops={loops} " +
                $"fade={FadeSeconds:0.#}s states_played={_statePlays} " +
                $"bgm_vol={bgmVol:0.00} sfx_vol={sfxVol:0.00} voice_vol={voiceVol:0.00} " +
                $"ding_tiers={dingTiers} files={filesWritten} source=cc0-kenney+procedural");
        }
        else
        {
            GD.PushError(
                "audio acceptance failed: " +
                $"transitions={transitions} equal_power={equalPower} mid={midIsSqrtHalf} " +
                $"states={combatDone}/{bossDone}/{calmDone} noop={sameTargetNoOp} " +
                $"personas={personasDistinct} audible={personaAudible} downgrade={downgrade} " +
                $"narrator_distinct={narratorDistinct} narrations={openFirst}/{victoryFirst} " +
                $"replay_silent={replaySilent} persisted={persisted} bus={busIndependent} " +
                $"ding={dingRegression} loops={loops} stingers={stingers} files={filesWritten}");
            GD.Print(
                $"AUDIO_FAIL transitions={transitions} personas={personasDistinct} " +
                $"narrated={narrated} bus={busIndependent} ding={dingRegression} loops={loops} stingers={stingers}");
            GetTree().Quit(1);
        }
    }

    private void LoadContent()
    {
        _programs = BgmProgramTable.Parse(ReadContent("audio", "bgm-programs.json"), "audio/bgm-programs.json");
        _timbres = PersonaTimbreTable.Parse(ReadContent("audio", "persona-timbres.json"), "audio/persona-timbres.json");
        _narrations = NarrationTable.Parse(ReadContent("audio", "narration.json"), "audio/narration.json");
    }

    private static string ReadContent(string subDir, string file)
    {
        string relative = System.IO.Path.Combine("game", "content", subDir);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = System.IO.Path.Combine(dir.FullName, relative);
            if (Directory.Exists(probe))
            {
                return System.IO.File.ReadAllText(System.IO.Path.Combine(probe, file));
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/{subDir}（宿主须在仓库内运行）");
    }

    /// <summary>Writes one rendered artifact next to the project so it can be auditioned by hand (可听测).</summary>
    private static int PersistWav(string fileName, byte[] wav)
    {
        if (OS.HasFeature("template"))
        {
            return 0; // read-only res:// (exported pack, T-096): streams still play from memory
        }

        DirAccess? root = DirAccess.Open("res://");
        if (root is null)
        {
            return 0;
        }

        _ = root.MakeDirRecursive("audio/generated"); // AlreadyExists on later runs — fine
        string path = $"{GeneratedDir}/{fileName}";
        Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
        if (file is null)
        {
            return 0; // read-only res:// (exported pack): streams still play from memory
        }

        file.StoreBuffer(wav);
        file.Close();
        return 1;
    }
}
