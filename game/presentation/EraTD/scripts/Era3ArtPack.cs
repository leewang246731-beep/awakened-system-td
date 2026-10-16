using System.Collections.Generic;
using Godot;

namespace EraTD;

/// <summary>
/// T-119 (C-28/F-127/F-133): E3 industrial art pack wiring — the Godot half
/// of the belt dual-layer animation. All texture facts come from the frozen
/// EraCore decision table (<see cref="EraCore.Presentation.BeltRenderDecisions"/>):
/// the straight-segment material below feeds its phase output straight into
/// the belt-scroll.gdshader scroll_px uniform (animated belt body + static
/// riveted rails in one draw), and turn cells address the 8-frame strip by
/// <see cref="TurnFrameRect"/> (frame = BeltRenderDecisions.FrameIndexFor)
/// with the static corner-edge overlay on top. Headless smoke (ART3/BELT
/// markers) proves the pack loads, the atlas composes and the shader
/// resource instantiates with the right parameters — GPU pixel output is a
/// windowed-only fact (headless limitation, 41 号 §6).
/// </summary>
internal static class Era3ArtPack
{
    /// <summary>Belt body tile edge in px — mirrors <see cref="EraCore.Presentation.BeltRenderDecisions.BodyTilePx"/> (frozen contract).</summary>
    public const int BeltBodyPx = EraCore.Presentation.BeltRenderDecisions.BodyTilePx;

    /// <summary>Turn animation frames — mirrors BeltRenderDecisions.TurnFrameCount.</summary>
    public const int TurnFrames = EraCore.Presentation.BeltRenderDecisions.TurnFrameCount;

    /// <summary>Turn strip grid: 4 frames per row × 2 rows of 16 px cells (single-row 8-frame strip would be 128 px wide — over the asset_validator size allowlist).</summary>
    public const int TurnFramesPerRow = 4;

    /// <summary>
    /// Straight-segment belt material: loads belt-scroll.gdshader, binds the
    /// dual-layer textures and the frozen tile-size constant. The per-frame
    /// scroll value is NOT baked here — the render loop sets scroll_px each
    /// frame from BeltRenderDecisions.ShaderOffsetPx (one uniform per layer,
    /// per-instance direction rides in INSTANCE_CUSTOM.y).
    /// </summary>
    public static ShaderMaterial BuildBeltScrollMaterial()
    {
        var shader = ResourceLoader.Load<Shader>("res://shaders/belt-scroll.gdshader");
        if (shader is null)
        {
            throw new InvalidOperationException("belt shader missing: res://shaders/belt-scroll.gdshader");
        }

        var body = ResourceLoader.Load<Texture2D>("res://textures/era3/belt/belt-body.png");
        var edge = ResourceLoader.Load<Texture2D>("res://textures/era3/belt/belt-edge.png");
        if (body is null || edge is null)
        {
            throw new InvalidOperationException(
                "belt dual-layer textures missing under res://textures/era3/belt/");
        }

        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("body", body);
        material.SetShaderParameter("edge", edge);
        material.SetShaderParameter("scroll_px", 0f);
        return material;
    }

    /// <summary>
    /// Pixel rect of turn animation frame <paramref name="frame"/> (0..7)
    /// inside the 64x32 belt-turn-anim strip (row-major grid, 4 per row).
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

    /// <summary>Loads the static corner-edge overlay (belt-turn-edge.png) — the second layer of the turn split.</summary>
    public static Texture2D LoadTurnEdge()
    {
        var edge = ResourceLoader.Load<Texture2D>("res://textures/era3/belt/belt-turn-edge.png");
        return edge ?? throw new InvalidOperationException(
            "belt turn edge missing: res://textures/era3/belt/belt-turn-edge.png");
    }

    /// <summary>
    /// Loads the 8-frame turn strip (belt-turn-anim.png) and asserts the
    /// frozen grid geometry (64x32 = 8 frames × 16 px) — the same contract
    /// Era3ArtMappingTests asserts headless over the PNG bytes.
    /// </summary>
    public static Texture2D LoadTurnStrip()
    {
        var strip = ResourceLoader.Load<Texture2D>("res://textures/era3/anim/belt-turn-anim.png");
        if (strip is null)
        {
            throw new InvalidOperationException(
                "belt turn strip missing: res://textures/era3/anim/belt-turn-anim.png");
        }

        return strip;
    }

    /// <summary>
    /// Existence probe over the non-terrain E3 pack files (towers/enemies/
    /// units/ui/structures + the belt trio). Returns the files that are
    /// present so the smoke marker can count them; a missing file is
    /// reported (not thrown) because these are art assets — the atlas
    /// Build() above is the throwing load proof for the tile set.
    /// </summary>
    public static List<string> ProbePackFiles()
    {
        string[] files =
        {
            "res://textures/era3/towers/tower-gatling-l1.png",
            "res://textures/era3/towers/tower-gatling-l2.png",
            "res://textures/era3/towers/tower-mortar-l1.png",
            "res://textures/era3/towers/tower-mortar-l2.png",
            "res://textures/era3/towers/tower-ironclad-wall-l1.png",
            "res://textures/era3/enemies/enemy-conscript.png",
            "res://textures/era3/enemies/enemy-line-infantry.png",
            "res://textures/era3/enemies/enemy-ram.png",
            "res://textures/era3/enemies/enemy-scout-rider.png",
            "res://textures/era3/enemies/enemy-gunner.png",
            "res://textures/era3/enemies/enemy-ironclad-guard.png",
            "res://textures/era3/enemies/enemy-sapper.png",
            "res://textures/era3/enemies/enemy-overseer.png",
            "res://textures/era3/enemies/boss-bastion.png",
            "res://textures/era3/units/mine-rail-right.png",
            "res://textures/era3/units/truck-right.png",
            "res://textures/era3/ui/panel-era3.png",
            "res://textures/era3/structures/chimney.png",
            "res://textures/era3/belt/belt-body.png",
            "res://textures/era3/belt/belt-edge.png",
            "res://textures/era3/belt/belt-turn-edge.png",
            "res://textures/era3/anim/belt-turn-anim.png",
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
