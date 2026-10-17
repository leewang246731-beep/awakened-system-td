using System.IO.Compression;
using System.Text.Json;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-28] [F-133] [F-127] T-142 终章实体→素材映射完整性 + 传送带终章皮肤 8 帧
// 循环像素级断言：era6 内容 JSON 全实体（两塔/八敌+堕落守望者/传送阵载具）必须
// 有对应过校验器的素材（"无实体无素材"清零，28 号 §2/41 号先例）；belt 终章
// 皮肤 =T-119 冻结几何的色板换装（∞ 档），逐像素复验 u 律（帧边界对齐+循环
// 闭合）与 BeltRenderDecisions 冻结常量的跨层契约；特殊形态锚点断言：时空塔
// =星门静态基准帧（星门环+裂缝+涟漪注记；时间涟漪属渲染层）、虚空蜂群=微粒
// 云团（swarm 行形）、堕落守望者=守望者之眼+重力脉冲同心弧（gravity_pulse
// 行形）+顶冠残环（星门母题的堕落形态）。
public class Era6ArtMappingTests
{
    // ---- 仓库相对寻址（RL9：禁绝对路径；Era5ArtMappingTests 同纪律） ----------

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
        var dir = FindRepoDir("game", "presentation", "EraTD", "textures", "era6");
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ContentDir()
    {
        var dir = FindRepoDir("game", "content", "era6");
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

    // ---- PNG 全解码（era6 合成器契约：行滤波全 0、RGBA8） ------------------

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

    private static int OpaqueCount((byte[] Data, int Width) img)
    {
        int count = 0;
        for (int i = 3; i < img.Data.Length; i += 4)
        {
            if (img.Data[i] != 0)
            {
                count++;
            }
        }

        return count;
    }

    // ---- 冻结契约镜像（era6_placeholders.py PALETTE/PAT16/u 律——终章换装色） -

    private static readonly (byte R, byte G, byte B)[] Pat16 =
    {
        (37, 34, 64), (37, 34, 64), (62, 85, 201), (37, 34, 64),         // 0..3 暗物质/星门靛蓝亮条
        (37, 34, 64), (37, 34, 64), (37, 34, 64), (37, 34, 64),          // 4..7
        (58, 55, 92), (58, 55, 92), (38, 55, 150), (58, 55, 92),         // 8..11 基材亮/靛蓝暗条
        (58, 55, 92), (58, 55, 92), (58, 55, 92), (58, 55, 92),          // 12..15
    };

    /// <summary>转弯路径坐标 u（与合成器 turn_path_u 同式：竖臂/弯心/横臂——
    /// E3~E6 皮肤共用同一冻结几何）。返回 null 表示带外。</summary>
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

    [Fact] // legal — 两塔（均 max_tier=1）全部有素材（towers.json tier/max_tier 驱动文件名）
    public void All_era6_towers_have_level_sprites()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ContentDir(), "towers.json")));
        // 画布档（era4/era5 先例：高能发射塔 48，常塔 32）——奇点炮=48（E3 臼炮/
        // E4 激光/E5 轨道炮同档），时空塔=32（星门母题塔）
        var canvasByStem = new Dictionary<string, int>
        {
            ["tower-singularity"] = 48,
            ["tower-spacetime"] = 32,
        };
        int checkedFiles = 0;
        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            string stem = id["era6:".Length..]; // era6:tower-singularity → tower-singularity
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

        Assert.Equal(2, checkedFiles); // 奇点炮 l1 + 时空塔 l1（终章两塔均单档）
    }

    [Fact] // legal — 八敌+堕落守望者 Boss 全部有素材（enemies.json 全行）
    public void All_era6_enemies_and_boss_have_sprites()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ContentDir(), "enemies.json")));
        int regular = 0;
        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            string stem = id["era6:".Length..];
            if (item.GetProperty("sp_id").GetString() == "e6_boss")
            {
                // Boss 画布裁定（28 号 §5 先例重演）：内容 canvas_size_px=96 超校验器
                // 尺寸清单 → 取「常规 Boss 64~96」下限 64；设计案 §13.1「终章
                // Boss 96×112」升格须随 SIZE-ALLOWLIST 扩展走 REV（挂账）。
                // 素材名=堕落守望者（era.json boss 块 name_en=Fallen Watcher）。
                string boss = Texture("enemies", "boss-fallen-watcher.png");
                Assert.True(File.Exists(boss), "缺 Boss 素材：boss-fallen-watcher.png");
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

    [Fact] // legal — 堕落守望者形态锚点：守望者之眼+重力脉冲同心弧+顶冠残环
    // （gravity_pulse 行形+星门母题的堕落形态）
    public void Boss_fallen_watcher_carries_watcher_eye_and_gravity_pulse_arcs()
    {
        var boss = DecodeRgba(Texture("enemies", "boss-fallen-watcher.png"));
        // 守望者之眼：星门靛蓝眼环 + 靛蓝瞳（重力脉冲源）+眼上裂缝（千年记忆）
        Assert.Equal(((byte)62, (byte)85, (byte)201, (byte)255), Pixel(boss, 30, 28));
        Assert.Equal(((byte)122, (byte)140, (byte)224, (byte)255), Pixel(boss, 31, 29));
        Assert.Equal(((byte)222, (byte)244, (byte)248, (byte)255), Pixel(boss, 30, 24));
        // 重力脉冲同心弧（traits.gravity_pulse：900t 周期 3.5 格压制——canonical 双弧注记）
        Assert.Equal(((byte)38, (byte)55, (byte)150, (byte)255), Pixel(boss, 30, 44));
        Assert.Equal(((byte)62, (byte)85, (byte)201, (byte)255), Pixel(boss, 30, 47));
        // 顶冠残环（星门母题——已破裂）+双肩坍缩塔靛蓝顶
        Assert.Equal(((byte)38, (byte)55, (byte)150, (byte)255), Pixel(boss, 32, 16));
        Assert.Equal(((byte)62, (byte)85, (byte)201, (byte)255), Pixel(boss, 13, 22));
    }

    [Fact] // legal — 载具/UI/地形/传送带终章皮肤全部在场（era.json logistics ∞ 档）
    public void Era6_pack_teleporter_ui_tiles_and_belt_skin_exist()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ContentDir(), "era.json")));
        JsonElement era = doc.RootElement.GetProperty("items")[0];
        // 终章物流行：carrier=teleporter（阶梯末位 ∞ 档——throughput=null）
        Assert.Equal("teleporter", era.GetProperty("logistics").GetProperty("carrier").GetString());
        Assert.Equal(JsonValueKind.Null, era.GetProperty("logistics").GetProperty("throughput_items_per_s").ValueKind);
        Assert.True(File.Exists(Texture("units", "teleporter-right.png")));
        Assert.Equal((32, 32), PngSize(Texture("units", "teleporter-right.png")));
        // UI 色板
        Assert.True(File.Exists(Texture("ui", "panel-era6.png")));
        // 地形 9 槽（Era5TerrainAtlas 语义序的终章对位：Plain/代位/Road×2/Wall×2/Rock×2/Water）
        string[] tiles =
        {
            "floor-voidstone", "floor-nebula", "road-lightray", "road-lightray-alt",
            "wall-stargate", "wall-stargate-alt", "floor-rubble-void",
            "floor-rubble-void-alt", "water-voidsea",
        };
        foreach (string tile in tiles)
        {
            Assert.True(File.Exists(Texture("tiles", $"{tile}.png")), $"缺地形槽：{tile}");
            Assert.Equal((16, 16), PngSize(Texture("tiles", $"{tile}.png")));
        }

        // 传送带终章皮肤三层 + 转弯 8 帧条带（T-119 双层动画的色板换装）
        Assert.Equal((16, 16), PngSize(Texture("belt", "belt-body.png")));
        Assert.Equal((16, 16), PngSize(Texture("belt", "belt-edge.png")));
        Assert.Equal((16, 16), PngSize(Texture("belt", "belt-turn-edge.png")));
        Assert.Equal((64, 32), PngSize(Texture("anim", "belt-turn-anim.png")));
    }

    // -------------------------------------------- 特殊形态（任务卡裁定锚点）

    [Fact] // legal — 时空塔=星门静态基准帧：星门环+裂缝+门内深空+涟漪注记落位
    // （时间涟漪属渲染层，决策层不预烘动画帧）
    public void Spacetime_sprite_is_the_static_canonical_gate_form()
    {
        var gate = DecodeRgba(Texture("towers", "tower-spacetime-l1.png"));
        // 星门环上沿（星门靛蓝）+环柱顶（零点白青）+裂缝辉光（门环断口）
        Assert.Equal(((byte)62, (byte)85, (byte)201, (byte)255), Pixel(gate, 16, 5));
        Assert.Equal(((byte)191, (byte)232, (byte)242, (byte)255), Pixel(gate, 8, 5));
        Assert.Equal(((byte)222, (byte)244, (byte)248, (byte)255), Pixel(gate, 19, 5));
        // 门内深空+光尘（(15,9) dust_light）
        Assert.Equal(((byte)232, (byte)238, (byte)252, (byte)255), Pixel(gate, 15, 9));
        // 环门源点（零点白青）+涟漪注记（透镜弧——canonical 相位，无帧序）
        Assert.Equal(((byte)191, (byte)232, (byte)242, (byte)255), Pixel(gate, 16, 16));
        Assert.Equal(((byte)110, (byte)130, (byte)210, (byte)255), Pixel(gate, 5, 9));
        Assert.Equal(((byte)110, (byte)130, (byte)210, (byte)255), Pixel(gate, 27, 13));
        // 守望塔渐收塔身（最终形态）+基座
        Assert.Equal(((byte)58, (byte)55, (byte)92, (byte)255), Pixel(gate, 16, 23));
        Assert.Equal(((byte)62, (byte)85, (byte)201, (byte)255), Pixel(gate, 16, 21)); // 塔身能量涌点
    }

    [Fact] // legal — 虚空蜂群=微粒云团：无实体轮廓（swarm 行形——虚空集集体）
    public void Void_swarm_is_a_mote_cloud_without_a_solid_body()
    {
        var swarm = DecodeRgba(Texture("enemies", "enemy-void-swarm.png"));
        int opaque = OpaqueCount(swarm);
        // 微粒云团：不透明像素占比 <15%（32×32=1024；实心单位 >50%）
        Assert.InRange(opaque, 24, 153);
        // 涡心星门靛蓝在场（集集体核心）+透镜弧闪点在场
        Assert.Equal(((byte)122, (byte)140, (byte)224, (byte)255), Pixel(swarm, 15, 13));
        Assert.Equal(((byte)110, (byte)130, (byte)210, (byte)255), Pixel(swarm, 12, 11));
        // 云团内部留透明间隙（非实心剪影）
        bool hasInteriorGap = false;
        for (int y = 8; y <= 22 && !hasInteriorGap; y++)
        {
            for (int x = 10; x <= 24; x++)
            {
                if (Pixel(swarm, x, y).A == 0)
                {
                    hasInteriorGap = true;
                    break;
                }
            }
        }

        Assert.True(hasInteriorGap, "云团内部无透明间隙（实心剪影违 swarm 行形）");
    }

    [Fact] // legal — 熵蚀幽体死亡爆炸锚点：熵蚀相变腔+滴落裂缝（death_explode 行形）
    public void Entropy_wraith_carries_phase_chamber_and_drip()
    {
        var wraith = DecodeRgba(Texture("enemies", "enemy-entropy-wraith.png"));
        Assert.Equal(((byte)38, (byte)55, (byte)150, (byte)255), Pixel(wraith, 14, 18)); // 坍缩液
        Assert.Equal(((byte)62, (byte)85, (byte)201, (byte)255), Pixel(wraith, 15, 18)); // 液面亮点
        Assert.Equal(((byte)191, (byte)232, (byte)242, (byte)255), Pixel(wraith, 15, 16)); // 腔箍（零点白青警示）
        Assert.Equal(((byte)62, (byte)85, (byte)201, (byte)255), Pixel(wraith, 16, 25)); // 滴落
    }

    [Fact] // legal — 圣盾机甲圣盾锚点：透镜弧场+发射柱（shield_wall 行形）
    public void Aegis_paladin_carries_lens_shield_and_emitter_posts()
    {
        var paladin = DecodeRgba(Texture("enemies", "enemy-aegis-paladin.png"));
        Assert.Equal(((byte)80, (byte)98, (byte)180, (byte)255), Pixel(paladin, 8, 8));   // 力场体（透镜暗）
        Assert.Equal(((byte)150, (byte)170, (byte)235, (byte)255), Pixel(paladin, 13, 10)); // 场内透镜弧
        Assert.Equal(((byte)62, (byte)85, (byte)201, (byte)255), Pixel(paladin, 4, 3));   // 发射柱顶
        Assert.Equal(((byte)38, (byte)55, (byte)150, (byte)255), Pixel(paladin, 4, 10));  // 发射柱体
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

    [Fact] // boundary — 帧序锁相：终章皮肤与 E3~E5 共用 BeltRenderDecisions 的相位律
    public void Frame_advance_points_at_the_same_phase_the_strip_encodes()
    {
        // 帧 k 编码相位 2k：FrameIndexFor(φ+origin) 必须选中编码该 φ 的帧
        //（终章皮肤=同一决策层驱动的色板换装——不另立相位律）
        for (int k = 0; k < BeltRenderDecisions.TurnFrameCount; k++)
        {
            double scrollAtFrameStart = k * BeltRenderDecisions.TurnPxPerFrame;
            Assert.Equal(k, BeltRenderDecisions.FrameIndexFor(scrollAtFrameStart, 0));
            double justBefore = (k + 1) * BeltRenderDecisions.TurnPxPerFrame - 0.01;
            Assert.Equal(k, BeltRenderDecisions.FrameIndexFor(justBefore, 0));
        }
    }

    [Fact] // boundary — ∞ 档皮肤对位：era6 皮肤 stem 的纹理组实测在场（ladder 末位）
    public void Era6_teleporter_gear_skin_files_exist_under_the_era6_stem()
    {
        Assert.True(double.IsPositiveInfinity(BeltRenderDecisions.Era6GearItemsPerSecond));
        Assert.Equal("era6", BeltRenderDecisions.BeltSkinStem(BeltRenderDecisions.Era6GearItemsPerSecond));
        Assert.True(File.Exists(Texture("belt", "belt-body.png")));
        Assert.True(File.Exists(Texture("belt", "belt-edge.png")));
        Assert.True(File.Exists(Texture("belt", "belt-turn-edge.png")));
        Assert.True(File.Exists(Texture("anim", "belt-turn-anim.png")));
    }

    [Fact] // illegal — max_tier=1 的塔不发明升级档：l2 素材不得存在（终章两塔）
    public void Single_tier_towers_have_no_invented_level_two_sprites()
    {
        Assert.False(File.Exists(Texture("towers", "tower-singularity-l2.png")));
        Assert.False(File.Exists(Texture("towers", "tower-spacetime-l2.png")));
    }

    [Fact] // illegal — 映射缺件必须红（哨兵：对不存在的路径断言存在性失败语义）
    public void Missing_mapping_target_fails_existence_assertion()
    {
        string missing = Texture("enemies", "enemy-does-not-exist.png");

        Assert.False(File.Exists(missing)); // 内容 JSON 无该实体 → 素材树亦无（清零的另一面）
    }
}
