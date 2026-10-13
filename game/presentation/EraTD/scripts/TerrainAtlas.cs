using Godot;

namespace EraTD;

/// <summary>
/// T-045 (C-28/C-21): E1 terrain atlas wiring. Loads the validator-gated CC0
/// 16 px tiles from <c>res://textures/tiles/</c> (Kenney Tiny Dungeon,
/// 14-art-license-audit §1.1 A1) and composes them into ONE horizontal
/// ImageTexture so the ground MultiMesh layer can sample every cell in a
/// single draw. The source tiles stay 16 px — the 2x integer scale to the
/// 32 px world cell happens in the shader via nearest filtering, which is
/// resample-free and keeps the CC0 pixels byte-identical (14 §1.1 A1 note).
/// </summary>
internal static class TerrainAtlas
{
    /// <summary>Source tile edge in px (Kenney Tiny Dungeon tile size).</summary>
    public const int TilePx = 16;

    /// <summary>
    /// Atlas cell order. The index in this array IS the atlas slot the
    /// shader computes from INSTANCE_CUSTOM.x; the mock framebuffer colors
    /// below reference these slots.
    /// </summary>
    private static readonly string[] TileFiles =
    {
        "floor-dirt",          // slot 0: Kenney tile 0048 — flat dirt floor
        "floor-dirt-pebbled",  // slot 1: Kenney tile 0049 — speckled floor
        "floor-dirt-mottled",  // slot 2: Kenney tile 0050 — mottled variation
        "floor-rubble",        // slot 3: Kenney tile 0024 — debris patch
        "wall-brick",          // slot 4: Kenney tile 0057 — brick wall face
        "wall-brick-alt",      // slot 5: Kenney tile 0058 — brick variant
        "wall-top",            // slot 6: Kenney tile 0026 — wall top meets floor
        "water-pool",          // slot 7: Kenney tile 0032 — glowing green pool
    };

    /// <summary>Number of atlas slots (= tiles per shader atlas row).</summary>
    public static int TileCount => TileFiles.Length;

    /// <summary>Builds the 1-row atlas texture (TileCount*16 x 16, RGBA8).</summary>
    public static ImageTexture Build()
    {
        var atlas = Image.CreateEmpty(TileCount * TilePx, TilePx, false, Image.Format.Rgba8);
        for (int slot = 0; slot < TileFiles.Length; slot++)
        {
            var texture = ResourceLoader.Load<Texture2D>($"res://textures/tiles/{TileFiles[slot]}.png");
            if (texture is null)
            {
                throw new InvalidOperationException(
                    $"terrain tile missing: res://textures/tiles/{TileFiles[slot]}.png");
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
                    $"terrain tile {TileFiles[slot]} is {tile.GetWidth()}x{tile.GetHeight()}, " +
                    $"expected {TilePx}x{TilePx} (asset_validator SIZE-ALLOWLIST)");
            }

            atlas.BlitRect(tile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(slot * TilePx, 0));
        }

        return ImageTexture.CreateFromImage(atlas);
    }

    /// <summary>
    /// Mock framebuffer color (0xRRGGBB) → atlas slot. The five greens of
    /// <see cref="DeterministicMockSnapshotSource"/>'s stable color field map
    /// onto the dirt/floor variety (darkest→rubble, brightest→pool) so the
    /// headless smoke shows a textured ground; unknown colors fall back to
    /// plain dirt (slot 0). Wall slots 4..6 are reachable by future callers
    /// (build/terrain rendering, T-056) without an atlas rebuild.
    /// </summary>
    public static int TileIndexForColor(int rgb) => rgb switch
    {
        0x2A4430 => 3, // floor-rubble
        0x2E4A34 => 0, // floor-dirt
        0x36523C => 1, // floor-dirt-pebbled
        0x405C42 => 2, // floor-dirt-mottled
        0x4A6648 => 7, // water-pool
        _ => 0,        // fallback: floor-dirt
    };
}
