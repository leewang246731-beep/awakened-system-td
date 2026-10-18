using System.Collections.Generic;
using Godot;

namespace EraTD;

/// <summary>
/// W8 E5 视觉接线 (T-142, C-28/F-042/F-127/F-133): E5 future art pack wiring
/// — era4 同构（30 文件包探针的 E5 对位件）。The belt keeps the T-119
/// dual-layer animation whole: same <c>belt-scroll.gdshader</c>, same frozen
/// geometry, only the palette textures swap to <c>res://textures/era5/belt/</c>
/// — and WHICH palette rides is a pure EraCore decision, <see cref="EraCore.
/// Presentation.BeltRenderDecisions.BeltSkinStem"/> maps the throughput gear
/// (20 件/s = E5 drone rung, v3.2 §5 ladder 第 5 档) onto the skin stem; the
/// Godot side mirrors that table, never re-derives it. Tower/enemy/Boss
/// entity texture mapping follows the same content-id → stem rule
/// <see cref="Era5ArtMappingTests"/> asserts headless over the PNG bytes.
/// Headless smoke (ART5/BELT5 markers) proves the pack loads, the atlas
/// composes, the skin switch selects e5 at 20/s and the shader resource
/// instantiates with the right parameters — GPU pixel output is a
/// windowed-only fact (headless 局限, 41 号 §6).
/// </summary>
internal static class Era5ArtPack
{
    /// <summary>Belt body tile edge in px — mirrors <see cref="EraCore.Presentation.BeltRenderDecisions.BodyTilePx"/> (frozen contract, unchanged by the palette reskin).</summary>
    public const int BeltBodyPx = EraCore.Presentation.BeltRenderDecisions.BodyTilePx;

    /// <summary>Turn animation frames — mirrors BeltRenderDecisions.TurnFrameCount.</summary>
    public const int TurnFrames = EraCore.Presentation.BeltRenderDecisions.TurnFrameCount;

    /// <summary>Turn strip grid: 4 frames per row × 2 rows of 16 px cells (same frozen grid as E3/E4 — 64x32 strip, doc 44 §3).</summary>
    public const int TurnFramesPerRow = 4;

    /// <summary>
    /// The E5 skin stem the belt gear decision selects (frozen fact for path
    /// composition below). T-134 left the upper rung riding the newest
    /// delivered skin (era4); the T-142 pack grows the table one rung so the
    /// 20/s drone gear now wears the <c>era5</c> palette.
    /// </summary>
    public static string BeltSkinStem =>
        EraCore.Presentation.BeltRenderDecisions.BeltSkinStem(
            EraCore.Presentation.BeltRenderDecisions.Era5GearItemsPerSecond);

    /// <summary>Smoke-facing short alias of <see cref="BeltSkinStem"/> ("e5" ← "era5").</summary>
    public static string BeltSkinShortStem => "e" + BeltSkinStem["era".Length..];

    /// <summary>Boss sprite stem — the Dominator (era.json boss 块 name_en=Dominator 主宰, T-134).</summary>
    public const string BossStem = "boss-dominator";

    /// <summary>Boss canvas edge in px — the 64~96 band lower bound ruling of 28 号 §5 (content canvas_size_px=96 exceeds the SIZE-ALLOWLIST).</summary>
    public const int BossCanvasPx = 64;

    /// <summary>Content-id prefix every E5 tower/enemy row carries (era5:tower-railgun …).</summary>
    private const string ContentPrefix = "era5:";

    /// <summary>
    /// Straight-segment belt material: loads belt-scroll.gdshader, binds the
    /// E5 dual-layer textures (skin stem from the pure gear decision) and the
    /// frozen tile-size constant. The per-frame scroll value is NOT baked
    /// here — the render loop sets scroll_px each frame from
    /// BeltRenderDecisions.ShaderOffsetPx (one uniform per layer, per-instance
    /// direction rides in INSTANCE_CUSTOM.y; same contract as E3/E4).
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
    /// same frozen geometry as E3/E4; the E5 strip only swaps the palette).
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

    /// <summary>Loads the static corner-edge overlay (belt-turn-edge.png) — the second layer of the turn split, E5 palette.</summary>
    public static Texture2D LoadTurnEdge()
    {
        var edge = ResourceLoader.Load<Texture2D>($"res://textures/{BeltSkinStem}/belt/belt-turn-edge.png");
        return edge ?? throw new InvalidOperationException(
            $"belt turn edge missing: res://textures/{BeltSkinStem}/belt/belt-turn-edge.png");
    }

