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
        _unitLayer = new MultiMeshRenderer { Name = "SessionUnitLayer", ZIndex = UnitZIndex };
        AddChild(_unitLayer);
        _groundLayer.Hide();
        _unitLayer.Hide();
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

        _framesDrawn++;
    }

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
