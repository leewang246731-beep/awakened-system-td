using EraCore.CampaignLoop;
using EraCore.Contracts.Foundation;
using EraCore.Contracts.Sim;
using EraCore.Sim;
using Godot;

namespace EraTD;

// T-160b 战役战场渲染绑定（REV-20261018-T160 §七遗留③ 的收口半边）：
// 任意 LevelSession 泳道（战役/程序关）复用单机渲染路径的视口件——先例=
// NetClientViewport（镜像快照→SimSnapshotBridge→双 MultiMesh 层，读它怎么
// 做的）。四条重绑律：
//   ① 关随图变（GridWidth/Height/地形）→Bind 检测 sim 引用变化即整面重建
//     （桥+双层 Setup+时代路由图集 era1~era6 既有）；
//   ② 关卡切换/跃迁后重进再重绑（每帧 Bind 幂等——引用相等零动作）；
//   ③ 退出到菜单=Release（战场层隐藏+虚拟相机平移归零——恢复菜单态口径，
//     宿主夹具画面/菜单面板覆盖其上）；
//   ④ 虚拟相机按新图尺寸重居中（图心=画布心；T-163 实测收口：Position
//     平移替代 Camera2D——后者会平移 root 画布全体 UI）。
// 承载面纪律（NetClientViewport 同律）：本视口自持节点树，不触碰 Main 的
// SimHost 夹具泳道（headless 冒烟既有段不破）；宿主泳道的夹具层由宿主 UI
// （CampaignMenuUi/ProceduralMenuUi）在 Bind/Release 时隐藏/恢复（同层不叠画）。

/// <summary>
/// The session battlefield viewport (T-160b): renders any
/// <see cref="LevelSession"/>-backed lane through the SAME dirty-chunk path
/// the single-player Main runs, re-binds wholesale when the bound simulation
/// reference changes (new level / re-entry / era transition), and releases
/// the camera plus hides its layers on exit-to-menu.
/// </summary>
public sealed partial class SessionBattlefieldViewport : Control
{
    private const int UnitLayerCapacity = 256; // 战役波预算余量（NetClientViewport 同带）
    private const int GroundZIndex = 0;        // main.tscn 双层同 z（隐藏夹具层后无叠画）
    private const int UnitZIndex = 10;

    // —— T-164（D 批皮肤与手感）：塔覆盖层（静态 sprite+待机 4 相条带）与
    //    伤害飘字层（对象池）——挂在自持树内随 Bind/Release 同生命周期。 ——
    private const int TowerZIndex = 5;   // 地面(0)之上、单位(10)之下
    private const int FloatZIndex = 12;  // 单位之上（飘字不被敌群盖住）
    private Control _towerLayer = null!;
    private Control _floatLayer = null!;
    private readonly Dictionary<long, TextureRect> _towerOverlays = new(); // towerId → sprite
    private readonly Dictionary<long, int> _unitHpSeen = new();            // 伤害飘字的 HP 差分基线
    private int _idleStripsLive;                                           // 待机动画条带在播数（探针）

    private MultiMeshRenderer _groundLayer = null!;
    private MultiMeshRenderer _unitLayer = null!;
    private SimSnapshotBridge _bridge = null!;

    private Simulation? _bound;
    private int _boundEra;
    private IFramebufferProvider? _boundFramebuffer;

    private int _framesDrawn;
    private int _groundChunkRebuilds;
    private int _unitChunkRebuilds;
    private int _maxUnitsSeen;

    private SessionBattlefieldViewport()
    {
    }

    /// <summary>Code-built instantiation: camera + two render layers (NetClientViewport 同律).</summary>
    public static SessionBattlefieldViewport Instantiate(Control host)
    {
        var viewport = new SessionBattlefieldViewport
        {
            Name = "SessionBattlefieldViewport",
        };
        viewport.BuildTree();
        host.AddChild(viewport);
        return viewport;
    }

