using EraCore.Agent.Governance;
using EraCore.Cinematics;
using EraCore.Narrative;
using Godot;
using NarrPersona = EraCore.Narrative.SystemPersona;

namespace EraTD.Narrative;

// T-089 (C-24 叙事薄版) 叙事 HUD：EraCore/Narrative 纯逻辑导演的场景侧接线。
// 职责边界（CS-1.6：表现层零业务规则）——本节点只做三件事：
//   1) 内容装载：game/content/narrative/story-e1.json（+ e1 钩子行与绑定
//      演出脚本的拍面对接：波拍同时喂叙事导演与钩子路由）；
//   2) 节拍观察：每帧只读快照（波计数器跳变→wave 拍+波间闲聊；同拍把钩子
//      路由触发的演出 id 回填绑定幕），台词批次落气泡 Label（SystemPanel
//      报告气泡先例同形：『说话人：内容』）；胜利拍（交底+卡点预告+打脸
//      判定数据面）由结算挂载点（T-087）在战役宿主侧接出——开发沙盒宿主
//      无战役状态机，本 HUD 不伪造胜利；
//   3) 冒烟：headless 合成节拍流走完 E1 黄金三章并打印 NARR_OK。
// 并行纪律：Main.cs 只追加一行 Instantiate（冲突由后提交者 rebase）。

/// <summary>
/// The narrative HUD: binds the pure
/// <see cref="EraCore.Narrative.NarrativeDirector"/> to the live
/// <see cref="SimHost"/> and renders the emitted dialogue lines through the
/// report-bubble precedent (text bubble, last line of each batch).
/// </summary>
public sealed partial class NarrativeHud : Control
{
    private const int BubbleMaxChars = 48;

    /// <summary>气泡配图边长（T-152：96px 方形缩略，Nearest 全局过滤）。</summary>
    private const int BubbleImageSize = 96;

    private SimHost _host = null!;
    private NarrativeDirector? _director;
    private CinematicHookRouter? _router;
    private int _lastWave; // 0 = pre-first-wave grace（首帧不误发波拍）
    private EraCore.Cinematics.VisualAssetCatalog? _catalog; // T-152 配图位素材目录
    private EraCore.Narrative.PersonaLineBank? _personaBank; // T-126 四人格情境库（切换问候/人格行消费面）
    private Audio.DingAudioPlayer? _ding; // T-126 台词触发接音效位（ding_* 三级）
    private Audio.AudioDirector? _audioDirector; // T-126 人格语音 token（三联动音色臂）

    private Label _bubble = null!;
    private TextureRect _bubbleImage = null!;

    // T-152 冒烟探针：配图渲染/降级计数
    internal int _imagesShown;
    internal int _imagesDegraded;

    // T-126 冒烟探针：音效位触发计数（台词 cue → ding_* 播放尝试）
    internal int _cuesPlayed;

    /// <summary>Mount helper (Main.cs single-line wiring, same as the other HUDs).</summary>
    internal static NarrativeHud Instantiate(Main main, SimHost host)
    {
        // Name 固定（T-126）：代码挂载节点缺省名是 @Control@N——切换缝/冒烟按名
        // 解析（InfoLayersUi 与 Main 的 GetNodeOrNull("NarrativeHud")）须有名可寻。
        var hud = new NarrativeHud { _host = host, Name = "NarrativeHud" };
        main.AddChild(hud); // _Ready builds the bubble (and smokes headless)
        return hud;
    }

    public override void _Ready()
    {
        _bubble = new Label
        {
            Visible = false,
            Position = new Vector2(8f, 300f),
            Size = new Vector2(300f, 18f),
        };
        _bubble.AddThemeFontSizeOverride("font_size", 11);
        _bubble.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        AddChild(_bubble);

        // T-152 配图位：气泡左侧 96px 插画缩略（缺图隐藏——自动降级）
        _bubbleImage = new TextureRect
        {
            Visible = false,
            Position = new Vector2(8f, 196f),
            Size = new Vector2(BubbleImageSize, BubbleImageSize),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_bubbleImage);

        if (LoadContent())
        {
            SmokeHeadless();
        }
    }

