using System.IO.Compression;
using System.Text.Json;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-28] [F-133] [F-127] T-119 E3 实体→素材映射完整性 + 传送带 8 帧循环像素级
// 断言：era3 内容 JSON 全实体（三塔×升级位/九敌+堡垒号/矿轨载具）必须有对应
// 过校验器的素材（"无实体无素材"清零，28 号 §2 先例）；belt-turn-anim.png 逐
// 像素复验合成器 u 律（帧边界对齐+循环闭合）与 BeltRenderDecisions 冻结常量
// 的跨层契约（headless 决策层正确指向验收的素材侧一半）。
public class Era3ArtMappingTests
{
    // ---- 仓库相对寻址（RL9：禁绝对路径；Era3ContentTests 同纪律） ----------

    private static DirectoryInfo? FindRepoDir(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, Path.Combine(relative));
            if (Directory.Exists(probe))
            {
                return new DirectoryInfo(probe);
            }

            dir = dir.Parent!;
        }

        return null;
    }

    private static string TexturesDir()
    {
        var dir = FindRepoDir("game", "presentation", "EraTD", "textures", "era3");
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ContentDir()
    {
        var dir = FindRepoDir("game", "content", "era3");
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Texture(params string[] parts) =>
        Path.Combine(new[] { TexturesDir() }.Concat(parts).ToArray());

    // ---- PNG 头解析（IHDR 宽高；不引三方依赖） ------------------------------

    private static (int Width, int Height) PngSize(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        Assert.True(data.Length > 24 && data[0] == 0x89 && data[1] == (byte)'P',
            $"{path} 缺 PNG 签名（asset_validator G2.5 前置）");
        // IHDR 为首个分块：长度 13，类型偏移 8，数据偏移 16（宽高各 4 字节大端）
        Assert.Equal(13, BitConverter.ToInt32(data[8..12].Reverse().ToArray(), 0));
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(data[12..16]));
        int width = BitConverter.ToInt32(data[16..20].Reverse().ToArray(), 0);
        int height = BitConverter.ToInt32(data[20..24].Reverse().ToArray(), 0);
        return (width, height);
    }

    // ---- PNG 全解码（era3 合成器契约：行滤波全 0、RGBA8） ------------------

    /// <summary>解码 RGBA8/滤波 0 PNG（合成器 Canvas.write 的写出契约；非 0 滤波行=违约即红）。</summary>
    private static (byte[] Data, int Width) DecodeRgba(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        using var ms = new MemoryStream();
        int pos = 8;
        (int w, int h) = (0, 0);
        while (pos < data.Length)
        {
            int len = BitConverter.ToInt32(data[pos..(pos + 4)].Reverse().ToArray(), 0);
            string type = System.Text.Encoding.ASCII.GetString(data[(pos + 4)..(pos + 8)]);
            byte[] chunk = data[(pos + 8)..(pos + 8 + len)];
            if (type == "IHDR")
            {
                w = BitConverter.ToInt32(chunk[0..4].Reverse().ToArray(), 0);
                h = BitConverter.ToInt32(chunk[4..8].Reverse().ToArray(), 0);
                Assert.Equal(8, chunk[8]);  // 位深 8
                Assert.Equal(6, chunk[9]);  // 颜色类型 RGBA
            }
            else if (type == "IDAT")
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
        Assert.Equal((w * 4 + 1) * h, pixels.Length);

        var px = new byte[w * h * 4]; // 平铺 RGBA 行主序（stride = Width*4）
        for (int y = 0; y < h; y++)
        {
            Assert.Equal(0, pixels[y * (w * 4 + 1)]); // 行滤波 type 0（合成器契约）
            Array.Copy(pixels, y * (w * 4 + 1) + 1, px, y * w * 4, w * 4);
        }

        return (px, w);
    }

    private static (byte R, byte G, byte B, byte A) Pixel(
        (byte[] Data, int Width) img, int x, int y)
    {
        int i = y * img.Width * 4 + x * 4;
        return (img.Data[i], img.Data[i + 1], img.Data[i + 2], img.Data[i + 3]);
    }

    /// <summary>两帧逐像素相等判定（⑤ 非退化断言的内容比对）。</summary>
    private static bool SameFrame((byte R, byte G, byte B, byte A)[,] a, (byte R, byte G, byte B, byte A)[,] b)
    {
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                if (a[x, y] != b[x, y])
                {
                    return false;
                }
            }
        }

        return true;
    }

    // ---- 冻结契约镜像（era3_placeholders.py PALETTE/PAT16/u 律） ------------

    private static readonly (byte R, byte G, byte B)[] Pat16 =
    {
        (46, 42, 40), (46, 42, 40), (181, 137, 58), (46, 42, 40),   // 0..3 煤黑/黄铜亮条
        (46, 42, 40), (46, 42, 40), (46, 42, 40), (46, 42, 40),     // 4..7
        (66, 60, 57), (66, 60, 57), (138, 102, 38), (66, 60, 57),   // 8..11 煤灰/黄铜暗条
        (66, 60, 57), (66, 60, 57), (66, 60, 57), (66, 60, 57),     // 12..15
    };

    /// <summary>转弯路径坐标 u（与合成器 turn_path_u 同式：竖臂/弯心/横臂）。</summary>
    private static int? TurnPathU(int x, int y)
    {
        if (x >= 5 && x <= 10 && y >= 11 && y <= 15)
        {
            return 15 - y;
        }

        if (x >= 5 && x <= 10 && y >= 5 && y <= 10)
        {
            return 5 + (x - 5) + (10 - y);
        }

        if (x >= 11 && x <= 15 && y >= 5 && y <= 10)
        {
            return 15 + (x - 10);
        }

        return null;
    }

    // ---------------------------------------------------- 映射完整性（F-133）

    [Fact] // legal — 三塔×升级位全部有素材（towers.json tier/max_tier 驱动文件名）
    public void All_era3_towers_have_level_sprites()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ContentDir(), "towers.json")));
        int checkedFiles = 0;
        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            string stem = id["era3:".Length..]; // era3:tower-gatling → tower-gatling
            int maxTier = item.GetProperty("max_tier").GetInt32();
            int canvas = stem == "tower-mortar" ? 48 : 32; // 臼炮重炮座 48（E2 弩炮同档先例）
            for (int level = 1; level <= maxTier; level++)
            {
                string path = Texture("towers", $"{stem}-l{level}.png");
                Assert.True(File.Exists(path), $"缺塔素材：{stem}-l{level}.png");
                Assert.Equal(canvas, PngSize(path).Width);
                Assert.Equal(canvas, PngSize(path).Height);
                checkedFiles++;
            }
        }

        Assert.Equal(5, checkedFiles); // 加特林 l1/l2 + 臼炮 l1/l2 + 铁甲墙 l1
    }

    [Fact] // legal — 九敌+堡垒号 Boss 全部有素材（enemies.json 全行）
    public void All_era3_enemies_and_boss_have_sprites()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ContentDir(), "enemies.json")));
        int regular = 0;
        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            string stem = id["era3:".Length..];
            if (item.GetProperty("sp_id").GetString() == "e3_boss")
            {
                // Boss 画布裁定（28 号 §5 先例）：内容 canvas_size_px=96 超校验器
                // 尺寸清单 → 取「常规 Boss 64~96」下限 64；升 96 须 SIZE-ALLOWLIST
                // 扩展走 REV。素材名=堡垒号（era.json boss 块 name_en=Bastion）。
                string boss = Texture("enemies", "boss-bastion.png");
                Assert.True(File.Exists(boss), "缺 Boss 素材：boss-bastion.png");
                Assert.Equal((64, 64), PngSize(boss));
                continue;
            }

            string path = Texture("enemies", $"{stem}.png");
            Assert.True(File.Exists(path), $"缺敌素材：{stem}.png");
            Assert.Equal((32, 32), PngSize(path));
            regular++;
        }

        Assert.Equal(8, regular);
    }

    [Fact] // legal — 载具/UI/地形/装饰/传送带前置资产全部在场（era.json + E4 前置）
    public void Era3_pack_vehicles_ui_tiles_structures_and_belt_forward_assets_exist()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ContentDir(), "era.json")));
        JsonElement era = doc.RootElement.GetProperty("items")[0];
        // E3 载具行：logistics.carrier=mine_rail → 矿轨矿车
        Assert.Equal("mine_rail", era.GetProperty("logistics").GetProperty("carrier").GetString());
        Assert.True(File.Exists(Texture("units", "mine-rail-right.png")));
        // E4 前置（era.json _ladder_note：传送带 15/s 与卡车=第 3 档，T-123 数据位；
        // T-119 预制双层动画与卡车为前置资产）
        Assert.True(File.Exists(Texture("units", "truck-right.png")));
        // UI 色板 / 烟囱装饰
        Assert.True(File.Exists(Texture("ui", "panel-era3.png")));
        Assert.True(File.Exists(Texture("structures", "chimney.png")));
        Assert.Equal((32, 64), PngSize(Texture("structures", "chimney.png")));
        // 地形 9 槽（Era3TerrainAtlas.TileFiles 同序）
        string[] tiles =
        {
            "floor-steel", "floor-steel-grated", "rail-iron", "rail-iron-alt",
            "wall-riveted", "wall-riveted-alt", "floor-slag", "floor-slag-alt",
            "water-industrial",
        };
        foreach (string tile in tiles)
        {
            Assert.True(File.Exists(Texture("tiles", $"{tile}.png")), $"缺地形槽：{tile}");
            Assert.Equal((16, 16), PngSize(Texture("tiles", $"{tile}.png")));
        }
    }

    // ------------------------------------------- 传送带双层契约（F-127 核心）

    [Fact] // legal — 直段双层纹理：带体 16×16 沿 V 可平铺（周期 16）+带边静态轨
    public void Belt_straight_dual_layer_textures_follow_the_frozen_geometry()
    {
        (int w, int h) = PngSize(Texture("belt", "belt-body.png"));
        Assert.Equal((BeltRenderDecisions.BodyTilePx, BeltRenderDecisions.BodyTilePx), (w, h));
        Assert.Equal((16, 16), PngSize(Texture("belt", "belt-edge.png")));
        Assert.Equal((16, 16), PngSize(Texture("belt", "belt-turn-edge.png")));

        // 带体 u 律：列 5..10 全 16 行，行色=PAT16[y%16]（V 向周期 16 → 无缝平铺）
        var body = DecodeRgba(Texture("belt", "belt-body.png"));
        for (int y = 0; y < 16; y++)
        {
            var expected = Pat16[y % 16];
            for (int x = 0; x < 16; x++)
            {
                var px = Pixel(body, x, y);
                if (x >= 5 && x <= 10)
                {
                    Assert.True(px.A == 255 && (px.R, px.G, px.B) == expected,
                        $"belt-body ({x},{y}) 违 PAT16 律");
                }
                else
                {
                    Assert.Equal(0, px.A); // 带外全透明（双层拆分：边框在静态层）
                }
            }
        }

        // 直段带边与带体不透明集零重叠（edge 永远叠在透明槽上，不遮带体）
        var edge = DecodeRgba(Texture("belt", "belt-edge.png"));
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Assert.False(Pixel(edge, x, y).A != 0 && Pixel(body, x, y).A != 0,
                    $"belt-edge 与 belt-body 重叠 ({x},{y})");
            }
        }
    }

    [Fact] // legal — 转弯 8 帧循环：网格几何+u 律+帧边界平移+循环闭合（验收核心）
    public void Belt_turn_strip_is_a_seamless_eight_frame_loop()
    {
        // ① 网格几何：64×32 = 4 列×2 行×16px（8 帧横条 128px 宽超尺寸清单 → 网格）
        (int w, int h) = PngSize(Texture("anim", "belt-turn-anim.png"));
        Assert.Equal(64, w);
        Assert.Equal(32, h);
        var strip = DecodeRgba(Texture("anim", "belt-turn-anim.png"));
        var frames = new (byte R, byte G, byte B, byte A)[8][,];
        for (int k = 0; k < BeltRenderDecisions.TurnFrameCount; k++)
        {
            int ox = (k % 4) * 16;
            int oy = (k / 4) * 16;
            frames[k] = new (byte, byte, byte, byte)[16, 16];
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    frames[k][x, y] = Pixel(strip, ox + x, oy + y);
                }
            }
        }

        // ② u 律：帧 k 带体像素 = PAT16[(u − 2k) mod 16]（2k = k×TurnPxPerFrame）
        for (int k = 0; k < 8; k++)
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    int? u = TurnPathU(x, y);
                    var px = frames[k][x, y];
                    if (u is null)
                    {
                        Assert.Equal(0, px.A); // 带外透明（静态边框独立成层）
                    }
                    else
                    {
                        var expected = Pat16[(u.Value - k * BeltRenderDecisions.TurnPxPerFrame % 16 + 16) % 16];
                        Assert.True(px.A == 255 && (px.R, px.G, px.B) == expected,
                            $"frame{k} ({x},{y}) u={u} 违 u 律");
                    }
                }
            }
        }

        // ③ 帧边界对齐（可直读形式）：竖臂帧 k+1(y) == 帧 k(y+2)；横臂帧 k+1(x) == 帧 k(x−2)
        for (int k = 0; k < 8; k++)
        {
            int next = (k + 1) % 8;
            for (int y = 11; y <= 13; y++)
            {
                for (int x = 5; x <= 10; x++)
                {
                    Assert.Equal(frames[k][x, y + 2], frames[next][x, y]);
                }
            }

            for (int x = 13; x <= 15; x++)
            {
                for (int y = 5; y <= 10; y++)
                {
                    Assert.Equal(frames[k][x - 2, y], frames[next][x, y]);
                }
            }
        }

        // ④ 循环闭合：8 帧×2px=16px=图案周期 → 帧 0 的图案 = 相位 16 的图案
        //（u 律含 (u−16)≡u (mod 16)，帧 8 将逐像素回到帧 0——循环无缝）
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int? u = TurnPathU(x, y);
                if (u is null)
                {
                    continue;
                }

                var atZero = Pat16[(u.Value % 16 + 16) % 16];
                var atSixteen = Pat16[((u.Value - 8 * BeltRenderDecisions.TurnPxPerFrame % 16) % 16 + 16) % 16];
                Assert.Equal(atZero, atSixteen); // 相位 16 ≡ 相位 0
                Assert.Equal(atZero, (frames[0][x, y].R, frames[0][x, y].G, frames[0][x, y].B));
            }
        }

        // ⑤ 八帧两两不同（循环非退化；数组按内容比对）+ 弯口静态边框与帧带体零重叠
        for (int a = 0; a < 8; a++)
        {
            for (int b = a + 1; b < 8; b++)
            {
                Assert.False(SameFrame(frames[a], frames[b]), $"frame{a} 与 frame{b} 逐像素相同（循环退化）");
            }
        }

        var turnEdge = DecodeRgba(Texture("belt", "belt-turn-edge.png"));
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Assert.False(Pixel(turnEdge, x, y).A != 0 && TurnPathU(x, y) is not null,
                    $"belt-turn-edge 与带体重叠 ({x},{y})");
            }
        }
    }

    // ------------------------------------------------ boundary / illegal

    [Fact] // boundary — 单帧边界：帧序在 φ 跨偶数边界时恰 +1（决策层↔素材锁相）
    public void Frame_advance_points_at_the_same_phase_the_strip_encodes()
    {
        // 帧 k 编码相位 2k：FrameIndexFor(φ+origin) 必须选中编码该 φ 的帧
        for (int k = 0; k < BeltRenderDecisions.TurnFrameCount; k++)
        {
            double scrollAtFrameStart = k * BeltRenderDecisions.TurnPxPerFrame;
            Assert.Equal(k, BeltRenderDecisions.FrameIndexFor(scrollAtFrameStart, 0));
            double justBefore = (k + 1) * BeltRenderDecisions.TurnPxPerFrame - 0.01;
            Assert.Equal(k, BeltRenderDecisions.FrameIndexFor(justBefore, 0));
        }
    }

    [Fact] // illegal — 映射缺件必须红（哨兵：对不存在的路径断言存在性失败语义）
    public void Missing_mapping_target_fails_existence_assertion()
    {
        string missing = Texture("enemies", "enemy-does-not-exist.png");

        Assert.False(File.Exists(missing)); // 内容 JSON 无该实体 → 素材树亦无（清零的另一面）
    }
}