    private void BuildTree()
    {
        _groundLayer = new MultiMeshRenderer { Name = "SessionGroundLayer", ZIndex = GroundZIndex };
        AddChild(_groundLayer);
        _towerLayer = new Control { Name = "SessionTowerLayer", ZIndex = TowerZIndex, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_towerLayer);
        _unitLayer = new MultiMeshRenderer { Name = "SessionUnitLayer", ZIndex = UnitZIndex };
        AddChild(_unitLayer);
        _floatLayer = new Control { Name = "SessionFloatLayer", ZIndex = FloatZIndex, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_floatLayer);
        _groundLayer.Hide();
        _unitLayer.Hide();
        _towerLayer.Hide();
        _floatLayer.Hide();
        BuildFloatPool();
    }

    /// <summary>
    /// Binds (or re-binds) the viewport onto a live level session's
    /// simulation: a reference change rebuilds the bridge and both layers
    /// from the NEW world's dimensions and era palette (律①②④). Idempotent
    /// per frame — an unchanged reference costs one comparison.
    /// </summary>
    /// <param name="session">The live campaign/procedural level session.</param>
    /// <param name="eraIndex">Era ordinal 1..6 — routes the terrain atlas.</param>
    public void Bind(LevelSession session, int eraIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        // 守卫=sim 引用+eraIndex 双比（T-160 审查 #4b：当前 era 变必换 sim，
        // 但若未来同 sim 换时代（混编宿主等）图集也须重路由——双条件堵潜在陷阱）。
        if (ReferenceEquals(_bound, session.Sim) && _boundEra == eraIndex)
        {
            return;
        }

        _bound = session.Sim;
        _boundEra = eraIndex;
        var source = new SessionSnapshotSource(session.Sim);
        _boundFramebuffer = new SessionFramebuffer(session.Sim);
        _bridge = new SimSnapshotBridge(
            source, _boundFramebuffer,
            _boundFramebuffer.WidthCells, _boundFramebuffer.HeightCells,
            chunkCells: EraCore.Presentation.DirtyRegionSet.DefaultChunkCells);

        int width = _boundFramebuffer.WidthCells;
        int height = _boundFramebuffer.HeightCells;
        float cell = SimSnapshotBridge.CellSizePx;
        // T-163（C 批战斗 HUD 图标化实测收口）：「虚拟相机」=自持树整体
        // Position 平移（图心→画布心），不再启用 Camera2D——任何 Camera2D
        // 都会平移 root 画布层全体（SystemPanel/建造栏/BattleHudUi 全部
        // Control 一起被牵移出屏，多相机 MakeCurrent 也只换平移源不除平移）。
        // Position 平移只动本视口两层 MultiMesh，会话 UI 纹丝不动。
        Position = new Vector2(
            EraCore.Presentation.ViewportScale.BaseWidth / 2f - width * cell / 2f,
            EraCore.Presentation.ViewportScale.BaseHeight / 2f - height * cell / 2f);

        (Texture2D atlas, Func<int, int> tileIndex, int tilesPerRow) = EraGroundArt.Of(eraIndex);
        _groundLayer.SetupGround(
            width, height, EraCore.Presentation.DirtyRegionSet.DefaultChunkCells,
            atlas, tileIndex, tilesPerRow);
        _unitLayer.SetupUnits(
            width, height, EraCore.Presentation.DirtyRegionSet.DefaultChunkCells,
            capacity: UnitLayerCapacity);
        _groundLayer.Show();
        _unitLayer.Show();
        _towerLayer.Show();
        _floatLayer.Show();
        ClearTowerOverlays();  // T-164：换局=塔覆盖层整面重建（新世界新塔集）
        _unitHpSeen.Clear();   // HP 差分基线随局重置（旧局单位不入新局飘字）
    }

