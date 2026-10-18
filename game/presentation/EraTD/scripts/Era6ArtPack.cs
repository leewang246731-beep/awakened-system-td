using System.Collections.Generic;
using Godot;

namespace EraTD;

/// <summary>
/// W8 终章视觉接线 (T-142, C-28/F-042/F-127/F-133): final-chapter deep-space
/// art pack wiring — era4/era5 同构（全包探针的终章对位件）. The belt keeps
/// the T-119 dual-layer animation whole: same <c>belt-scroll.gdshader</c>,
/// same frozen geometry, only the palette textures swap to
/// <c>res://textures/era6/belt/</c> — and WHICH palette rides is a pure
/// EraCore decision, <see cref="EraCore.Presentation.BeltRenderDecisions.
/// BeltSkinStem"/> maps the throughput gear (the ladder's ∞ rung = 终章
/// teleporter, v3.2 §5 L78 末位) onto the skin stem; the Godot side mirrors
/// that table, never re-derives it. Tower/enemy/Boss entity texture mapping
/// follows the same content-id → stem rule <see cref="Era6ArtMappingTests"/>
/// asserts headless over the PNG bytes. Headless smoke (ART6/BELT6 markers)
/// proves the pack loads, the atlas composes, the skin switch selects era6
/// at the ∞ gear and the shader resource instantiates with the right
/// parameters — GPU pixel output is a windowed-only fact (headless 局限,
/// 41 号 §6).
/// </summary>
internal static class Era6ArtPack
{
    /// <summary>Belt body tile edge in px — mirrors <see cref="EraCore.Presentation.BeltRenderDecisions.BodyTilePx"/> (frozen contract, unchanged by the palette reskin).</summary>
    public const int BeltBodyPx = EraCore.Presentation.BeltRenderDecisions.BodyTilePx;

    /// <summary>Turn animation frames — mirrors BeltRenderDecisions.TurnFrameCount.</summary>
    public const int TurnFrames = EraCore.Presentation.BeltRenderDecisions.TurnFrameCount;

    /// <summary>Turn strip grid: 4 frames per row × 2 rows of 16 px cells (same frozen grid as E3~E5 — 64x32 strip, doc 44 §3).</summary>
    public const int TurnFramesPerRow = 4;

    /// <summary>
    /// The era6 skin stem the belt gear decision selects (frozen fact for
    /// path composition below): the ladder's ∞ rung wears the deep-space
    /// palette（era6/era.json logistics throughput=null → 决策层正无穷）.
    /// </summary>
    public static string BeltSkinStem =>
        EraCore.Presentation.BeltRenderDecisions.BeltSkinStem(
            EraCore.Presentation.BeltRenderDecisions.Era6GearItemsPerSecond);

    /// <summary>Smoke-facing short alias of <see cref="BeltSkinStem"/> ("e6" ← "era6").</summary>
    public static string BeltSkinShortStem => "e" + BeltSkinStem["era".Length..];

    /// <summary>Boss sprite stem — the Fallen Watcher (era.json boss 块 name_en=Fallen Watcher 堕落守望者, T-135).</summary>
    public const string BossStem = "boss-fallen-watcher";

    /// <summary>
    /// Boss canvas width in px — 设计案 §13.1「终章 Boss 96×112」的宽 96
    /// (=28 号 §5 常规 Boss 64~96 带上沿)。T-136 升格落地：内容表
    /// canvas_size_px=96 + 校验器 SIZE-ALLOWLIST 扩 96/112（REV-20261018-T136
    /// 处置 T-142 挂账 DEBT①）。
    /// </summary>
    public const int BossCanvasPx = 96;

    /// <summary>
    /// Boss canvas height in px — 设计案 §13.1「终章 Boss 96×112」的高 112
    /// （终章专属纵幅：冠环-舰体-脉冲弧三段拉满）。与
    /// <see cref="BossCanvasPx"/> 成对，对齐内容表 canvas_height_px=112。
    /// </summary>
    public const int BossCanvasHeightPx = 112;

    /// <summary>Content-id prefix every era6 tower/enemy row carries (era6:tower-singularity …).</summary>
    private const string ContentPrefix = "era6:";

