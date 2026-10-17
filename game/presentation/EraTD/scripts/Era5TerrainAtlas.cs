using System.Collections.Generic;
using Godot;

namespace EraTD;

/// <summary>
/// W8 E5 视觉接线 (T-142, C-28/F-133): E5 future terrain atlas wiring —
/// same composition scheme as the E1 <see cref="TerrainAtlas"/> / E2
/// <see cref="Era2TerrainAtlas"/> / E3 <see cref="Era3TerrainAtlas"/> / E4
/// <see cref="Era4TerrainAtlas"/> (one horizontal strip, nearest-filter
/// sampling, 16 px source tiles scaled 2x in the shader), sourcing the
/// fully procedural E5 set from <c>res://textures/era5/tiles/</c>
/// (generator <c>tools/art/era5_placeholders.py</c>, T-142; no CC0 pack
/// ships a future set, so E5 is 100% procedural per the T-078 fallback
/// route — era4 同构第 2 次重演：母题形变×色彩层积). Slot order mirrors
/// the Era5ArtMappingTests tile table exactly (纳米地坪 Plain/全息格点
/// 代位/光流路 Road×2/力场墙 Wall×2/纳米废墟 Rock×2/冷却池 Water); the
/// E5 ground layer stays routed by the render loop as with E2/E3/E4 —
/// this atlas exists so the pack loads and composes in-engine today, the
/// smoke marker ART5 proves it.
/// </summary>
internal static class Era5TerrainAtlas
{
    /// <summary>Source tile edge in px (procedural tile size, same as the atlas baseline).</summary>
    public const int TilePx = 16;

    /// <summary>
    /// Atlas cell order. The index in this array IS the atlas slot the
    /// shader computes from INSTANCE_CUSTOM.x. Slots 0..8 mirror the E2/E3/E4
    /// semantic order (plain / stand-in / road×2 / wall×2 / rock variants /
    /// water) with the E5 palette: nano cast floor / holo-lattice stand-in /
    /// flux conduit road / force-field barrier wall / nano rubble / coolant
    /// pool.
    /// </summary>
    private static readonly string[] TileFiles =
    {
        "floor-nano",           // slot 0: nano cast floor (Plain)
        "floor-nano-lattice",   // slot 1: holo-lattice variant (Forest stand-in)
        "road-flux",            // slot 2: light-flow conduit road (Road = 光流路)
        "road-flux-alt",        // slot 3: coupled-node variant
        "wall-barrier",         // slot 4: force-field barrier wall (Wall = 力场墙)
        "wall-barrier-alt",     // slot 5: lit-field variant
        "floor-rubble-nano",    // slot 6: shattered nano rubble (Rock)
        "floor-rubble-nano-alt", // slot 7: rubble variant
        "water-coolant",        // slot 8: fusion coolant pool (Water = 冷却池)
    };

    /// <summary>Number of atlas slots.</summary>
    public static int TileCount => TileFiles.Length;

    /// <summary>Builds the 1-row atlas texture (TileCount*16 x 16, RGBA8).</summary>
    public static ImageTexture Build()
    {
        var atlas = Image.CreateEmpty(TileCount * TilePx, TilePx, false, Image.Format.Rgba8);
        for (int slot = 0; slot < TileFiles.Length; slot++)
        {
            var texture = ResourceLoader.Load<Texture2D>($"res://textures/era5/tiles/{TileFiles[slot]}.png");
            if (texture is null)
            {
                throw new InvalidOperationException(
                    $"era5 terrain tile missing: res://textures/era5/tiles/{TileFiles[slot]}.png");
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
                    $"era5 terrain tile {TileFiles[slot]} is {tile.GetWidth()}x{tile.GetHeight()}, " +
                    $"expected {TilePx}x{TilePx} (asset_validator SIZE-ALLOWLIST)");
            }

            atlas.BlitRect(tile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(slot * TilePx, 0));
        }

        return ImageTexture.CreateFromImage(atlas);
    }

    /// <summary>
    /// <see cref="EraCore.World.TerrainKind"/> → atlas slot for the E5 palette.
    /// Plain maps to the nano cast floor; Forest reuses the holo-lattice
    /// stand-in; Road maps to the flux conduit road; Rock reuses the nano
    /// rubble; Wall maps to the force-field barrier; unknown kinds fall back
    /// to the nano floor (slot 0).
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