    /// <summary>
    /// Exit-to-menu restore (律③): hide both layers and reset the virtual
    /// camera shift（T-163：Position 归零——战场层隐藏、平移随 Release 撤销）.
    /// </summary>
    public void Release()
    {
        _bound = null;
        _boundFramebuffer = null;
        Position = Vector2.Zero;
        _groundLayer.Hide();
        _unitLayer.Hide();
        _towerLayer.Hide();
        _floatLayer.Hide();
        ClearTowerOverlays();
        _unitHpSeen.Clear();
    }

    /// <summary>
    /// One session render frame: diff the latest snapshot into dirty chunks
    /// and rebuild exactly those（单机 Main 同一脏块路径——会话渲染不发明
    /// 第二条渲染法，NetClientViewport 同律）。
    /// </summary>
    public void PumpFrame()
    {
        if (_bound is null)
        {
            return;
        }

        _bridge.SyncFrame();
        var groundChunks = _bridge.TakeDirtyGroundChunks();
        if (groundChunks.Count > 0)
        {
            _groundLayer.RebuildGroundChunks(groundChunks, _boundFramebuffer!);
            _groundChunkRebuilds += groundChunks.Count;
        }

        var unitChunks = _bridge.TakeDirtyUnitChunks();
        if (unitChunks.Count > 0 && _bridge.Current is not null)
        {
            _unitLayer.RebuildUnitChunks(unitChunks, _bridge.Current);
            _unitChunkRebuilds += unitChunks.Count;
            _maxUnitsSeen = Math.Max(_maxUnitsSeen, _bridge.Current.Units.Count);
        }

        SyncTowerOverlays();     // T-164：塔 sprite/待机条带随会话塔集增删（幂等差分）
        PumpDamageFloatsFrame(); // T-164：HP 差分→伤害飘字（对象池，零每拍分配）
        _framesDrawn++;
    }

    // ------------------------------------------------ T-164 塔覆盖层（待机动画接线）

    /// <summary>
    /// 塔覆盖层同步（幂等差分）：会话 <c>Sim.Towers</c> 新增=建 sprite 节点
    /// （era 图集缓存先例——<see cref="TowerOverlayArt"/> 静态缓存，缺图=
    /// 零覆盖不阻塞）；移除=释放。待机 4 相条带塔（era5 力场塔/era6 时空塔，
    /// T-157 ⑥既有素材）=<see cref="IdleStripSprite"/> 帧步进常播
    /// （AnimatedTexture 在 4.7.2 属弃用 API——CS0618 会触零警告门禁，帧序
    /// 列自步进为等价原生形态，见 :305）；其余塔=静态 sprite
    /// （textures/era{N}/towers/{stem}-l{tier}.png 确定性推导——T3 纪律）。
    /// </summary>
    private void SyncTowerOverlays()
    {
        if (_bound is null)
        {
            return;
        }

        var live = new HashSet<long>();
        _idleStripsLive = 0;
        foreach (SimTower tower in _bound.Towers)
        {
            long id = tower.Id.Value;
            live.Add(id);
            if (_towerOverlays.TryGetValue(id, out TextureRect? overlay))
            {
                if (overlay is IdleStripSprite)
                {
                    _idleStripsLive++;
                }

                continue;
            }

            // 待机条带塔=帧步进 sprite；其余=静态 sprite；缺图=零覆盖不阻塞
            Node? sprite = null;
            if (TowerOverlayArt.IdleFramesOf(tower.Spec.Id) is { } idle)
            {
                sprite = new IdleStripSprite(idle) { PhaseOffset = id % 4 };
            }
            else if (TowerOverlayArt.Of(tower.Spec.Id, tower.Spec.Tier) is { } art)
            {
                sprite = new TextureRect
                {
                    Texture = art,
                    MouseFilter = MouseFilterEnum.Ignore,
                };
            }

            if (sprite is not Control control)
            {
                continue; // 缺图零覆盖（优雅降级——塔仍占格，战斗不受阻）
            }

            control.Position = new Vector2(
                tower.Cell.X * SimSnapshotBridge.CellSizePx, tower.Cell.Y * SimSnapshotBridge.CellSizePx);
            control.Size = new Vector2(SimSnapshotBridge.CellSizePx, SimSnapshotBridge.CellSizePx); // 1 格位（32px 整格锚定）
            if (control is TextureRect rect)
            {
                rect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                rect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            }

            _towerLayer.AddChild(control);
            _towerOverlays[id] = (TextureRect)control;
            if (control is IdleStripSprite)
            {
                _idleStripsLive++;
            }
        }

        foreach (KeyValuePair<long, TextureRect> pair in _towerOverlays.Where(pair => !live.Contains(pair.Key)).ToList())
        {
            _towerOverlays.Remove(pair.Key);
            pair.Value.QueueFree();
        }
    }