    /// <summary>
    /// Straight-segment belt material: loads belt-scroll.gdshader, binds the
    /// era6 dual-layer textures (skin stem from the pure gear decision) and
    /// the frozen tile-size constant. The per-frame scroll value is NOT baked
    /// here — the render loop sets scroll_px each frame from
    /// BeltRenderDecisions.ShaderOffsetPx (one uniform per layer, per-instance
    /// direction rides in INSTANCE_CUSTOM.y; same contract as E3~E5).
    /// </summary>
    public static ShaderMaterial BuildBeltScrollMaterial()
    {
        var shader = ResourceLoader.Load<Shader>("res://shaders/belt-scroll.gdshader");
        if (shader is null)
        {
            throw new InvalidOperationException("belt shader missing: res://shaders/belt-scroll.gdshader");
        }

        string stem = BeltSkinStem;
        var body = ResourceLoader.Load<Texture2D>($"res://textures/{stem}/belt/belt-body.png");
        var edge = ResourceLoader.Load<Texture2D>($"res://textures/{stem}/belt/belt-edge.png");
        if (body is null || edge is null)
        {
            throw new InvalidOperationException(
                $"belt dual-layer textures missing under res://textures/{stem}/belt/");
        }

        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("body", body);
        material.SetShaderParameter("edge", edge);
        material.SetShaderParameter("scroll_px", 0f);
        return material;
    }

    /// <summary>
    /// Pixel rect of turn animation frame <paramref name="frame"/> (0..7)
    /// inside the 64x32 belt-turn-anim strip (row-major grid, 4 per row —
    /// same frozen geometry as E3~E5; the era6 strip only swaps the palette).
    /// </summary>
    public static Rect2I TurnFrameRect(int frame)
    {
        int index = frame % TurnFrames;
        return new Rect2I(
            (index % TurnFramesPerRow) * BeltBodyPx,
            (index / TurnFramesPerRow) * BeltBodyPx,
            BeltBodyPx,
            BeltBodyPx);
    }

    /// <summary>Loads the static corner-edge overlay (belt-turn-edge.png) — the second layer of the turn split, era6 palette.</summary>
    public static Texture2D LoadTurnEdge()
    {
        var edge = ResourceLoader.Load<Texture2D>($"res://textures/{BeltSkinStem}/belt/belt-turn-edge.png");
        return edge ?? throw new InvalidOperationException(
            $"belt turn edge missing: res://textures/{BeltSkinStem}/belt/belt-turn-edge.png");
    }

    /// <summary>
    /// Loads the 8-frame turn strip (belt-turn-anim.png) and asserts the
    /// frozen grid geometry (64x32 = 8 frames × 16 px) — the same contract
    /// Era6ArtMappingTests asserts headless over the PNG bytes.
    /// </summary>
    public static Texture2D LoadTurnStrip()
    {
        var strip = ResourceLoader.Load<Texture2D>($"res://textures/{BeltSkinStem}/anim/belt-turn-anim.png");
        if (strip is null)
        {
            throw new InvalidOperationException(
                $"belt turn strip missing: res://textures/{BeltSkinStem}/anim/belt-turn-anim.png");
        }

        return strip;
    }

    /// <summary>
    /// Tower content id + tier → sprite path:
    /// <c>("era6:tower-spacetime", 1) → res://textures/era6/towers/tower-spacetime-l1.png</c>.
    /// Level filenames follow the towers.json tier/max_tier rule (44 号 §2.1);
    /// 终章两塔均 max_tier=1（无升级位文件）.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="towerContentId"/> is null.</exception>
    /// <exception cref="ArgumentException">id lacks the era6: prefix, or level &lt; 1.</exception>
    public static string TowerSpritePath(string towerContentId, int level)
    {
        ArgumentNullException.ThrowIfNull(towerContentId);
        string stem = EntityStem(towerContentId, "tower");
        if (level < 1)
        {
            throw new ArgumentException("tower sprite level starts at 1.", nameof(level));
        }

        return $"res://textures/era6/towers/{stem}-l{level}.png";
    }

