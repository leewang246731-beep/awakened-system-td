using System.Collections.Generic;
using Godot;

namespace EraTD;

/// <summary>
/// T-119 (C-28): E3 industrial terrain atlas wiring. Same composition scheme
/// as the E1 <see cref="TerrainAtlas"/> / E2 <see cref="Era2TerrainAtlas"/>
/// (one horizontal strip, nearest-filter sampling, 16 px source tiles scaled
/// 2x in the shader), sourcing the fully procedural E3 set from
/// <c>res://textures/era3/tiles/</c> (generator
/// <c>tools/art/era3_placeholders.py</c>; no CC0 pack ships an industrial
/// set, so E3 is 100% procedural per the T-078 fallback route — 41 号 §2).
/// The E3 ground layer is not switched into the render loop here (era
/// routing lands with the E3 level visuals); this atlas exists so the pack
/// loads and composes in-engine today — the smoke marker ART3 proves it.
/// </summary>
internal static class Era3TerrainAtlas
{
    /// <summary>Source tile edge in px (procedural tile size, same as the atlas baseline).</summary>
    public const int TilePx = 16;

    /// <summary>
    /// Atlas cell order. The index in this array IS the atlas slot the
    /// shader computes from INSTANCE_CUSTOM.x. Slots 0..8 mirror the E2
    /// semantic order (plain / stand-in / road / rock / wall×2 / variants /
    /// water).
    /// </summary>
    private static readonly string[] TileFiles =
    {
        "floor-steel",         // slot 0: steel-plated industrial floor (Plain)
        "floor-steel-grated",  // slot 1: grate variant (Forest stand-in)
        "rail-iron",           // slot 2: iron rails + sleepers (Road = 工业道路=铁轨)
        "rail-iron-alt",       // slot 3: sleeper-offset rail variant
        "wall-riveted",        // slot 4: riveted iron plate wall (Wall)
        "wall-riveted-alt",    // slot 5: wall variant (coal band)
        "floor-slag",          // slot 6: coal slag rubble (Rock)
        "floor-slag-alt",      // slot 7: slag variant
        "water-industrial",    // slot 8: oily industrial runoff (Water)
    };

    /// <summary>Number of atlas slots.</summary>
    public static int TileCount => TileFiles.Length;

    /// <summary>Builds the 1-row atlas texture (TileCount*16 x 16, RGBA8).</summary>
    public static ImageTexture Build()
    {
        var atlas = Image.CreateEmpty(TileCount * TilePx, TilePx, false, Image.Format.Rgba8);
        for (int slot = 0; slot < TileFiles.Length; slot++)
        {
            var texture = ResourceLoader.Load<Texture2D>($"res://textures/era3/tiles/{TileFiles[slot]}.png");
            if (texture is null)
            {
                throw new InvalidOperationException(
                    $"era3 terrain tile missing: res://textures/era3/tiles/{TileFiles[slot]}.png");
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
                    $"era3 terrain tile {TileFiles[slot]} is {tile.GetWidth()}x{tile.GetHeight()}, " +
                    $"expected {TilePx}x{TilePx} (asset_validator SIZE-ALLOWLIST)");
            }

            atlas.BlitRect(tile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(slot * TilePx, 0));
        }

        return ImageTexture.CreateFromImage(atlas);
    }

    /// <summary>
    /// <see cref="EraCore.World.TerrainKind"/> → atlas slot for the E3 palette.
    /// Plain maps to the steel plate floor; Forest reuses the grate stand-in;
    /// Road maps to the iron rails (工业道路=铁轨); Rock reuses slag rubble;
    /// unknown kinds fall back to the steel floor (slot 0).
    /// </summary>
    public static int TileIndexForTerrain(EraCore.World.TerrainKind kind) => kind switch
    {
        EraCore.World.TerrainKind.Plain => 0,
        EraCore.World.TerrainKind.Forest => 1,
        EraCore.World.TerrainKind.Road => 2,
        EraCore.World.TerrainKind.Rock => 6,
        EraCore.World.TerrainKind.Wall => 4,
        EraCore.World.TerrainKind.Water => 8,
        _ => 0,
    };
}