    private void ClearTowerOverlays()
    {
        foreach (TextureRect sprite in _towerOverlays.Values)
        {
            sprite.QueueFree();
        }

        _towerOverlays.Clear();
        _idleStripsLive = 0;
    }

    /// <summary>塔覆盖层在数（--shot 取景探针）.</summary>
    internal int TowerOverlayCount => _towerOverlays.Count;

    /// <summary>待机 4 相条带在播数（--shot 取景探针——era5/era6 条带塔真接线的观测位）.</summary>
    internal int IdleStripsLive => _idleStripsLive;

    /// <summary>首个待机条带塔的全局矩形（--shot 特写取景探针；无在播=null）.</summary>
    internal Rect2? IdleOverlayGlobalRect =>
        _towerOverlays.Values.FirstOrDefault(sprite => sprite is IdleStripSprite)?.GetGlobalRect();

    /// <summary>会话当前 tick（--shot 轮询诊断——波次推进的观测位）.</summary>
    internal long BoundTick => _bound?.CurrentTick ?? -1;

    /// <summary>首个待机条带塔的当前相位 0..3（--shot 两相位证据；无在播=-1）.</summary>
    internal int IdlePhaseNow =>
        _towerOverlays.Values.OfType<IdleStripSprite>().FirstOrDefault()?.CurrentPhase ?? -1;

    /// <summary>
    /// 塔 sprite/待机条带静态缓存（era 图集缓存先例——EraGroundArt 同律）：
    /// res://textures/era{N}/towers/{stem}-l{tier}.png 确定性推导；era5 力场塔
    /// /era6 时空塔换装 4 相帧序列（64×64 条带 4×16px 横排——帧步进由
    /// <see cref="IdleStripSprite"/> 承担；AnimatedTexture 在 4.7.2 属弃用
    /// API（CS0618 零警告门禁），帧序列+自步进为引擎原生等价形态）。
    /// </summary>
    internal static class TowerOverlayArt
    {
        /// <summary>待机条带塔（内容 id→条带路径——T-157 ⑥既有素材的封闭集）.</summary>
        private static readonly Dictionary<string, string> IdleStripPaths = new(StringComparer.Ordinal)
        {
            ["era5:tower-force-field"] = "res://textures/era5/anim/tower-force-field-anim.png", // 力场塔 4 相脉冲呼吸
            ["era6:tower-spacetime"] = "res://textures/era6/anim/tower-spacetime-anim.png",     // 时空塔 4 相时间涟漪
        };

        private const int IdleStripFrames = 4;   // 4 相（条带 64×64=4×16px 横排）
        private const int IdleStripFramePx = 16; // ci-allow: R7 T-164 条带帧边长 px（素材规格）

        private static readonly Dictionary<string, Texture2D?> _cache = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, IReadOnlyList<AtlasTexture>?> _idleCache = new(StringComparer.Ordinal);

