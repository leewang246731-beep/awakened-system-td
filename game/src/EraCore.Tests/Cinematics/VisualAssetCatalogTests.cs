using EraCore.Cinematics;
using Xunit;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-113] [F-114] [T-152·31 号 §二/§三（视觉扩充批次不占特性序）] 素材目录（visual-assets.json）解析与入库产物：
// 五层 51 条视频计数/用途位/像素化档（Q18A 仅跃迁 5 条）/音频位、8 张
// Boss 序列帧、73 图（49 外部+24 成就图标）、六时代色板锚、合成篇
// requires 链；schema 违规逐项拒绝；入库产物真实文件断言（OggS 魔数/
// PNG IHDR 直读 6x6@240x135——禁 mock 文件系统）。
public class VisualAssetCatalogTests
{
    [Fact] // legal — 目录全量装载：51 视频/8 序列帧/73 图（49 外部+24 成就）/六色板/合成篇
    public void Catalog_carries_the_full_v2_expansion_counts()
    {
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();

        Assert.Equal(51, catalog.Videos.Count);
        Assert.Equal(7, catalog.Videos.Count(v => v.Layer == "A"));
        Assert.Equal(15, catalog.Videos.Count(v => v.Layer == "B"));
        Assert.Equal(17, catalog.Videos.Count(v => v.Layer == "C"));
        Assert.Equal(8, catalog.Videos.Count(v => v.Layer == "D"));
        Assert.Equal(4, catalog.Videos.Count(v => v.Layer == "E"));
        Assert.Equal(5, catalog.Videos.Count(v => v.Pixelate)); // Q18A：仅跃迁 A-02~06
        Assert.All(catalog.Videos.Where(v => v.Pixelate), v => Assert.StartsWith("A-0", v.Id));
        Assert.Equal(19, catalog.Videos.Count(v => v.Audio)); // A/D/E 层开音轨（7+8+4）
        Assert.Equal(8, catalog.Strips.Count);
        Assert.Equal(49, catalog.Images.Values.Sum(batch => batch.Count));
        Assert.Equal(24, catalog.Achievements.Count);
        Assert.Equal(6, catalog.Palettes.Count);
        Assert.Equal(8, catalog.Composite.Requires.Count);
    }

    [Fact] // legal — 用途位查询（插入点映射的消费面）+时长折算（30 tick/s）
    public void Catalog_usage_queries_and_tick_conversion()
    {
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();

        Assert.Equal("A-01", Assert.Single(catalog.VideosByUsage("menu_loop")).Id);
        Assert.Equal(5, catalog.VideosByUsage("era_transition").Count);
        Assert.Equal(6, catalog.VideosByUsage("wonder").Count);
        Assert.Equal(5, catalog.VideosByUsage("advisor_milestone").Count);
        Assert.Equal(4, catalog.VideosByUsage("persona_unlock").Count);
        Assert.Equal(4, catalog.VideosByUsage("boss_enter").Count);
        Assert.Equal(4, catalog.VideosByUsage("boss_death").Count);
        Assert.Equal(7, catalog.VideosByUsage("level_opening").Count);
        Assert.Equal(2, catalog.VideosByUsage("first_clear").Count);
        Assert.Equal(8, catalog.VideosByUsage("memory").Count);
        Assert.Equal(4, catalog.Videos.Count(v => v.Layer == "E")); // E 层彩蛋 4（easter_e09/easter_e05/hidden_finale_teaser/endless_milestone）

        VisualVideoAsset a01 = catalog.VideoById("A-01")!;
        Assert.Equal(602, catalog.DurationTicksOf(a01)); // 20.06s × 30tps
        Assert.NotNull(catalog.StripFor("e1", "boss_death"));
        Assert.NotNull(catalog.ImageByKey("story-e1-act2-awakening-1"));
        Assert.NotNull(catalog.ImageByKey("achv-zero-truth"));
        Assert.Null(catalog.VideoById("Z-99"));
    }

