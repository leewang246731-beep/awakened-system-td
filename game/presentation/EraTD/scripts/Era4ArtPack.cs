using System.Collections.Generic;
using Godot;

namespace EraTD;

/// <summary>
/// W7 E4 视觉接线 (T-128, C-28/F-042/F-127/F-133): E4 modern art pack wiring —
/// the presentation half 44 号 §1 explicitly deferred out of the T-128
/// art-pack card. The belt keeps the T-119 dual-layer animation whole: same
/// <c>belt-scroll.gdshader</c>, same frozen geometry, only the palette
/// textures swap to <c>res://textures/era4/belt/</c> — and WHICH palette
/// rides is a pure EraCore decision, <see cref="EraCore.Presentation.
/// BeltRenderDecisions.BeltSkinStem"/> maps the throughput gear (15 件/s =
/// E4 conveyor-belt rung, v3.2 §5 ladder) onto the skin stem; the Godot
/// side mirrors that table, never re-derives it. Tower/enemy/Boss entity
/// texture mapping follows the same content-id → stem rule
/// <see cref="Era4ArtMappingTests"/> asserts headless over the PNG bytes.
/// Headless smoke (ART4/BELT4 markers) proves the pack loads, the atlas
/// composes, the skin switch selects e4 at 15/s and the shader resource
/// instantiates with the right parameters — GPU pixel output is a
/// windowed-only fact (headless 局限, 41 号 §6).
/// </summary>
internal static class Era4ArtPack
{
    /// <summary>Belt body tile edge in px — mirrors <see cref="EraCore.Presentation.BeltRenderDecisions.BodyTilePx"/> (frozen contract, unchanged by the palette reskin).</summary>
    public const int BeltBodyPx = EraCore.Presentation.BeltRenderDecisions.BodyTilePx;

    /// <summary>Turn animation frames — mirrors BeltRenderDecisions.TurnFrameCount.</summary>
    public const int TurnFrames = EraCore.Presentation.BeltRenderDecisions.TurnFrameCount;

    /// <summary>Turn strip grid: 4 frames per row × 2 rows of 16 px cells (same frozen grid as E3 — 64x32 strip, doc 44 §3).</summary>
    public const int TurnFramesPerRow = 4;

    /// <summary>The E4 skin stem the belt gear decision selects (frozen fact for path composition below).</summary>
    public static string BeltSkinStem =>
        EraCore.Presentation.BeltRenderDecisions.BeltSkinStem(
            EraCore.Presentation.BeltRenderDecisions.Era4GearItemsPerSecond);

    /// <summary>Smoke-facing short alias of <see cref="BeltSkinStem"/> ("e4" ← "era4").</summary>
    public static string BeltSkinShortStem => "e" + BeltSkinStem["era".Length..];

    /// <summary>Boss sprite stem — the Night Owl (era.json boss 块 name_en=Night Owl, 44 号 §2.2).</summary>
    public const string BossStem = "boss-nightowl";

    /// <summary>Boss canvas edge in px — the 64~96 band lower bound ruling of 44 号 §2.2 (content canvas_size_px=96 exceeds the SIZE-ALLOWLIST).</summary>
    public const int BossCanvasPx = 64;

    /// <summary>Content-id prefix every E4 tower/enemy row carries (era4:tower-missile …).</summary>
    private const string ContentPrefix = "era4:";

    /// <summary>
    /// Straight-segment belt material: loads belt-scroll.gdshader, binds the
    /// E4 dual-layer textures (skin stem from the pure gear decision) and the
    /// frozen tile-size constant. The per-frame scroll value is NOT baked
    /// here — the render loop sets scroll_px each frame from
    /// BeltRenderDecisions.ShaderOffsetPx (one uniform per layer, per-instance
    /// direction rides in INSTANCE_CUSTOM.y; same contract as E3).
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
    /// same frozen geometry as E3; the E4 strip only swaps the palette).
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

    /// <summary>Loads the static corner-edge overlay (belt-turn-edge.png) — the second layer of the turn split, E4 palette.</summary>
    public static Texture2D LoadTurnEdge()
    {
        var edge = ResourceLoader.Load<Texture2D>($"res://textures/{BeltSkinStem}/belt/belt-turn-edge.png");
        return edge ?? throw new InvalidOperationException(
            $"belt turn edge missing: res://textures/{BeltSkinStem}/belt/belt-turn-edge.png");
    }