        /// <summary>待机条带帧序列（条带塔=null 外；缺图=null）.</summary>
        public static IReadOnlyList<AtlasTexture>? IdleFramesOf(string contentId)
        {
            if (_idleCache.TryGetValue(contentId, out IReadOnlyList<AtlasTexture>? cached))
            {
                return cached;
            }

            if (!IdleStripPaths.TryGetValue(contentId, out string? stripPath)
                || !ResourceLoader.Exists(stripPath)
                || GD.Load<Texture2D>(stripPath) is not { } strip)
            {
                _idleCache[contentId] = null;
                return null;
            }

            var frames = new AtlasTexture[IdleStripFrames];
            for (int frame = 0; frame < IdleStripFrames; frame++)
            {
                frames[frame] = new AtlasTexture
                {
                    Atlas = strip,
                    Region = new Rect2(frame * IdleStripFramePx, 0f, IdleStripFramePx, IdleStripFramePx),
                };
            }

            _idleCache[contentId] = frames;
            return frames;
        }

        /// <summary>静态塔 art（非条带塔；缺图=null）.</summary>
        public static Texture2D? Of(string contentId, int tier)
        {
            string key = $"{contentId}#t{Math.Clamp(tier, 1, 4)}";
            if (_cache.TryGetValue(key, out Texture2D? cached))
            {
                return cached;
            }

            Texture2D? art = LoadStaticSprite(contentId, tier);
            _cache[key] = art;
            return art;
        }

        /// <summary>静态塔 sprite（{stem}-l{tier}，tier 越档回落 l1——存在性探测）.</summary>
        private static Texture2D? LoadStaticSprite(string contentId, int tier)
        {
            string stem = contentId.Replace(':', '-'); // 冒号转横线（HudIconDecisions.IconStemOf 同律）
            int era = contentId.Length > 4 && contentId.StartsWith("era", StringComparison.Ordinal)
                && int.TryParse(contentId[3..4], out int parsed) ? parsed : 1;
            for (int level = Math.Clamp(tier, 1, 2); level >= 1; level--)
            {
                string path = $"res://textures/era{era}/towers/{stem}-l{level}.png";
                if (ResourceLoader.Exists(path))
                {
                    return GD.Load<Texture2D>(path);
                }
            }

            return null; // 该时代塔无 sprite 素材（era 图集未覆盖位）——零覆盖
        }
    }

    /// <summary>
    /// 待机 4 相帧步进 sprite（T-164 接线件）：按固定帧率在 4 相条带帧序列上
    /// 循环（<c>_Process</c> 自步进——确定性相序；PhaseOffset 按塔 id 错相，
    /// 同型塔不同步呼吸）。非条带塔走普通 TextureRect（零步进开销）。
    /// </summary>
    internal sealed partial class IdleStripSprite : TextureRect
    {
        private const double FramesPerSecond = 5.0; // ci-allow: R7 T-164 待机动画帧率（表现层演出节奏，非平衡数值）

        private readonly IReadOnlyList<AtlasTexture> _frames;
        private double _accumulator;

        /// <summary>当前相位（0 起——--shot 两相位证据探针）.</summary>
        internal int CurrentPhase { get; private set; }

        /// <summary>起始相（塔 id 取模——同型塔错相呼吸）.</summary>
        internal long PhaseOffset { private get; init; }

        internal IdleStripSprite(IReadOnlyList<AtlasTexture> frames)
        {
            _frames = frames;
            Texture = frames.Count > 0 ? frames[0] : null;
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Process(double delta)
        {
            base._Process(delta);
            if (_frames.Count < 2)
            {
                return;
            }

            _accumulator += delta * FramesPerSecond;
            if (_accumulator < 1.0)
            {
                return;
            }

            int advance = (int)_accumulator;
            _accumulator -= advance;
            int next = (int)((Engine.GetProcessFrames() + (ulong)Math.Max(0, PhaseOffset)) % (ulong)_frames.Count);
            CurrentPhase = next;
            Texture = _frames[next];
        }
    }

    // ------------------------------------------------ T-164 伤害飘字（对象池）

    private const int FloatPoolSize = 32;     // 同屏飘字预算（波峰并发覆盖——对象池上限）
    private const float FloatRisePerFrame = 0.9f; // ci-allow: R7 T-164 上飘速度 px/帧（表现层动画）
    private const int FloatLifeFrames = 45;   // ci-allow: R7 T-164 淡出寿命帧数（~0.75s @60fps）
    private const float FloatFontSizePx = 10; // ci-allow: R7 T-164 飘字字号 px（像素风小号）
    private const int FloatsPerUnitMax = 2;   // ci-allow: R7 T-165 同目标同屏飘字上限（53 号文 P2-2 限流，表现层）