    /// <summary>
    /// Loads the 8-frame turn strip (belt-turn-anim.png) and asserts the
    /// frozen grid geometry (64x32 = 8 frames × 16 px) — the same contract
    /// Era5ArtMappingTests asserts headless over the PNG bytes.
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
    /// <c>("era5:tower-railgun", 2) → res://textures/era5/towers/tower-railgun-l2.png</c>.
    /// Level filenames follow the towers.json tier/max_tier rule (44 号 §2.1);
    /// max-1 towers have no l2 file by design (mapping-test sentinel).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="towerContentId"/> is null.</exception>
    /// <exception cref="ArgumentException">id lacks the era5: prefix, or level &lt; 1.</exception>
    public static string TowerSpritePath(string towerContentId, int level)
    {
        ArgumentNullException.ThrowIfNull(towerContentId);
        string stem = EntityStem(towerContentId, "tower");
        if (level < 1)
        {
            throw new ArgumentException("tower sprite level starts at 1.", nameof(level));
        }

        return $"res://textures/era5/towers/{stem}-l{level}.png";
    }

    /// <summary>
    /// Enemy content id → sprite path: <c>"era5:enemy-nano-swarm" →
    /// res://textures/era5/enemies/enemy-nano-swarm.png"</c>. The Boss row
    /// (<c>era5:enemy-boss</c>) carries its own stem <see cref="BossStem"/>
    /// (dominator) and canvas <see cref="BossCanvasPx"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="enemyContentId"/> is null.</exception>
    /// <exception cref="ArgumentException">id lacks the era5: prefix or is empty after it.</exception>
    public static string EnemySpritePath(string enemyContentId)
    {
        ArgumentNullException.ThrowIfNull(enemyContentId);
        if (enemyContentId == ContentPrefix + "enemy-boss")
        {
            return $"res://textures/era5/enemies/{BossStem}.png";
        }

        return $"res://textures/era5/enemies/{EntityStem(enemyContentId, "enemy")}.png";
    }

    /// <summary>Strips the era5: prefix and validates the stem carries the expected kind.</summary>
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
    /// Existence probe over the FULL 30-file E5 pack (era5 content-id-aligned
    /// manifest: tiles 9 + belt 3 + anim 2 + towers 5 + enemies 9 + units 1 +
    /// ui 1 — anim 2=belt-turn 8 帧+力场塔 4 相【T-157 ⑥】；era4 的 30 文件
    /// 同构异数来自三塔 5 升级位 vs 四塔 6 件，槽类结构一致，REV 登记) —
    /// the atlas Build() above is the throwing load proof for the tile set.
    /// Returns the files that are present so the smoke marker can count
    /// them; a missing file is reported (not thrown) because these are art
    /// assets.
    /// </summary>
    public static List<string> ProbePackFiles()
    {
        string[] files =
        {
            // tiles (9)
            "res://textures/era5/tiles/floor-nano.png",
            "res://textures/era5/tiles/floor-nano-lattice.png",
            "res://textures/era5/tiles/road-flux.png",
            "res://textures/era5/tiles/road-flux-alt.png",
            "res://textures/era5/tiles/wall-barrier.png",
            "res://textures/era5/tiles/wall-barrier-alt.png",
            "res://textures/era5/tiles/floor-rubble-nano.png",
            "res://textures/era5/tiles/floor-rubble-nano-alt.png",
            "res://textures/era5/tiles/water-coolant.png",
            // belt trio + turn strip (4)
            "res://textures/era5/belt/belt-body.png",
            "res://textures/era5/belt/belt-edge.png",
            "res://textures/era5/belt/belt-turn-edge.png",
            "res://textures/era5/anim/belt-turn-anim.png",
            "res://textures/era5/anim/tower-force-field-anim.png", // T-157 ⑥：力场塔 4 相脉冲呼吸
            // towers (5 — three towers by era5/towers.json content ids)
            "res://textures/era5/towers/tower-railgun-l1.png",
            "res://textures/era5/towers/tower-railgun-l2.png",
            "res://textures/era5/towers/tower-drone-nest-l1.png",
            "res://textures/era5/towers/tower-drone-nest-l2.png",
            "res://textures/era5/towers/tower-force-field-l1.png",
            // enemies + boss (9)
            "res://textures/era5/enemies/enemy-nano-swarm.png",
            "res://textures/era5/enemies/enemy-combat-frame.png",
            "res://textures/era5/enemies/enemy-siege-titan.png",
            "res://textures/era5/enemies/enemy-hover-striker.png",
            "res://textures/era5/enemies/enemy-lancer-drone.png",
            "res://textures/era5/enemies/enemy-barrier-mech.png",
            "res://textures/era5/enemies/enemy-melt-drone.png",
            "res://textures/era5/enemies/enemy-nexus-core.png",
            $"res://textures/era5/enemies/{BossStem}.png",
            // units + ui (2)
            "res://textures/era5/units/drone-right.png",
            "res://textures/era5/ui/panel-era5.png",
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
