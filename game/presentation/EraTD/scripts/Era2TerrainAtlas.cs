using System.Collections.Generic;
using Godot;

namespace EraTD;

/// <summary>
/// T-078 (C-28): E2 classical terrain atlas wiring. Same composition scheme
/// as the E1 <see cref="TerrainAtlas"/> (one horizontal strip, nearest-filter
/// sampling, 16 px source tiles scaled 2x in the shader) but sourcing the
/// validator-gated E2 set from <c>res://textures/era2/tiles/</c>:
/// grass/stone-road/wall-brick/farmland from Kenney Tiny Town (CC0,
/// 14-art-license-audit intake batch — see doc 24) and the water tile from
/// the deterministic procedural generator <c>tools/art/era2_placeholders.py</c>
/// (Tiny Town ships no water tile; Medieval RTS water tiles exceed the
/// 64-color master palette cap and were rejected by the validator).
/// The E2 ground layer is not switched into the render loop here (era
/// routing lands with the E2 level work); this atlas exists so the pack
/// loads and composes in-engine today — the smoke marker ART2 proves it.
/// </summary>
internal static class Era2TerrainAtlas
{
    /// <summary>Source tile edge in px (Kenney Tiny Town tile size).</summary>
    public const int TilePx = 16;

    /// <summary>
    /// Atlas cell order. The index in this array IS the atlas slot the
    /// shader computes from INSTANCE_CUSTOM.x.
    /// </summary>
    private static readonly string[] TileFiles =
    {
        "grass",                // slot 0: Tiny Town tile_0000 — open grassland (Plain)
        "grass-spotted",        // slot 1: tile_0001 — flower-speckled variant (Forest stand-in)
        "road-stone",           // slot 2: tile_0041 — cobbled road with curb (Road)
        "road-stone-alt",       // slot 3: tile_0096 — cut-stone plaza (Rock stand-in)
        "wall-stone-brick",     // slot 4: tile_0048 — crenellated stone wall (Wall)
        "wall-stone-brick-alt", // slot 5: tile_0049 — wall variant
        "farm-plowed",          // slot 6: tile_0012 — plowed field rows
        "farm-plowed-alt",      // slot 7: tile_0013 — plowed variant
        "water",                // slot 8: procedural water (Water)
    };

    /// <summary>Number of atlas slots.</summary>
    public static int TileCount => TileFiles.Length;

    /// <summary>Builds the 1-row atlas texture (TileCount*16 x 16, RGBA8).</summary>
    public static ImageTexture Build()
    {
        var atlas = Image.CreateEmpty(TileCount * TilePx, TilePx, false, Image.Format.Rgba8);
        for (int slot = 0; slot < TileFiles.Length; slot++)
        {
            var texture = ResourceLoader.Load<Texture2D>($"res://textures/era2/tiles/{TileFiles[slot]}.png");
            if (texture is null)
            {
                throw new InvalidOperationException(
                    $"era2 terrain tile missing: res://textures/era2/tiles/{TileFiles[slot]}.png");
            }

            Image tile = texture.GetImage();
            if (tile.IsCompressed())
            {
                tile.Decompress();
            }

            if (tile.GetFormat() != Image.Format.Rgba8)
            {
                tile.Convert(Image.Format.Rgba8);
            }

            if (tile.GetWidth() != TilePx || tile.GetHeight() != TilePx)
            {
                throw new InvalidOperationException(
                    $"era2 terrain tile {TileFiles[slot]} is {tile.GetWidth()}x{tile.GetHeight()}, " +
                    $"expected {TilePx}x{TilePx} (asset_validator SIZE-ALLOWLIST)");
            }

            atlas.BlitRect(tile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(slot * TilePx, 0));
        }

        return ImageTexture.CreateFromImage(atlas);
    }

    /// <summary>
    /// <see cref="EraCore.World.TerrainKind"/> → atlas slot for the E2 palette.
    /// Plain maps to grass (the classical open field, vs E1's dirt floor);
    /// Forest reuses the speckled variant as its stand-in until a dedicated
    /// E2 grove tile is drawn; Rock reuses the cut-stone plaza. Unknown kinds
    /// fall back to grass (slot 0).
    /// </summary>
    public static int TileIndexForTerrain(EraCore.World.TerrainKind kind) => kind switch
    {
        EraCore.World.TerrainKind.Plain => 0,
        EraCore.World.TerrainKind.Forest => 1,
        EraCore.World.TerrainKind.Road => 2,
        EraCore.World.TerrainKind.Rock => 3,
        EraCore.World.TerrainKind.Wall => 4,
        EraCore.World.TerrainKind.Water => 8,
        _ => 0,
    };

    /// <summary>
    /// Existence probe over the non-terrain E2 pack files (towers/units/ui +
    /// the CC0 structure sprites). Returns the files that are present so the
    /// smoke marker can count them; a missing file is reported (not thrown)
    /// because these are stand-in art assets, not runtime-critical resources.
    /// </summary>
    public static List<string> ProbePackFiles()
    {
        string[] files =
        {
            "res://textures/era2/towers/tower-arrow-l1.png",
            "res://textures/era2/towers/tower-arrow-l2.png",
            "res://textures/era2/towers/tower-crossbow-l1.png",
            "res://textures/era2/towers/tower-crossbow-l2.png",
            "res://textures/era2/towers/tower-repeating-crossbow-l1.png",
            "res://textures/era2/towers/tower-repeating-crossbow-l2.png",
            "res://textures/era2/towers/tower-onager-l1.png",
            "res://textures/era2/towers/tower-onager-l2.png",
            "res://textures/era2/towers/tower-fire-oil-l1.png",
            "res://textures/era2/towers/tower-fire-oil-l2.png",
            "res://textures/era2/units/carriage-right.png",
            "res://textures/era2/ui/panel-era2.png",
            "res://textures/era2/structures/watchtower.png",
            "res://textures/era2/structures/gatehouse.png",
            "res://textures/era2/structures/wall-crenellated.png",
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