    private readonly Queue<Label> _floatPool = new();
    private readonly List<(Label Label, int Life, long UnitId, int Total)> _floatsLive = new();

    private void BuildFloatPool()
    {
        for (int i = 0; i < FloatPoolSize; i++)
        {
            var label = new Label
            {
                Visible = false,
                MouseFilter = MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            label.AddThemeFontSizeOverride("font_size", (int)FloatFontSizePx);
            label.AddThemeColorOverride("font_color", new Color(1f, 0.86f, 0.45f)); // ci-allow: R7 T-164 伤害暖色（表现层）
            UiFeel.Outline(label, 3);
            _floatLayer.AddChild(label);
            _floatPool.Enqueue(label);
        }
    }

    /// <summary>
    /// 伤害飘字帧泵：快照 HP 差分（纯读 K-6 快照——CS-1.5，模拟零改动）→
    /// 命中格上飘淡出。对象池复用（池满=最旧让位——防每拍分配）；飘完回收。
    /// </summary>
    private void PumpDamageFloatsFrame()
    {
        if (_bridge.Current is { } snapshot)
        {
            foreach (IUnit unit in snapshot.Units)
            {
                if (_unitHpSeen.TryGetValue(unit.Id.Value, out int seen) && unit.Hp < seen && seen - unit.Hp > 0)
                {
                    SpawnDamageFloat(seen - unit.Hp, unit.Id.Value, unit.X, unit.Y);
                }

                _unitHpSeen[unit.Id.Value] = unit.Hp;
            }

            // 离场单位清基线（复活/重进不误飘）
            if (_unitHpSeen.Count > snapshot.Units.Count * 2 + 64)
            {
                var live = new HashSet<long>(snapshot.Units.Select(unit => unit.Id.Value));
                foreach (long id in _unitHpSeen.Keys.Where(id => !live.Contains(id)).ToList())
                {
                    _unitHpSeen.Remove(id);
                }
            }
        }

        for (int i = _floatsLive.Count - 1; i >= 0; i--)
        {
            (Label label, int life, long unitId, int total) = _floatsLive[i];
            label.Position += new Vector2(0f, -FloatRisePerFrame);
            label.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp((float)life / FloatLifeFrames, 0f, 1f));
            if (life <= 1)
            {
                label.Visible = false;
                _floatsLive.RemoveAt(i);
                _floatPool.Enqueue(label);
            }
            else
            {
                _floatsLive[i] = (label, life - 1, unitId, total);
            }
        }
    }

    private void SpawnDamageFloat(int damage, long unitId, float x, float y)
    {
        // T-165 飘字限流+重叠合并（53 号文 P2-2）：同目标同屏 ≤2 条；超限命中
        // 并入该目标最新一条（数字累加重显+寿命刷新——重叠处一条不断更新的
        // 合计，不再第三条压顶）。
        int newest = -1;
        int liveForUnit = 0;
        for (int i = _floatsLive.Count - 1; i >= 0; i--)
        {
            if (_floatsLive[i].UnitId != unitId)
            {
                continue;
            }

            if (newest < 0)
            {
                newest = i;
            }

            liveForUnit++;
        }

        if (liveForUnit >= FloatsPerUnitMax && newest >= 0)
        {
            (Label merged, _, _, int total) = _floatsLive[newest];
            int sum = total + damage;
            _floatsLive[newest] = (merged, FloatLifeFrames, unitId, sum);
            merged.Text = $"{sum}";
            return;
        }

        if (_floatPool.Count == 0 && _floatsLive.Count > 0)
        {
            // 池尽=最旧让位（波峰并发防丢新命中——近尾声的旧字先退）
            (Label oldest, _, _, _) = _floatsLive[0];
            _floatsLive.RemoveAt(0);
            _floatPool.Enqueue(oldest);
        }

        if (_floatPool.Count == 0)
        {
            return; // 无可用（理论不可达——池=活集上限）
        }

        Label label = _floatPool.Dequeue();
        label.Text = $"{damage}";
        label.Position = new Vector2(x - 8f, y - 14f); // ci-allow: R7 T-164 飘字锚点偏移 px（命中点上飘起点）
        label.Modulate = Colors.White;
        label.Visible = true;
        _floatsLive.Add((label, FloatLifeFrames, unitId, damage));
    }