    /// <summary>
    /// Enemy content id → sprite path: <c>"era6:enemy-void-swarm" →
    /// res://textures/era6/enemies/enemy-void-swarm.png"</c>. The Boss row
    /// (<c>era6:enemy-boss</c>) carries its own stem <see cref="BossStem"/>
    /// (fallen watcher) and canvas <see cref="BossCanvasPx"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="enemyContentId"/> is null.</exception>
    /// <exception cref="ArgumentException">id lacks the era6: prefix or is empty after it.</exception>
    public static string EnemySpritePath(string enemyContentId)
    {
        ArgumentNullException.ThrowIfNull(enemyContentId);
        if (enemyContentId == ContentPrefix + "enemy-boss")
        {
            return $"res://textures/era6/enemies/{BossStem}.png";
        }

        return $"res://textures/era6/enemies/{EntityStem(enemyContentId, "enemy")}.png";
    }

    /// <summary>Strips the era6: prefix and validates the stem carries the expected kind.</summary>
    private static string EntityStem(string contentId, string kind)
    {
        if (!contentId.StartsWith(ContentPrefix, System.StringComparison.Ordinal)
            || contentId.Length <= ContentPrefix.Length)
        {
            throw new ArgumentException(
                $"content id must look like '{ContentPrefix}{kind}-…' (got '{contentId}').", nameof(contentId));
        }

        return contentId[ContentPrefix.Length..];
    }

    /// <summary>
    /// Existence probe over the FULL 27-file era6 pack (era6 content-id-aligned
    /// manifest: tiles 9 + belt 3 + anim 1 + towers 2 + enemies 9 + units 1 +
    /// ui 1 — 终章两塔均 max_tier=1 ⇒ 2 升级位文件，era4 30/era5 29 的同构
    /// 异数，槽类结构一致，REV 登记) — the atlas Build() above is the
    /// throwing load proof for the tile set. Returns the files that are
    /// present so the smoke marker can count them; a missing file is
    /// reported (not thrown) because these are art assets.
    /// </summary>
    public static List<string> ProbePackFiles()
    {
        string[] files =
        {
            // tiles (9)
            "res://textures/era6/tiles/floor-voidstone.png",
            "res://textures/era6/tiles/floor-nebula.png",
            "res://textures/era6/tiles/road-lightray.png",
            "res://textures/era6/tiles/road-lightray-alt.png",
            "res://textures/era6/tiles/wall-stargate.png",
            "res://textures/era6/tiles/wall-stargate-alt.png",
            "res://textures/era6/tiles/floor-rubble-void.png",
            "res://textures/era6/tiles/floor-rubble-void-alt.png",
            "res://textures/era6/tiles/water-voidsea.png",
            // belt trio + turn strip (4)
            "res://textures/era6/belt/belt-body.png",
            "res://textures/era6/belt/belt-edge.png",
            "res://textures/era6/belt/belt-turn-edge.png",
            "res://textures/era6/anim/belt-turn-anim.png",
            "res://textures/era6/anim/tower-spacetime-anim.png", // T-157 ⑥：时空塔 4 相时间涟漪
            // towers (2 — both era6 towers are max_tier=1 by content ids)
            "res://textures/era6/towers/tower-singularity-l1.png",
            "res://textures/era6/towers/tower-spacetime-l1.png",
            // enemies + boss (9)
            "res://textures/era6/enemies/enemy-void-swarm.png",
            "res://textures/era6/enemies/enemy-null-frame.png",
            "res://textures/era6/enemies/enemy-siege-colossus.png",
            "res://textures/era6/enemies/enemy-phase-striker.png",
            "res://textures/era6/enemies/enemy-lancer-array.png",
            "res://textures/era6/enemies/enemy-aegis-paladin.png",
            "res://textures/era6/enemies/enemy-entropy-wraith.png",
            "res://textures/era6/enemies/enemy-choir-core.png",
            $"res://textures/era6/enemies/{BossStem}.png",
            // units + ui (2)
            "res://textures/era6/units/teleporter-right.png",
            "res://textures/era6/ui/panel-era6.png",
        };

        var present = new List<string>();
        foreach (string path in files)
        {
            // ResourceLoader.Exists 走导入 remap——导出件里源 png 不在 pck
            // （只有 .godot/imported 的 .ctex+.remap），FileAccess.FileExists
            // 对源路径恒 false（v1.0 出包实测 ART4 0/30 假阴性先例，d28b1b6）。
            if (ResourceLoader.Exists(path))
            {
                present.Add(path);
            }
        }

        return present;
    }
}
