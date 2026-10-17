using System.Collections.Generic;
using Godot;

namespace EraTD;

/// <summary>
/// W7 E4 视觉接线 (T-128, C-28/F-133): E4 modern terrain atlas wiring — the
/// presentation half 44 号 §1 explicitly deferred out of the T-128 art-pack
/// card ("Era4TerrainAtlas/ArtPack 接线不在本任务卡范围"). Same composition
/// scheme as the E1 <see cref="TerrainAtlas"/> / E2
/// <see cref="Era2TerrainAtlas"/> / E3 <see cref="Era3TerrainAtlas"/>
/// (one horizontal strip, nearest-filter sampling, 16 px source tiles scaled
/// 2x in the shader), sourcing the fully procedural E4 set from
/// <c>res://textures/era4/tiles/</c> (generator
/// <c>tools/art/era4_placeholders.py</c>, doc 44 §2.3; no CC0 pack ships a
/// modern set, so E4 is 100% procedural per the T-078 fallback route).
/// Slot order mirrors the Era4ArtMappingTests tile table exactly (混凝土
/// Plain/代位/公路 Road×2/玻璃幕墙 Wall×2/碎石 Rock×2/现代水面 Water);
/// the E4 ground layer stays routed by the render loop as with E2/E3 —
/// this atlas exists so the pack loads and composes in-engine today, the
/// smoke marker ART4 proves it.
/// </summary>
internal static class Era4TerrainAtlas
{
    /// <summary>Source tile edge in px (procedural tile size, same as the atlas baseline).</summary>
    public const int TilePx = 16;

    /// <summary>
    /// Atlas cell order. The index in this array IS the atlas slot the
    /// shader computes from INSTANCE_CUSTOM.x. Slots 0..8 mirror the E2/E3
    /// semantic order (plain / stand-in / road×2 / wall×2 / rock variants /
    /// water) with the E4 palette: concrete floor / grated stand-in /
    /// asphalt highway / glass curtain wall / concrete rubble / modern water.
    /// </summary>
    private static readonly string[] TileFiles =
    {
        "floor-concrete",         // slot 0: poured concrete floor (Plain)
        "floor-concrete-grated",  // slot 1: grated variant (Forest stand-in)
        "road-asphalt",           // slot 2: asphalt highway with lane marks (Road = 公路)
        "road-asphalt-alt",       // slot 3: worn asphalt variant
        "wall-glass-curtain",     // slot 4: glass curtain-wall tower face (Wall = 玻璃幕墙)
        "wall-glass-curtain-alt", // slot 5: curtain-wall variant (lit floors)
        "floor-rubble-concrete",  // slot 6: shattered concrete rubble (Rock)
        "floor-rubble-concrete-alt", // slot 7: rubble variant
        "water-modern",           // slot 8: modern basin water (Water = 水面)
    };

    /// <summary>Number of atlas slots.</summary>
    public static int TileCount => TileFiles.Length;

    /// <summary>Builds the 1-row atlas texture (TileCount*16 x 16, RGBA8).</summary>
    public static ImageTexture Build()
    {
        var atlas = Image.CreateEmpty(TileCount * TilePx, TilePx, false, Image.Format.Rgba8);
        for (int slot = 0; slot < TileFiles.Length; slot++)
        {
            var texture = ResourceLoader.Load<Texture2D>($"res://textures/era4/tiles/{TileFiles[slot]}.png");
            if (texture is null)
            {
                throw new InvalidOperationException(
                    $"era4 terrain tile missing: res://textures/era4/tiles/{TileFiles[slot]}.png");
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
                    $"era4 terrain tile {TileFiles[slot]} is {tile.GetWidth()}x{tile.GetHeight()}, " +
                    $"expected {TilePx}x{TilePx} (asset_validator SIZE-ALLOWLIST)");
            }

            atlas.BlitRect(tile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(slot * TilePx, 0));
        }

        return ImageTexture.CreateFromImage(atlas);
    }

    /// <summary>
    /// <see cref="EraCore.World.TerrainKind"/> → atlas slot for the E4 palette.
    /// Plain maps to the poured concrete floor; Forest reuses the grated
    /// stand-in; Road maps to the asphalt highway (现代道路=公路); Rock reuses
    /// concrete rubble; Wall maps to the glass curtain wall; unknown kinds
    /// fall back to the concrete floor (slot 0).
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