    /// <summary>当前在屏飘字数（--shot 取景探针——飘字进行时的真信号观测位）.</summary>
    internal int DamageFloatsActive => _floatsLive.Count;

    // ------------------------------------------------ probes (smoke/observability)

    /// <summary>Render frames pumped so far.</summary>
    public int FramesDrawn => _framesDrawn;

    /// <summary>Ground chunk rebuilds so far.</summary>
    public int GroundChunkRebuilds => _groundChunkRebuilds;

    /// <summary>Unit chunk rebuilds so far.</summary>
    public int UnitChunkRebuilds => _unitChunkRebuilds;

    /// <summary>Peak live unit count observed through the bridge.</summary>
    public int MaxUnitsSeen => _maxUnitsSeen;

    /// <summary>Bound map width in cells (0 while released).</summary>
    public int BoundWidthCells => _boundFramebuffer?.WidthCells ?? 0;

    /// <summary>Bound map height in cells (0 while released).</summary>
    public int BoundHeightCells => _boundFramebuffer?.HeightCells ?? 0;

    /// <summary>Bound era ordinal (era atlas routing probe; 0 while released).</summary>
    public int BoundEra => _bound is null ? 0 : _boundEra;

    /// <summary>Whether a session is currently bound (camera owned).</summary>
    public bool IsBound => _bound is not null;

    /// <summary>
    /// Terrain signature of the bound world (color→cell count, 冒烟判据「地
    /// 形哈希=该关真图」的数据面): counts every cell the framebuffer would
    /// draw. Empty while released.
    /// </summary>
    public IReadOnlyDictionary<int, int> TerrainSignature()
    {
        var counts = new Dictionary<int, int>();
        if (_boundFramebuffer is null)
        {
            return counts;
        }

        for (int y = 0; y < _boundFramebuffer.HeightCells; y++)
        {
            for (int x = 0; x < _boundFramebuffer.WidthCells; x++)
            {
                int rgb = _boundFramebuffer.ColorAt(x, y);
                counts[rgb] = counts.TryGetValue(rgb, out int seen) ? seen + 1 : 1;
            }
        }

        return counts;
    }

    // ------------------------------------------------ era routing + adapters

    /// <summary>
    /// Era palette routing (T-160b 律①): era1 keeps the E1 atlas + its
    /// color→slot table; era2~6 route to their own composed atlases
    /// (Era2~Era6TerrainAtlas — validator-gated, already loaded by the ART
    /// markers) with TerrainKind slots reached through the inverse of the
    /// framebuffer color coding.
    /// </summary>
    internal static class EraGroundArt
    {
        // 按时代缓存（T-160 审查 #4）：每次 rebind 重 Build 图集=8 张 PNG 重载合成，
        // 29 关连打累积且旧 ImageTexture 无释放引用链；静态缓存一份常驻（六图集
        // 总量远小于一关 MultiMesh 容量，常驻可接受）。
        private static readonly (Texture2D Atlas, Func<int, int> TileIndex, int TilesPerRow)[] _cache = new (Texture2D, Func<int, int>, int)[7];

        public static (Texture2D Atlas, Func<int, int> TileIndex, int TilesPerRow) Of(int eraIndex)
        {
            int slot = Math.Clamp(eraIndex, 1, 6);
            if (_cache[slot].Atlas is null)
            {
                _cache[slot] = Build(slot);
            }

            return _cache[slot];
        }

