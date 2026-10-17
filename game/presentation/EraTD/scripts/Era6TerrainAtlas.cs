using System.Collections.Generic;
using Godot;

namespace EraTD;

/// <summary>
/// W8 终章视觉接线 (T-142, C-28/F-133): final-chapter deep-space terrain
/// atlas wiring — same composition scheme as E1~E4 (one horizontal strip,
/// nearest-filter sampling, 16 px source tiles scaled 2x in the shader),
/// sourcing the fully procedural era6 set from
/// <c>res://textures/era6/tiles/</c> (generator
/// <c>tools/art/era6_placeholders.py</c>, T-142; no CC0 pack ships a deep
/// space set — T-078 fallback route, era4/era5 先例第 3 次重演). Slot order
/// mirrors the Era6ArtMappingTests tile table exactly (虚空岩床 Plain/
/// 星云代位/光线 Road×2/星门 Wall×2/虚空废墟 Rock×2/虚空海 Water); the era6
/// ground layer stays routed by the render loop as with E2~E5 — this atlas
/// exists so the pack loads and composes in-engine today, the smoke marker
/// ART6 proves it.
/// </summary>
internal static class Era6TerrainAtlas
{
    /// <summary>Source tile edge in px (procedural tile size, same as the atlas baseline).</summary>
    public const int TilePx = 16;

    /// <summary>
    /// Atlas cell order. The index in this array IS the atlas slot the
    /// shader computes from INSTANCE_CUSTOM.x. Slots 0..8 mirror the E2~E5
    /// semantic order (plain / stand-in / road×2 / wall×2 / rock variants /
    /// water) with the deep-space palette: void stone floor / nebula
    /// strata stand-in / light-ray road / stargate wall / void rubble /
    /// void sea.
    /// </summary>
    private static readonly string[] TileFiles =
    {
        "floor-voidstone",      // slot 0: void stone bed (Plain)
        "floor-nebula",         // slot 1: nebula strata variant (Forest stand-in)
        "road-lightray",        // slot 2: light-ray conduit road (Road = 光线路)
        "road-lightray-alt",    // slot 3: lens-coupled variant
        "wall-stargate",        // slot 4: stargate barrier wall (Wall = 星门墙)
        "wall-stargate-alt",    // slot 5: lit-field variant
        "floor-rubble-void",    // slot 6: shattered void rubble (Rock)
        "floor-rubble-void-alt", // slot 7: rubble variant
        "water-voidsea",        // slot 8: void energy sea (Water = 虚空海)
    };

    /// <summary>Number of atlas slots.</summary>
    public static int TileCount => TileFiles.Length;

    /// <summary>Builds the 1-row atlas texture (TileCount*16 x 16, RGBA8).</summary>
    public static ImageTexture Build()
    {
        var atlas = Image.CreateEmpty(TileCount * TilePx, TilePx, false, Image.Format.Rgba8);
        for (int slot = 0; slot < TileFiles.Length; slot++)
        {
            var texture = ResourceLoader.Load<Texture2D>($"res://textures/era6/tiles/{TileFiles[slot]}.png");
            if (texture is null)
            {
                throw new InvalidOperationException(
                    $"era6 terrain tile missing: res://textures/era6/tiles/{TileFiles[slot]}.png");
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
                    $"era6 terrain tile {TileFiles[slot]} is {tile.GetWidth()}x{tile.GetHeight()}, " +
                    $"expected {TilePx}x{TilePx} (asset_validator SIZE-ALLOWLIST)");
            }

            atlas.BlitRect(tile, new Rect2I(0, 0, TilePx, TilePx), new Vector2I(slot * TilePx, 0));
        }

        return ImageTexture.CreateFromImage(atlas);
    }

    /// <summary>
    /// <see cref="EraCore.World.TerrainKind"/> → atlas slot for the era6
    /// palette. Plain maps to the void stone floor; Forest reuses the nebula
    /// strata stand-in; Road maps to the light-ray road; Rock reuses the
    /// void rubble; Wall maps to the stargate barrier; unknown kinds fall
    /// back to the void stone floor (slot 0).
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