    [Fact] // legal — 六时代色板锚（31 号 §1.2：E1 赭金/E6 靛蓝=补色对）
    public void Catalog_palette_anchors_match_the_identity_cards()
    {
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();

        Assert.Equal((0xC9, 0x91, 0x5C), catalog.Palettes["e1"].Primary);
        Assert.Equal((0x3E, 0x55, 0xC9), catalog.Palettes["e6"].Primary);
        Assert.Equal("星门", catalog.Palettes["e6"].MotifZh);
        Assert.Equal(2, catalog.Palettes["e2"].Secondary.Count);
    }

    [Fact] // illegal — 目录 schema：坏时代/坏 hex/错时域/重复视频 id 拒绝
    public void Catalog_rejects_schema_violations()
    {
        string json = CinematicContent.ContentFile("cinematics/visual-assets.json");
        Assert.Throws<VisualAssetCatalog.VisualAssetSchemaException>(() =>
            VisualAssetCatalog.Parse(json.Replace("\"ticks_per_second\": 30", "\"ticks_per_second\": 60"), "t"));
        Assert.Throws<VisualAssetCatalog.VisualAssetSchemaException>(() =>
            VisualAssetCatalog.Parse(json.Replace("\"era\": \"e1\"", "\"era\": \"e9\""), "t"));
        Assert.Throws<VisualAssetCatalog.VisualAssetSchemaException>(() =>
            VisualAssetCatalog.Parse(json.Replace("\"primary\": \"c9915c\"", "\"primary\": \"zz\""), "t"));
        Assert.Throws<VisualAssetCatalog.VisualAssetSchemaException>(() =>
            VisualAssetCatalog.Parse(json.Replace("\"id\": \"A-01\"", "\"id\": \"A-02\""), "t"));
    }

    [Fact] // legal — 51 ogv+合成篇全部在仓且 OggS 魔数（内置轨入库门禁）
    public void Every_catalog_video_is_in_repo_as_ogg_container()
    {
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();
        string root = CinematicContent.RepoRoot();

        foreach (VisualVideoAsset video in catalog.Videos)
        {
            string path = Path.Combine(root, VisualAssetCatalog.RepoVideoRoot,
                video.File.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"入库缺失：{video.Id} -> {path}");
            using var stream = File.OpenRead(path);
            var magic = new byte[4];
            Assert.Equal(4, stream.Read(magic, 0, 4));
            Assert.Equal("OggS"u8.ToArray(), magic); // Theora/Vorbis 容器魔数
            Assert.True(stream.Length > 10_000, $"{video.Id} 体积极小（转码可疑）");
        }

        string composite = Path.Combine(root, VisualAssetCatalog.RepoVideoRoot,
            catalog.Composite.File.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(composite));
    }

    [Fact] // legal — 8 张 Boss 序列帧图集在仓且 6x6@240x135=1440x810（PNG IHDR 直读）
    public void Boss_strip_atlases_are_in_repo_with_the_declared_grid()
    {
        VisualAssetCatalog catalog = CinematicContent.VisualAssets();
        string root = CinematicContent.RepoRoot();

        foreach (VisualStripAsset strip in catalog.Strips)
        {
            string path = Path.Combine(root, VisualAssetCatalog.RepoVideoRoot,
                strip.File.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"序列帧缺失：{strip.Id} -> {path}");
            byte[] header = File.ReadAllBytes(path)[..33];
            Assert.Equal(0x89, header[0]); // PNG 签名首字节
            Assert.Equal(strip.Cols * strip.FrameWidth, ReadBigEndianInt(header, 16));   // 6×240=1440
            Assert.Equal(strip.Frames / strip.Cols * strip.FrameHeight, ReadBigEndianInt(header, 20)); // 6×135=810
        }
    }

    private static int ReadBigEndianInt(byte[] source, int offset) =>
        (source[offset] << 24) | (source[offset + 1] << 16) | (source[offset + 2] << 8) | source[offset + 3];
}