        private static (Texture2D Atlas, Func<int, int> TileIndex, int TilesPerRow) Build(int eraIndex) => eraIndex switch
        {
            <= 1 => (TerrainAtlas.Build(), TerrainAtlas.TileIndexForColor, TerrainAtlas.TileCount),
            2 => (Era2TerrainAtlas.Build(), rgb => Era2TerrainAtlas.TileIndexForTerrain(TerrainKindOfColor(rgb)), Era2TerrainAtlas.TileCount),
            3 => (Era3TerrainAtlas.Build(), rgb => Era3TerrainAtlas.TileIndexForTerrain(TerrainKindOfColor(rgb)), Era3TerrainAtlas.TileCount),
            4 => (Era4TerrainAtlas.Build(), rgb => Era4TerrainAtlas.TileIndexForTerrain(TerrainKindOfColor(rgb)), Era4TerrainAtlas.TileCount),
            5 => (Era5TerrainAtlas.Build(), rgb => Era5TerrainAtlas.TileIndexForTerrain(TerrainKindOfColor(rgb)), Era5TerrainAtlas.TileCount),
            _ => (Era6TerrainAtlas.Build(), rgb => Era6TerrainAtlas.TileIndexForTerrain(TerrainKindOfColor(rgb)), Era6TerrainAtlas.TileCount),
        };
    }

    /// <summary>Framebuffer color → terrain kind (inverse of <see cref="ColorOfTerrain"/>; unknown colors read as Plain).</summary>
    internal static EraCore.World.TerrainKind TerrainKindOfColor(int rgb) => rgb switch
    {
        0x36523C => EraCore.World.TerrainKind.Forest,
        0x2A4430 => EraCore.World.TerrainKind.Rock,
        0x4A6648 => EraCore.World.TerrainKind.Water,
        0x405C42 => EraCore.World.TerrainKind.Wall,
        _ => EraCore.World.TerrainKind.Plain,
    };

    /// <summary>Terrain kind → framebuffer color (SimHost/NetClientViewport 同一映射，读面独立成对).</summary>
    internal static int ColorOfTerrain(EraCore.World.TerrainKind kind) => kind switch
    {
        EraCore.World.TerrainKind.Forest => 0x36523C, // atlas slot 1 (floor-dirt-pebbled)
        EraCore.World.TerrainKind.Rock => 0x2A4430,   // atlas slot 3 (floor-rubble)
        EraCore.World.TerrainKind.Water => 0x4A6648,  // atlas slot 7 (water-pool)
        EraCore.World.TerrainKind.Wall => 0x405C42,   // atlas slot 2 (floor-dirt-mottled)
        _ => 0x2E4A34,                                // atlas slot 0 (floor-dirt) — Plain/Road
    };

    // ---- adapters（会话 sim → 桥的两端口；SimHost/NetClientViewport 同款映射）-

    private sealed class SessionSnapshotSource(Simulation simulation) : ISimSnapshotSource
    {
        public SimSnapshot? Latest => simulation.Snapshots.Latest;
    }

    private sealed class SessionFramebuffer(Simulation simulation) : IFramebufferProvider
    {
        public int WidthCells => simulation.World.Width;

        public int HeightCells => simulation.World.Height;

        public long FramebufferVersion => 1L; // 静态地形（SimHost 同律）：绑定时一次性全量重建；换关=新实例再触发一次

        public int ColorAt(int x, int y)
        {
            if (x < 0 || x >= WidthCells)
            {
                throw new ArgumentException($"cell X {x} outside grid [0,{WidthCells}).", nameof(x));
            }

            if (y < 0 || y >= HeightCells)
            {
                throw new ArgumentException($"cell Y {y} outside grid [0,{HeightCells}).", nameof(y));
            }

            return ColorOfTerrain(simulation.World.TerrainAt(new CellCoord(x, y)));
        }
    }
}