    /// <summary>Live path: one frame poll — read-only snapshot beats only.</summary>
    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_director is null || _router is null)
        {
            return;
        }

        int wave = _host.Simulation.CurrentWave;
        if (wave == _lastWave)
        {
            return;
        }

        _lastWave = wave;
        long tick = _host.Simulation.CurrentTick;
        Show(_director.OnWaveStart(wave, tick)); // 幕推进 + 打脸嘲讽拍
        foreach (string scriptId in _router.OnWaveStart(wave))
        {
            Show(_director.OnCinematicFired(scriptId, tick)); // 绑定幕（演出已有=T-086）
        }

        if (_director.OnWaveEnd() is { } idle)
        {
            Show(new[] { idle }); // 波间军师闲聊（档位×人格加权）
        }
    }

    /// <summary>Director under direction (smoke probe).</summary>
    internal NarrativeDirector? Director => _director;

    /// <summary>T-126：人格情境库（冒烟探针；装载失败=null——人格行消费面降级）。</summary>
    internal EraCore.Narrative.PersonaLineBank? PersonaBank => _personaBank;

    /// <summary>
    /// T-126 三联动台词臂：人格切换后展示切入自报行（bank 的 switch.in 面）
    /// 并按当前人格播语音 token——InfoLayersUi.ApplyPersona 在
    /// NarrativeDirector.SetPersona 之后调用本缝，台词/音色/立绘三臂同拍切换。
    /// </summary>
    public void OnPersonaApplied(NarrPersona persona)
    {
        string key = $"{EraCore.Presentation.PersonaChoice.IdOf(persona)}.switch.in";
        if (_personaBank?.Find(key) is not { } row)
        {
            return; // 库缺行（装载失败面）——切换仍生效，仅问候静默
        }

        DialogueLine line = row.ToDialogueLine();
        Show(new[] { line });
        _audioDirector ??= GetParent().GetNodeOrNull<Audio.AudioDirector>("AudioDirector"); // 晚挂载重解析
        _audioDirector?.PlayLineVoice(line, persona, systemEnabled: true);
    }

    private void Show(IReadOnlyList<DialogueLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        DialogueLine line = lines[^1]; // 气泡一帧一行：批次尾行（全批次留痕归战报/字幕面）
        string text = line.BubbleText.Length > BubbleMaxChars
            ? line.BubbleText[..BubbleMaxChars] + "…"
            : line.BubbleText;
        _bubble.Text = text;
        _bubble.Visible = true;

        // T-126 台词触发接音效位（F-105/F-137 全量）：DialogueLine.SoundCue →
        // 叮通道三级槽（ding_info/ding_reward/ding_crisis——封闭槽集与
        // CinematicScript/DingBus 同表；节流归 DingBus，此处只投递）。
        if (line.SoundCue is { } cue && _ding is not null)
        {
            _ = _ding.PlayCueSlot(cue, _host.Simulation.CurrentTick);
            _cuesPlayed++;
        }

        // T-152 ③b 配图位（31 号 §三 b 位）：DialogueLine.ImageKey→插画贴图；
        // 缺图自动降级纯文本（外部轨 D21-5，U 盘不在机/键未配=隐藏）。
        if (line.ImageKey is { } key && _catalog is not null)
        {
            Texture2D? texture = Expand.ExpandAssetPaths.LoadPng(
                Expand.ExpandAssetPaths.ExternalImagePath(_catalog, key));
            if (texture is not null)
            {
                _bubbleImage.Texture = texture;
                _bubbleImage.Visible = true;
                _bubble.Position = new Vector2(8f + BubbleImageSize + 6f, _bubble.Position.Y);
                _imagesShown++;
            }
            else
            {
                _bubbleImage.Visible = false;
                _bubble.Position = new Vector2(8f, _bubble.Position.Y);
                _imagesDegraded++;
            }
        }
        else
        {
            _bubbleImage.Visible = false;
            _bubble.Position = new Vector2(8f, _bubble.Position.Y);
        }
    }

    /// <summary>
    /// Headless acceptance smoke: the synthetic E1 beat schedule walks the
    /// golden three acts (wave 2 → binding cinematic → wave 5 → victory) and
    /// samples the trust-tier pools, then reports the loop math.
    /// </summary>
    private void SmokeHeadless()
    {
        NarrativeDirector director = _director!;
        _ = director.Begin();

        const long wave2Tick = 28_800; // L1-1 生产节拍实锚：grace(21600)+1×interval(7200)
        const long wave5Tick = 50_400; // grace+4×interval
        var act1Batch = director.OnWaveStart(2, wave2Tick); // 受辱幕+波 2 嘲讽
        int actLines = act1Batch.Count;
        Show(act1Batch); // T-152：冒烟也走真实渲染路径（配图位显示/降级计数在此累积）
        var bindBatch = director.OnCinematicFired(_router!.OnWaveStart(2).Single(), wave2Tick);
        int bindLines = bindBatch.Count;
        Show(bindBatch);
        var slapBatch = director.OnWaveStart(5, wave5Tick);
        int slapLines = slapBatch.Count;
        Show(slapBatch);
        MockArcVerdict verdict = MockArcRulebook.Judge(director.MockArc!, victory: true, towersStanding: 3);

        int acts = director.ActsCompleted;
        double loop = director.Golden.LoopSeconds ?? -1;
        bool within = director.Golden.WithinBudget;
        int budget = director.Chapter.GoldenActs.BudgetSeconds;

        // 闲聊池抽样：三档各一抽（人格加权面冒烟；须在收章前抽——收章后闲聊停）
        var samples = new List<string>();
        foreach (TrustBadge badge in new[] { TrustBadge.Skeptical, TrustBadge.Trustworthy, TrustBadge.Devoted })
        {
            director.SetTrust(badge);
            if (director.OnWaveEnd() is { } line)
            {
                samples.Add(line.Key);
            }
        }

        var ending = director.OnVictory(wave5Tick + 30);

        bool ok = acts == 3 && loop > 0 && within && verdict.HiddenHit && ending.Count > 0 && samples.Count == 3
            && _imagesShown + _imagesDegraded >= 1; // T-152：配图位至少被走到一次（显示或降级）
        GD.Print(
            $"NARR_OK acts={acts} loop={loop:0}s budget={budget}s within={(within ? 1 : 0)} " +
            $"lines={actLines}+{bindLines}+{slapLines} idle_samples={samples.Count} " +
            $"hidden_tier={verdict.HiddenChestTier} sim_towers={_host.Simulation.Towers.Count} " +
            $"bubble_images={_imagesShown}shown+{_imagesDegraded}degraded");
        if (!ok)
        {
            GD.Print("NARR_FAIL golden loop invariant broken");
            GetTree().Quit(1);
        }
    }

    /// <summary>Loads the E1 story chapter plus the hook rows, the bound aha script and the persona bank.</summary>
    private bool LoadContent()
    {
        try
        {
            StoryChapter story = StoryChapter.Parse(
                ReadContent("narrative", "story-e1.json"), "narrative/story-e1.json");
            _director = new NarrativeDirector(
                story, TrustBadge.Trustworthy, NarrPersona.Advisor, story.MockArcFor(story.LevelId));
            _router = new CinematicHookRouter(
                CinematicHookRouter.ParseLevelHooks(ReadContent("levels", "e1.json"), "levels/e1.json"),
                new[] { CinematicScript.Parse(ReadContent("cinematics", "e1-aha.json"), "cinematics/e1-aha.json") });
            _catalog = EraCore.Cinematics.VisualAssetCatalog.Parse(
                ReadContent("cinematics", "visual-assets.json"), "cinematics/visual-assets.json");
            _personaBank = EraCore.Narrative.PersonaLineBank.Parse(
                ReadContent("narrative", "persona-lines.json"), "narrative/persona-lines.json");

            // T-126 台词→音效位/语音 token 接线：兄弟节点晚绑定（本 HUD 在
            // AudioDirector 之前挂载——DingAudio 属场景树恒在，AudioDirector
            // 可能为 null，切换缝/冒烟段重解析（ApplyPersona 时再取）。
            _ding = GetParent().GetNodeOrNull<Audio.DingAudioPlayer>("DingAudio");
            _audioDirector = GetParent().GetNodeOrNull<Audio.AudioDirector>("AudioDirector");
            return true;
        }
        catch (Exception ex)
        {
            GD.Print($"NARR_FAIL load={ex.Message}");
            GetTree().Quit(1);
            return false;
        }
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
}