    /// <summary>
    /// Loads the 8-frame turn strip (belt-turn-anim.png) and asserts the
    /// frozen grid geometry (64x32 = 8 frames × 16 px) — the same contract
    /// Era4ArtMappingTests asserts headless over the PNG bytes.
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
    /// <c>("era4:tower-missile", 2) → res://textures/era4/towers/tower-missile-l2.png</c>.
    /// Level filenames follow the towers.json tier/max_tier rule (44 号 §2.1);
    /// max-1 towers have no l2 file by design (mapping-test sentinel).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="towerContentId"/> is null.</exception>
    /// <exception cref="ArgumentException">id lacks the era4: prefix, or level &lt; 1.</exception>
    public static string TowerSpritePath(string towerContentId, int level)
    {
        ArgumentNullException.ThrowIfNull(towerContentId);
        string stem = EntityStem(towerContentId, "tower");
        if (level < 1)
        {
            throw new ArgumentException("tower sprite level starts at 1.", nameof(level));
        }

        return $"res://textures/era4/towers/{stem}-l{level}.png";
    }

    /// <summary>
    /// Enemy content id → sprite path: <c>"era4:enemy-militia" →
    /// res://textures/era4/enemies/enemy-militia.png"</c>. The Boss row
    /// (<c>era4:enemy-boss</c>) carries its own stem <see cref="BossStem"/>
    /// (night owl) and canvas <see cref="BossCanvasPx"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="enemyContentId"/> is null.</exception>
    /// <exception cref="ArgumentException">id lacks the era4: prefix or is empty after it.</exception>
    public static string EnemySpritePath(string enemyContentId)
    {
        ArgumentNullException.ThrowIfNull(enemyContentId);
        if (enemyContentId == ContentPrefix + "enemy-boss")
        {
            return $"res://textures/era4/enemies/{BossStem}.png";
        }

        return $"res://textures/era4/enemies/{EntityStem(enemyContentId, "enemy")}.png";
    }

    /// <summary>Strips the era4: prefix and validates the stem carries the expected kind.</summary>
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
    /// Existence probe over the FULL 30-file E4 pack (doc 44 §5 manifest:
    /// tiles 9 + belt 3 + anim 1 + towers 6 + enemies 9 + units 1 + ui 1 —
    /// the atlas Build() above is the throwing load proof for the tile
    /// set). Returns the files that are present so the smoke marker can
    /// count them; a missing file is reported (not thrown) because these
    /// are art assets.
    /// </summary>
    public static List<string> ProbePackFiles()
    {
        string[] files =
        {
            // tiles (9)
            "res://textures/era4/tiles/floor-concrete.png",
            "res://textures/era4/tiles/floor-concrete-grated.png",
            "res://textures/era4/tiles/road-asphalt.png",
            "res://textures/era4/tiles/road-asphalt-alt.png",
            "res://textures/era4/tiles/wall-glass-curtain.png",
            "res://textures/era4/tiles/wall-glass-curtain-alt.png",
            "res://textures/era4/tiles/floor-rubble-concrete.png",
            "res://textures/era4/tiles/floor-rubble-concrete-alt.png",
            "res://textures/era4/tiles/water-modern.png",
            // belt trio + turn strip (4)
            "res://textures/era4/belt/belt-body.png",
            "res://textures/era4/belt/belt-edge.png",
            "res://textures/era4/belt/belt-turn-edge.png",
            "res://textures/era4/anim/belt-turn-anim.png",
            // towers (6)
            "res://textures/era4/towers/tower-missile-l1.png",
            "res://textures/era4/towers/tower-missile-l2.png",
            "res://textures/era4/towers/tower-laser-l1.png",
            "res://textures/era4/towers/tower-laser-l2.png",
            "res://textures/era4/towers/tower-radar-l1.png",
            "res://textures/era4/towers/tower-mine-l1.png",
            // enemies + boss (9)
            "res://textures/era4/enemies/enemy-militia.png",
            "res://textures/era4/enemies/enemy-assault-trooper.png",
            "res://textures/era4/enemies/enemy-siege-mech.png",
            "res://textures/era4/enemies/enemy-raid-quad.png",
            "res://textures/era4/enemies/enemy-missile-trooper.png",
            "res://textures/era4/enemies/enemy-riot-armor.png",
            "res://textures/era4/enemies/enemy-kamikaze-drone.png",
            "res://textures/era4/enemies/enemy-ew-officer.png",
            $"res://textures/era4/enemies/{BossStem}.png",
            // units + ui (2)
            "res://textures/era4/units/truck-right.png",
            "res://textures/era4/ui/panel-era4.png",
        };

        var present = new List<string>();
        foreach (string path in files)
        {
            if (Godot.FileAccess.FileExists(path))
            {
                present.Add(path);
            }
        }

        return present;
    }
}
