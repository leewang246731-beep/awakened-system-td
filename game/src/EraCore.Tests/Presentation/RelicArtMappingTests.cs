using System.IO.Compression;
using System.Text;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-28][C-22][F-057][F-168] 跨代遗迹标记资产映射完整性（T-125 遗迹映射正式版）：
// RelicMarkerBoard 四类标记的 TextureStem 常量 ↔ textures/relic/ 实存 PNG 逐一
// 对应（"无标记无素材"清零，28 号 §2.4 冻结面；Era3ArtMappingTests 同纪律的
// era 无关域版）——签名/画布/位深/颜色类型/行滤波=合成器写出契约（Era3 同款
// 解码口径），半透明保留区 tile 的 alpha 通道实值断言。
public class RelicArtMappingTests
{
    // ---- 仓库相对寻址（RL9：禁绝对路径；Era3ArtMappingTests 同纪律） ----------

    private static string TexturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "presentation", "EraTD", "textures", "relic");
            if (Directory.Exists(probe))
            {
                return new DirectoryInfo(probe).FullName;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/presentation/EraTD/textures/relic（测试须在仓库内运行）");
    }

    private static string Texture(string stem) => Path.Combine(TexturesDir(), stem + ".png");

    // ---- PNG 头解析（IHDR 宽高；Era3ArtMappingTests 同款） --------------------

    private static (int Width, int Height, byte[] Pixels) DecodeRgba(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        Assert.True(data.Length > 24 && data[0] == 0x89 && data[1] == (byte)'P',
            $"{path} 缺 PNG 签名（asset_validator G2.5 前置）");
        Assert.Equal(13, BitConverter.ToInt32(data[8..12].Reverse().ToArray(), 0));
        Assert.Equal("IHDR", Encoding.ASCII.GetString(data[12..16]));
        int width = BitConverter.ToInt32(data[16..20].Reverse().ToArray(), 0);
        int height = BitConverter.ToInt32(data[20..24].Reverse().ToArray(), 0);
        Assert.Equal(8, data[24]); // 位深 8
        Assert.Equal(6, data[25]); // 颜色类型 RGBA

        using var ms = new MemoryStream();
        int pos = 8;
        while (pos < data.Length)
        {
            int len = BitConverter.ToInt32(data[pos..(pos + 4)].Reverse().ToArray(), 0);
            string type = Encoding.ASCII.GetString(data[(pos + 4)..(pos + 8)]);
            byte[] chunk = data[(pos + 8)..(pos + 8 + len)];
            if (type == "IDAT")
            {
                ms.Write(chunk);
            }
            else if (type == "IEND")
            {
                break;
            }

            pos += 12 + len;
        }

        ms.Position = 0;
        using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] pixels = raw.ToArray();
        Assert.Equal((width * 4 + 1) * height, pixels.Length);
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, pixels[y * (width * 4 + 1)]); // 行滤波 type 0（合成器契约）
        }

        return (width, height, pixels);
    }

    private static (byte R, byte G, byte B, byte A) Pixel((int Width, int Height, byte[] Pixels) img, int x, int y)
    {
        int i = y * (img.Width * 4 + 1) + 1 + x * 4;
        return (img.Pixels[i], img.Pixels[i + 1], img.Pixels[i + 2], img.Pixels[i + 3]);
    }

    // ---- 映射完整性 ------------------------------------------------------ legal

    [Theory] // legal — 四类标记纹理逐一实存且画布规格正确（stem=RelicMarkerBoard 冻结常量）
    [InlineData(RelicMarkerBoard.StandingRelicTexture, 32, 32)]
    [InlineData(RelicMarkerBoard.DigSiteTexture, 32, 32)]
    [InlineData(RelicMarkerBoard.MemorialSlotTexture, 32, 32)]
    [InlineData(RelicMarkerBoard.CoreReserveTexture, 16, 16)]
    public void marker_textures_exist_with_pinned_canvases(string stem, int width, int height)
    {
        string path = Texture(stem);
        Assert.True(File.Exists(path), $"textures/relic/{stem}.png 缺失（无标记无素材，28 号 §2.4）");
        (int w, int h, byte[] pixels) = DecodeRgba(path);
        Assert.Equal(width, w); // SIZE-ALLOWLIST {16,32} 内（G2.5）
        Assert.Equal(height, h);

        // 非空画布：既有实心像素也有透明底（标记 sprite=镂空贴图，非整块填充）
        int opaque = 0, transparent = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (Pixel((w, h, pixels), x, y).A == 0)
                {
                    transparent++;
                }
                else
                {
                    opaque++;
                }
            }
        }

        Assert.True(opaque > w * h / 8, $"{stem} 实心像素不足（{opaque}/{w * h}）");
        Assert.True(transparent > w * h / 8, $"{stem} 透明底不足（{transparent}/{w * h}）");
    }

    [Fact] // legal — 保留区勾勒 tile=半透明覆盖层（alpha 192 边框 + 255 四角加重），
    // 与实体标记的全不透明形成通道语义区分（UI 叠加层 vs 地图实体）
    public void reserve_overlay_carries_semi_transparent_border_pixels()
    {
        (int w, int h, byte[] pixels) = DecodeRgba(Texture(RelicMarkerBoard.CoreReserveTexture));
        // 左上角饰（1,1）=不透明加重
        Assert.Equal(255, Pixel((w, h, pixels), 1, 1).A);
        Assert.Equal(255, Pixel((w, h, pixels), 2, 2).A);
        // 边框中段（7,1）=半透明 192
        Assert.Equal(192, Pixel((w, h, pixels), 7, 1).A);
        Assert.Equal(192, Pixel((w, h, pixels), 14, 7).A);
        // 画布中心=全透明（勾勒 tile 不覆盖区内部）
        Assert.Equal(0, Pixel((w, h, pixels), 8, 8).A);
        // 勾勒金色域：金色 hue 实值（outline_gold=222,184,92）
        (byte r, byte g, byte b, _) = Pixel((w, h, pixels), 7, 1);
        Assert.Equal(222, r);
        Assert.Equal(184, g);
        Assert.Equal(92, b);
    }

    [Fact] // legal — 目录零孤儿：textures/relic/ 内 PNG 恰为四个冻结 stem（不多不少）
    public void relic_texture_directory_carries_exactly_the_frozen_stems()
    {
        var expected = new SortedSet<string>(StringComparer.Ordinal)
        {
            RelicMarkerBoard.StandingRelicTexture + ".png",
            RelicMarkerBoard.DigSiteTexture + ".png",
            RelicMarkerBoard.MemorialSlotTexture + ".png",
            RelicMarkerBoard.CoreReserveTexture + ".png",
        };
        var actual = new SortedSet<string>(
            Directory.GetFiles(TexturesDir(), "*.png").Select(file => Path.GetFileName(file)!),
            StringComparer.Ordinal);
        Assert.Equal(expected, actual); // 孤儿文件=漂移（28 号 §6 冻结纪律）
    }

    // ---- boundary ----------------------------------------------------------

    [Fact] // boundary — 常量与文件名双射：RelicMarkerBoard 四常量互异、皆 kebab-case
    // 且落在 relic- 命名空间（G2.5 命名门禁的决策层镜像——渲染侧不得自造 stem）
    public void marker_texture_constants_are_distinct_kebab_stems()
    {
        var stems = new[]
        {
            RelicMarkerBoard.StandingRelicTexture,
            RelicMarkerBoard.DigSiteTexture,
            RelicMarkerBoard.MemorialSlotTexture,
            RelicMarkerBoard.CoreReserveTexture,
        };
        Assert.Equal(stems.Length, stems.Distinct().Count());
        Assert.All(stems, stem =>
        {
            Assert.DoesNotContain('_', stem); // kebab-case（asset_validator KEBAB_RE_OK 镜像）
            Assert.StartsWith("relic-", stem, StringComparison.Ordinal); // 跨代遗迹域命名空间
        });
    }
}
