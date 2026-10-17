using System.IO.Compression;
using System.Text.Json;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-28] [F-133] [F-127] T-128 E4 实体→素材映射完整性 + 传送带 E4 皮肤 8 帧
// 循环像素级断言：era4 内容 JSON 全实体（四塔×升级位/九敌+夜枭/卡车载具）必须
// 有对应过校验器的素材（"无实体无素材"清零，28 号 §2/41 号先例）；belt E4 皮肤
// =T-119 冻结几何的色板换装（15/s 档），逐像素复验 u 律（帧边界对齐+循环闭合）
// 与 BeltRenderDecisions 冻结常量的跨层契约；特殊形态锚点断言：雷达=静态基准
// 帧（旋转属渲染层 SpriteRotationQuarters，决策层不预烘动画帧）、地雷=埋设
// 半隐（地表线以下没入地面带）。
public class Era4ArtMappingTests
{
    // ---- 仓库相对寻址（RL9：禁绝对路径；Era3ArtMappingTests 同纪律） ----------

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
        var dir = FindRepoDir("game", "presentation", "EraTD", "textures", "era4");
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ContentDir()
    {
        var dir = FindRepoDir("game", "content", "era4");
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

    // ---- PNG 全解码（era4 合成器契约：行滤波全 0、RGBA8） ------------------

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

    // ---- 冻结契约镜像（era4_placeholders.py PALETTE/PAT16/u 律——E4 换装色） -

    private static readonly (byte R, byte G, byte B)[] Pat16 =
    {
        (40, 43, 46), (40, 43, 46), (227, 193, 79), (40, 43, 46),     // 0..3 沥青/警示亮条
        (40, 43, 46), (40, 43, 46), (40, 43, 46), (40, 43, 46),       // 4..7
        (60, 64, 68), (60, 64, 68), (172, 143, 47), (60, 64, 68),     // 8..11 沥青灰/警示暗条
        (60, 64, 68), (60, 64, 68), (60, 64, 68), (60, 64, 68),       // 12..15
    };

    /// <summary>转弯路径坐标 u（与合成器 turn_path_u 同式：竖臂/弯心/横臂——
    /// E3/E4 皮肤共用同一冻结几何）。返回 null 表示带外。</summary>
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

    [Fact] // legal — 四塔×升级位全部有素材（towers.json tier/max_tier 驱动文件名）
    public void All_era4_towers_have_level_sprites()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ContentDir(), "towers.json")));
        // 画布档（E3 先例：重炮座 48，常塔 32）——激光塔=48（高能发射塔，E3 臼炮同档）
        var canvasByStem = new Dictionary<string, int>
        {
            ["tower-missile"] = 32,
            ["tower-laser"] = 48,
            ["tower-radar"] = 32,
            ["tower-mine"] = 32,
        };
        int checkedFiles = 0;
        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            string stem = id["era4:".Length..]; // era4:tower-missile → tower-missile
            int maxTier = item.GetProperty("max_tier").GetInt32();
            int canvas = canvasByStem[stem];
            for (int level = 1; level <= maxTier; level++)
            {
                string path = Texture("towers", $"{stem}-l{level}.png");
                Assert.True(File.Exists(path), $"缺塔素材：{stem}-l{level}.png");
                Assert.Equal(canvas, PngSize(path).Width);
                Assert.Equal(canvas, PngSize(path).Height);
                checkedFiles++;
            }
        }

        Assert.Equal(6, checkedFiles); // 导弹 l1/l2 + 激光 l1/l2 + 雷达 l1 + 地雷 l1
    }

    [Fact] // legal — 九敌+夜枭 Boss 全部有素材（enemies.json 全行）
    public void All_era4_enemies_and_boss_have_sprites()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ContentDir(), "enemies.json")));
        int regular = 0;
        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            string stem = id["era4:".Length..];
            if (item.GetProperty("sp_id").GetString() == "e4_boss")
            {
                // Boss 画布裁定（28 号 §5 先例重演）：内容 canvas_size_px=96 超校验器
                // 尺寸清单 → 取「常规 Boss 64~96」下限 64；升 96 须 SIZE-ALLOWLIST
                // 扩展走 REV。素材名=夜枭（era.json boss 块 name_en=Night Owl）。
                string boss = Texture("enemies", "boss-nightowl.png");
                Assert.True(File.Exists(boss), "缺 Boss 素材：boss-nightowl.png");
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

    [Fact] // legal — 夜枭形态锚点：枭目传感器+空袭弹序+翼尖航行灯（stealth/airstrike 行形）
    public void Boss_nightowl_carries_owl_sensor_eyes_and_airstrike_bombs()
    {
        var owl = DecodeRgba(Texture("enemies", "boss-nightowl.png"));
        // 枭目：双玻璃传感器盘 + 居中警示瞳（锁定光）
        Assert.Equal(((byte)206, (byte)228, (byte)238, (byte)255), Pixel(owl, 26, 25));
        Assert.Equal(((byte)227, (byte)193, (byte)79, (byte)255), Pixel(owl, 26, 26));
        Assert.Equal(((byte)227, (byte)193, (byte)79, (byte)255), Pixel(owl, 36, 26));
        // 空袭弹序（traits.airstrike）：腹内弹舱警示弹
        Assert.Equal(((byte)227, (byte)193, (byte)79, (byte)255), Pixel(owl, 29, 44));
        // 翼尖电网蓝航行灯（本代主色锚）
        Assert.Equal(((byte)61, (byte)126, (byte)166, (byte)255), Pixel(owl, 6, 30));
        Assert.Equal(((byte)61, (byte)126, (byte)166, (byte)255), Pixel(owl, 57, 30));
    }

    [Fact] // legal — 载具/UI/地形/传送带 E4 皮肤全部在场（era.json logistics 15/s 档）
    public void Era4_pack_truck_ui_tiles_and_belt_skin_exist()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ContentDir(), "era.json")));
        JsonElement era = doc.RootElement.GetProperty("items")[0];
        // E4 物流行：carrier=conveyor_belt（第 3 档 15 件/s）+卡车机动补充
        Assert.Equal("conveyor_belt", era.GetProperty("logistics").GetProperty("carrier").GetString());
        Assert.Equal(15.0, era.GetProperty("logistics").GetProperty("throughput_items_per_s").GetDouble());
        Assert.True(era.GetProperty("logistics").GetProperty("truck").GetProperty("available").GetBoolean());
        Assert.True(File.Exists(Texture("units", "truck-right.png")));
        Assert.Equal((32, 32), PngSize(Texture("units", "truck-right.png")));
        // UI 色板
        Assert.True(File.Exists(Texture("ui", "panel-era4.png")));
        // 地形 9 槽（Era3TerrainAtlas 语义序的 E4 对位：Plain/代位/Road×2/Wall×2/Rock×2/Water）
        string[] tiles =
        {
            "floor-concrete", "floor-concrete-grated", "road-asphalt", "road-asphalt-alt",
            "wall-glass-curtain", "wall-glass-curtain-alt", "floor-rubble-concrete",
            "floor-rubble-concrete-alt", "water-modern",
        };
        foreach (string tile in tiles)
        {
            Assert.True(File.Exists(Texture("tiles", $"{tile}.png")), $"缺地形槽：{tile}");
            Assert.Equal((16, 16), PngSize(Texture("tiles", $"{tile}.png")));
        }

        // 传送带 E4 皮肤三层 + 转弯 8 帧条带（T-119 双层动画的色板换装）
        Assert.Equal((16, 16), PngSize(Texture("belt", "belt-body.png")));
        Assert.Equal((16, 16), PngSize(Texture("belt", "belt-edge.png")));
        Assert.Equal((16, 16), PngSize(Texture("belt", "belt-turn-edge.png")));
        Assert.Equal((64, 32), PngSize(Texture("anim", "belt-turn-anim.png")));
    }

    // -------------------------------------------- 特殊形态（任务卡裁定锚点）

    [Fact] // legal — 雷达=静态基准帧：抛物面+馈源+扇扫注记弧落位（旋转属渲染层）
    public void Radar_sprite_is_the_static_canonical_dish_form()
    {
        var radar = DecodeRgba(Texture("towers", "tower-radar-l1.png"));
        // 抛物面 rim（开口朝上 canonical）+碗身+馈源头/杆
        Assert.Equal(((byte)104, (byte)148, (byte)176, (byte)255), Pixel(radar, 10, 6));
        Assert.Equal(((byte)104, (byte)148, (byte)176, (byte)255), Pixel(radar, 16, 6));
        Assert.Equal(((byte)156, (byte)196, (byte)216, (byte)255), Pixel(radar, 11, 7));
        Assert.Equal(((byte)227, (byte)193, (byte)79, (byte)255), Pixel(radar, 16, 2)); // 馈源头
        Assert.Equal(((byte)36, (byte)82, (byte)114, (byte)255), Pixel(radar, 16, 4));  // 馈源杆
        // 静态扇扫注记弧（canonical 相位；旋转=渲染层 SpriteRotationQuarters，无帧序）
        Assert.Equal(((byte)118, (byte)202, (byte)156, (byte)255), Pixel(radar, 23, 4));
        Assert.Equal(((byte)118, (byte)202, (byte)156, (byte)255), Pixel(radar, 27, 12));
        // 播放键母题徽（E4 专属母题，电网蓝）
        Assert.Equal(((byte)61, (byte)126, (byte)166, (byte)255), Pixel(radar, 10, 28));
    }

    [Fact] // legal — 地雷=埋设半隐：地表线 y=21 以上出露穹顶、以下没入地面带
    public void Mine_sprite_depicts_half_buried_placement()
    {
        var mine = DecodeRgba(Texture("towers", "tower-mine-l1.png"));
        // 出露部：感应穹顶+警示接触环+触发须尖端
        Assert.Equal(((byte)154, (byte)154, (byte)148, (byte)255), Pixel(mine, 16, 17)); // 穹顶
        Assert.Equal(((byte)227, (byte)193, (byte)79, (byte)255), Pixel(mine, 15, 20));  // 接触环
        Assert.Equal(((byte)240, (byte)218, (byte)128, (byte)255), Pixel(mine, 12, 8));  // 须尖
        // 埋设部：地表线以下=地面带全覆盖（埋设床不透出）
        for (int y = 21; y <= 30; y++)
        {
            for (int x = 2; x <= 29; x++)
            {
                Assert.True(Pixel(mine, x, y).A == 255, $"地面带缺口 ({x},{y})");
            }
        }

        Assert.Equal(((byte)60, (byte)64, (byte)68, (byte)255), Pixel(mine, 16, 26));   // 埋设床
        Assert.Equal(((byte)40, (byte)43, (byte)46, (byte)255), Pixel(mine, 16, 22));   // 埋设体暗影
        // 半隐另一面：穹顶以上留透明缘（非通高实心方块——埋没的下半不可见）
        int transparentAbove = 0;
        for (int y = 0; y <= 10; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                if (Pixel(mine, x, y).A == 0)
                {
                    transparentAbove++;
                }
            }
        }

        Assert.True(transparentAbove > 200, $"穹顶以上透明像素 {transparentAbove}（应留大段透明缘）");
    }

    // ------------------------------------------- 传送带双层契约（F-127 核心）

    [Fact] // legal — 直段双层纹理：带体 16×16 沿 V 可平铺（周期 16）+带边静态护栏
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
                    Assert.Equal(0, px.A); // 带外全透明（双层拆分：护栏在静态层）
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
                        Assert.Equal(0, px.A); // 带外透明（静态护栏独立成层）
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

        // ④ 循环闭合：8 帧×2px=16px=图案周期 → 相位 16 ≡ 相位 0（循环无缝）
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

        // ⑤ 八帧两两不同（循环非退化）+ 弯口静态护栏与帧带体零重叠
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

    [Fact] // boundary — 帧序锁相：E4 皮肤与 E3 共用 BeltRenderDecisions 的相位律
    public void Frame_advance_points_at_the_same_phase_the_strip_encodes()
    {
        // 帧 k 编码相位 2k：FrameIndexFor(φ+origin) 必须选中编码该 φ 的帧
        //（E4 皮肤=同一决策层驱动的色板换装——不另立相位律）
        for (int k = 0; k < BeltRenderDecisions.TurnFrameCount; k++)
        {
            double scrollAtFrameStart = k * BeltRenderDecisions.TurnPxPerFrame;
            Assert.Equal(k, BeltRenderDecisions.FrameIndexFor(scrollAtFrameStart, 0));
            double justBefore = (k + 1) * BeltRenderDecisions.TurnPxPerFrame - 0.01;
            Assert.Equal(k, BeltRenderDecisions.FrameIndexFor(justBefore, 0));
        }
    }

    [Fact] // illegal — max_tier=1 的塔不发明升级档：l2 素材不得存在（雷达/地雷）
    public void Single_tier_towers_have_no_invented_level_two_sprites()
    {
        Assert.False(File.Exists(Texture("towers", "tower-radar-l2.png")));
        Assert.False(File.Exists(Texture("towers", "tower-mine-l2.png")));
    }

    [Fact] // illegal — 映射缺件必须红（哨兵：对不存在的路径断言存在性失败语义）
    public void Missing_mapping_target_fails_existence_assertion()
    {
        string missing = Texture("enemies", "enemy-does-not-exist.png");

        Assert.False(File.Exists(missing)); // 内容 JSON 无该实体 → 素材树亦无（清零的另一面）
    }
}
