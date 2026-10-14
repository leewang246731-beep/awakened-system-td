# 18 - R1 素材技术研究：图片/视频插入 Godot 4 + 素材生产管线

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 研究代理 | R1（多源网络交叉研究） |
| 目的 | 为《我在游戏觉醒系统》（像素塔防+觉醒系统叙事）二次开发提供图片/视频素材接入与生产管线的技术依据 |
| 关联文档 | 14-art-license-audit.md（美术管线/许可证台账）；15-t045-art-integration.md（E1 地形接入、Nearest 2× 先例）；17-ark-video-api-research.md（Seedance 视频生成 API） |
| 可信度标注 | 沿用 17 号口径：【官方】= 官方文档/源码原文；【多源】= 官方+第三方交叉一致；【单源】= 仅第三方，落地前需自测 |

---

## 0. 速查结论表

| # | 问题 | 结论 | 可信度 |
|---|---|---|---|
| C1 | Godot 4 内置视频格式 | **仅 Ogg Theora（.ogv）+ 可选 Vorbis 音轨**。WebM 在 4.0 被移除（"too buggy"），H.264/H.265 因专利排除，AV1 因 CPU 解码慢未进核心。**已核到 4.7（2026-06-18 stable，"Lights, Camera, Action!"）：发版说明零视频编解码改动，Theora 现状延续** | 【官方】docs Playing videos + 4.7 release notes |
| C2 | mp4→ogv 转码 | ffmpeg `-q:v 6 -q:a 6 -g:v 64`（q 1-10 越大越好，6=平衡）；需 libtheora/libvorbis 构建；ogv 压缩效率差，**体积明显大于同画质 mp4**；先出高质量中间片再二压，勿用 Blender 内置 Theora 导出 | 【官方】 |
| C3 | 透明视频 | **Theora 无 alpha 通道**。官方给的是色键（chroma key）canvas_item shader 拆透明（pickup_range+fade）。游戏内"立绘动态背景"若要透明显露场景，只能色键或改用序列帧 | 【官方】 |
| C4 | 解码性能 | Theora **纯 CPU 解码，无 GPU 硬解**。官方建议：桌面 ≤1080p/30fps（高端机可 1440p@60）；**移动/Web ≤720p/30fps**；Web 端"因缺少架构专属汇编优化而性能很差" | 【官方】 |
| C5 | 第三方视频方案 | GDExtension 路线：**EIRTeam.FFmpeg**（Godot 4.1+，Linux/macOS 官方支持，Windows 弱）、**GDE GoZen**（Voylin，Linux/Windows/Android/macOS）。均为社区维护、非生产级加固；老 godot-videodecoder 是 Godot 3 GDNative，不适用 | 【多源】 |
| C6 | 运行时外部图片加载 | `Image.load_from_file()`（静态）→ `ImageTexture.create_from_image()`；res:// 导入资源走 `ResourceLoader.load_threaded_request`。外部路径 `load()` 不可用 | 【官方】Runtime file loading |
| C7 | 运行时外部视频加载 | `VideoStreamTheora.new()` → `stream.file = "user://…/x.ogv"`（`set_file`），播放器直接 FileAccess 读原文件，**无需导入步骤**；`load()` 同样不可用。**无 `load_from_buffer`**（提案 #11375 未实现） | 【官方】Runtime file loading 教程含此例 |
| C8 | 视频播放能力边界 | 不支持 URL 流播放、不支持 seek、不支持变速；音频仅单声道/立体声（5.1/7.1 混缩成立体声）；循环播放需把项目设置 **Video Delay Compensation=0** 否则每次循环起点丢帧 | 【官方】 |
| C9 | AnimatedTexture 替代视频 | 官方警告 **>2048×2048 不适用、全帧常驻内存**；换场景后帧变白 bug（#79070）。仅适合小尺寸短循环（UI 特效、8-16 帧 breathing 立绘底）；大动态背景用视频或 AnimatedSprite2D+SpriteFrames | 【官方+多源】 |
| C10 | 像素风保持 | 本项目已有全局 `default_texture_filter=0`（Nearest）+ shader `filter_nearest`（15 号报告）；运行时 ImageTexture **不受导入器影响**，过滤由 CanvasItem 决定 → 全局 Nearest 对外部加载同样生效，无需额外处理 | 【官方+项目实况】 |
| C11 | 剧情视频不打断心流 | 先例三件套：王国保卫战=关卡间短地图动画+文字引入；PvZ=疯狂戴夫纯文本对话（点击推进、乱码配音）；明日方舟=VN 演出+跳过+3x 自动播放（社区仍抱怨长度/误触跳过）。通用准则：**永远可跳过、重看可回看、检查点与玩法之间不得强制播片** | 【多源】 |

---

## 1. Godot 4 视频支持现状（研究点 1）

### 1.1 格式支持矩阵

| 格式 | Godot 4 核心 | 说明 |
|---|---|---|
| Ogg Theora (.ogv) | ✅ 唯一 | `VideoStreamTheora`，CPU 解码 |
| WebM (VP9) | ❌ 4.0 移除 | Godot 3 有 `VideoStreamWebm`，4.x 无 |
| MP4/H.264/H.265 | ❌ 专利 | 只能靠 GDExtension |
| AV1 | ❌ 未进核心 | 免版税但 CPU 软解慢、硬解未普及 |
| .ogg 扩展名的 Theora | ⚠️ | 需改名为 .ogv 引擎才识别 |

4.7 核实（2026-10 实访 release notes）：主题"Lights, Camera, Action!"指渲染/HDR（AreaLight3D、HDR 输出、DrawableTexture2D、nearest-neighbor viewport scaling 等），**没有任何视频编解码/VideoStreamPlayer 变更**；与过场相关的仅有 `Tween.tween_await()`（对话/过场编排便利）与 Android 画中画。=> 本报告结论对 4.3~4.7 通用。

### 1.2 mp4→ogv 转码命令（官方口径 + 补充）

```bash
# 官方标准命令（保持原分辨率）
ffmpeg -i input.mp4 -q:v 6 -q:a 6 -g:v 64 output.ogv

# 官方降 720p 再转（移动/网页目标）
ffmpeg -i input.mp4 -vf "scale=-1:720" -q:v 6 -q:a 6 -g:v 64 output.ogv

# 显式编码器写法（社区等价形式，便于查错）
ffmpeg -i input.mp4 -c:v libtheora -q:v 10 -c:a libvorbis -q:a 6 output.ogv
```

参数权衡（官方 Playing videos 教程）：

| 参数 | 范围 | 建议 |
|---|---|---|
| `-q:v` 视频质量 | 1-10 | 6=平衡；1440p/4K 源可用 5（因为高分压出来的瑕疵不明显） |
| `-q:a` 音频质量 | -1~10 | 6=可用；9≈感知无损但解码 CPU 升高（负载下可能爆音） |
| `-g:v` GOP | 默认 12 太低 | **64~512**；恰过 2 的幂（如 65）seek 最大等待时间陡增 |
| 比特模式 | — | **VBR 优于 CBR** |

官方质量纪律：
- ffmpeg 需带 libtheora+libvorbis（`ffmpeg -version` 的 configuration 行确认）；
- **先渲染高码率/无损中间片（如高码率 H.264）再二压 Theora**，别从有损低码率源直转；
- 不用 Blender 内置 Theora 导出（质量低）；
- 官方还提示旧版 Ogg/Theora muxer 有 bug，建议较新构建；Reddit 有 Windows 64 位构建偶发 Theora 花屏的报告（r/godot 1i0lvyb）——**批量转码后必须逐条目检（抽帧目检）**。
- 体积预期：Theora 是 2000 年代编码器，**同画质 ogv 通常比 mp4(H.264) 大 1.5~3×**【多源】。720p/8s 片段 -q:v 6 大致 4~8 MB 量级，入库预算按此放大。

### 1.3 透明视频：不支持，需色键

官方教程未提供 alpha 方案，直接给出 chroma key shader 示例（canvas_item shader 挂在 VideoStreamPlayer 上）：

```glsl
shader_type canvas_item;
uniform vec3 chroma_key_color : source_color = vec3(0.0, 1.0, 0.0);
uniform float pickup_range : hint_range(0.0, 1.0) = 0.1;
uniform float fade_amount : hint_range(0.0, 1.0) = 0.1;

void fragment() {
    vec4 color = texture(TEXTURE, UV);
    float dist = distance(color.rgb, chroma_key_color);
    float alpha = clamp((dist - pickup_range) / max(fade_amount, 0.0001), 0.0, 1.0);
    if (alpha <= 0.0) discard;
    COLOR = vec4(color.rgb, alpha * color.a);
}
```

结论：**凡是要"叠在战斗画面上的透明动态立绘"，一律不用视频**，改用 AnimatedSprite2D/序列帧（见 §2.4）。视频只做满屏/信箱矩形画面。对比：Bink 时代（RAD Game Tools，Epic 旗下）专门有逐帧 alpha 平面做"视频精灵"，那是商用中间件的能力，Godot 内置不提供【官方对比 radgametools.com】。

### 1.4 GDExtension 第三方方案（备选，不推荐首发）

| 方案 | 平台 | 状态 |
|---|---|---|
| EIRTeam.FFmpeg（GDExtension，Godot 4.1+） | Linux/macOS 官方；Windows 社区 WIP | FFmpeg 软解，音频走 Godot 总线；活跃但非官方 |
| GDE GoZen（Voylin） | Linux/Windows/Android/macOS | itch.io 分发，覆盖最广 |
| godot-videodecoder (Blooym) | Godot 3 GDNative | **4.x 不可用** |

判断：本项目首发 PC 单机、视频量小（觉醒系统过场），**转 ogv + 内置播放器是零依赖最稳路线**；GDExtension 只在"必须 H.264 高压缩率"或"必须透明视频"时再评估，且要接受平台矩阵/维护风险。

### 1.5 平台限制

| 平台 | 限制 |
|---|---|
| 桌面 | ≤1080p/30fps 稳；高端 CPU 可 1440p@60 |
| 移动 | ≤720p/30fps，低端机再降 |
| **Web** | 官方明示性能差（缺汇编优化）；无 URL 流、无 seek；社区做法是 JS 桥接 HTML5 `<video>` 盖在 canvas 上，或干脆 Web 版砍视频 |
| 全平台 | 音频仅 mono/stereo；循环需 Video Delay Compensation=0 |

---

## 2. 视频在游戏里的用法模式（研究点 2）

### 2.1 模式对照表（结合本项目）

| 模式 | 先例 | Godot 4 实现 | 本项目适用 |
|---|---|---|---|
| 开场/章节过场（全屏） | 王国保卫战章节引入、传统 Bink 开场 | VideoStreamPlayer + AspectRatioContainer(Ratio=16.0/9) + Expand | ✅ 觉醒系统章节过场（15-30s） |
| 一闪式系统觉醒演出（3-8s） | 明日方舟技能/剧情闪光 | 全屏 ogv 或 **AnimationPlayer+TextureRect 序列**（更省） | ✅ 首选序列帧；视频仅当镜头感必需 |
| 半屏/信箱 NPC 剧情 | RPG 常见信箱上下黑边 | Control 定尺寸 + VideoStreamPlayer(Expand off) | ✅ 觉醒提示插入 |
| 背景循环视频 | Bink 时代游戏内循环（Bink 官方"fast enough for in-game"） | ogv 循环：`loop=true` + 项目设置 Video Delay Compensation=0；CPU 解码占用需预算 | ⚠️ 塔防战斗中禁用（抢模拟线程），仅主菜单/章节选择页允许 |
| 对话立绘动态背景 | 手游通用 | **AnimatedTexture（小图）或 AnimatedSprite2D+SpriteFrames**，不用视频（无 alpha+内存） | ✅ 立绘 breathing 用 4-8 帧 |

### 2.2 过场场景骨架（GDScript）

```gdscript
# cutscene_player.gd — 全屏过场 + 任意键跳过 + finished 信号收尾
extends Control

@export var video_path: String            # "res://video/era1-awakening.ogv"
@export var skippable: bool = true

@onready var vp: VideoStreamPlayer = $AspectRatioContainer/VideoStreamPlayer

func _ready() -> void:
    vp.finished.connect(_on_finished)
    vp.play()                              # 或场景里勾 autoplay_enabled

func _unhandled_input(event: InputEvent) -> void:
    if skippable and event.is_pressed():
        _finish()

func _on_finished() -> void:
    _finish()

func _finish() -> void:
    vp.stop()
    queue_free()                           # 或发信号让 CutsceneManager 切下一幕
```

要点：
- **跳过必须挂在 `_unhandled_input`**（不吞 UI 输入），且过场控制器要把游戏模拟 pause 掉（`get_tree().paused = true` + 过场节点 `process_mode = PROCESS_MODE_WHEN_PAUSED`）。
- 循环背景视频：`vp.loop = true` + 项目设置 `video/delay_compensation_ms=0`（非 0 会在每次循环起点因音画重同步丢帧）【官方】。
- 3D 表面播放（如塔身屏幕）官方路径：SubViewport(Expand off)+PlaneMesh/QuadMesh+ViewportTexture Albedo+Unshaded——本项目纯 2D 暂不需要。

### 2.3 性能坑清单（视频）

1. **CPU 解码线程**：Theora 在主线程外的解码仍吃 CPU；塔防 Wave 期间模拟+渲染负载高，**战斗内播视频必掉帧**。铁律：视频只在非战斗态（过场/主菜单）出现。
2. **内存**：整文件加载（无流式），720p×30s ogv 全量驻内存；过场结束立即 `vp.stream = null` 释放（引用计数回收）。
3. **分辨率上限**：按目标平台压（桌面 720p 足够——本项目像素风，480p 也可接受，见 §4.5 风格统一转码）。
4. **Web 导出**：直接判死——Web 版构建提供"静帧+文字"降级路线。

### 2.4 序列帧替代视频的实践（对话立绘/微演出）

| 方案 | 上限 | 适用 |
|---|---|---|
| AnimatedTexture | 帧数少、**≤2048×2048**、全帧常驻内存、#79070 换场景白帧 bug | 小尺寸 UI 特效、≤8 帧 256² 立绘 breathing |
| AnimatedSprite2D + SpriteFrames | 帧图走正常导入管线，可用 atlas | 立绘动态、觉醒粒子演出（推荐） |
| AnimationPlayer + TextureRect keyframe | 换 texture 关键帧 | 3-5 张 CG 轮播"伪视频"（成本最低） |

像素风对话立绘推荐：**1 张静态立绘 + shader 呼吸/浮动（sin 位移 + 顶点微缩放）**，零序列帧成本；进阶 4 帧 breathing 只在主角/系统化身上做。

---

## 3. 图片素材动态加载（研究点 3）

### 3.1 三条加载路径对比

| 路径 | API | 适用 | 备注 |
|---|---|---|---|
| res:// 导入资源 | `ResourceLoader.load_threaded_request/get`（C# `ResourceLoader.LoadThreadedRequest`） | 随包素材（战斗 tile/塔/敌） | 后台线程解码，主线程只取结果；`load_threaded_get` 过早调用会卡主线程 |
| 运行时外部图片 | `Image.load_from_file(path)` → `ImageTexture.create_from_image(img)` | user://、绝对路径（U 盘 DLC 包） | **静态方法**；对非 res:// 的 `load()` 无效 |
| 字节缓冲 | `FileAccess.get_file_as_bytes` → `Image.load_png_from_buffer/load_jpg_from_buffer` | 需错误处理/加密包 | `load_from_file` 失败静默返回空——生产代码走 buffer 版才能拿到 Error |

### 3.2 C# 加载骨架（本项目主语言）

```csharp
// RuntimeAssetLoader.cs — 外部像素图加载 + Nearest + 缓存
using System.Collections.Generic;
using Godot;
using Godot.Collections;

public static class RuntimeAssetLoader
{
    private static readonly Dictionary<string, ImageTexture> _cache = new();

    /// 加载外部 PNG（绝对路径或 user://）。失败返回 null。
    public static ImageTexture LoadPixelTexture(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;

        // 生产路径：FileAccess + buffer（可拿到错误码）；load_from_file 失败是静默的
        byte[] bytes = FileAccess.GetFileAsBytes(path);
        if (bytes == null || bytes.Length == 0) return null;   // Error 未尽细分
        var img = new Image();
        Error err = img.LoadPngFromBuffer(bytes);
        if (err != Error.Ok) return null;

        var tex = ImageTexture.CreateFromImage(img);
        _cache[path] = tex;
        return tex;
    }

    /// 扫描素材包目录（U 盘/本地 DLC 式），返回 kebab 命名的 png 清单
    public static System.Collections.Generic.List<string> ScanPack(string dirPath)
    {
        var result = new System.Collections.Generic.List<string>();
        if (!DirAccess.DirExistsAbsolute(dirPath)) return result;
        foreach (var f in DirAccess.GetFilesAt(dirPath))
            if (f.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase))
                result.Add(f);
        return result;
    }
}

// 用法（Nearest 过滤由 CanvasItem 决定，项目全局 default_texture_filter=0 已覆盖）：
// var tex = RuntimeAssetLoader.LoadPixelTexture("E:/game-assets/era1/towers/era1-watchtower-idle.png");
// sprite2D.Texture = tex;   // 无需再设 Filter，全局 Nearest 生效
```

外部视频（ogv）运行时加载：

```csharp
var stream = new VideoStreamTheora();
stream.File = "user://dlc/video/era2-awakening.ogv";   // set_file；res:// 或 user://
videoStreamPlayer.Stream = stream;
videoStreamPlayer.Play();
```

### 3.3 GDScript 等价骨架

```gdscript
extends Node
# 运行时图片（外部）
var img := Image.load_from_file("E:/game-assets/era1/towers/era1-watchtower-idle.png")
if img: $Sprite2D.texture = ImageTexture.create_from_image(img)

# 运行时视频（外部 ogv）
var vs := VideoStreamTheora.new()
vs.file = "user://dlc/era2-awakening.ogv"
$VideoStreamPlayer.stream = vs
$VideoStreamPlayer.play()

# 资源包目录扫描（DLC 式素材包）
var dir := DirAccess.open("E:/game-assets/era1/towers")
if dir:
    for f in dir.get_files():
        if f.ends_with(".png"):
            _register(f, Image.load_from_file("E:/game-assets/era1/towers/".path_join(f)))
```

### 3.4 DLC 式素材包设计要点

1. **包根放 manifest.json**（见 §4.2），加载器只信 manifest 不信目录枚举（枚举仅作校验"声明的文件都存在"）。
2. user:// 优先：PC 上把 `E:/game-assets` 视作"作者侧源库"，**玩家侧包运行时拷到 `user://packs/<pack_id>/`** 再加载——导出版对绝对路径无沙箱限制（桌面），但 user:// 是官方推荐的运行时可写位置【官方】。
3. 逐项校验后再生效：尺寸白名单（16/32/48/64，对齐 15 号报告校验器口径）、调色板数、命名 kebab——外部包复用 `asset_validator` 同规则（T-046 门禁外延）。
4. 大图（章节 banner 1024² 级）加载走后台线程：`ResourceLoader.load_threaded_request` 仅适用 res://；外部图用 `WorkerThreadPool.add_task` 包 `Image.load_from_file`（Image 解码本身线程安全，主线程只做 `ImageTexture.create_from_image` 上传）。
5. 像素缩进：外部 16×16 图进 32px 世界格，沿用既有 shader `filter_nearest` 2× 整数放大（15 号 §3 先例），**不要在图片侧预放大**（保持源 16×16 入库纪律）。

---

## 4. 素材管线规范（研究点 4）

### 4.1 命名与目录

**命名**：`{era}-{subject}-{state|variant}.{ext}`，全 kebab-case（与 15 号 FILENAME-KEBAB 门禁一致）。

- 图：`era1-watchtower-idle.png`、`era1-watchtower-attack.png`、`era1-awakening-glow.png`
- 视频：`era1-awakening-01.mp4`（母片）→ `era1-awakening-01.ogv`（游戏内）
- 规则：小写、连字符分词、状态后缀枚举固定（idle/attack/death/banner/awaken/frame-XX）。

**目录**（目标态，作者侧源库）：

```
E:\game-assets\
  {era}\                     # era1 / era2 / …（对齐游戏六时代分期）
    {category}\              # towers / enemies / terrain / characters / ui / video
      raw\                   # AI 母片/生成原档（mp4、大 png）+ 逐素材 manifest
      dist\                  # 过校验的游戏成品（16/32px png、.ogv）
  manifest.json              # 总清单（可由脚本聚合）
  _licenses\                 # 第三方包许可证快照（对应 docs/assets/licenses 双备份）
```

现状（2026-10-13 实查）：`E:\game-assets\` 已有 `era-banners/`、`characters/`、`scenes/`（类目优先、era 打进文件名，如 `era-banners/era1-banner-dawn.png`）。**迁移建议**：新增素材按 `{era}/{category}/` 放置；存量三个类目待量增后再搬迁，搬迁时 manifest 记录 `moved_from` 保持可追溯。理由：era 数量（6）> 类目数量，era 优先树更耐膨胀；且与游戏章节内容组织（`game/content/`）同构。

### 4.2 manifest.json 元数据 schema

```json
{
  "schema": "game-assets/1",
  "updated_at": "2026-10-13T00:00:00+08:00",
  "entries": [
    {
      "id": "era1-awakening-01",
      "type": "video",
      "era": "era1",
      "category": "video",
      "files": {
        "raw": "era1/video/raw/era1-awakening-01.mp4",
        "dist": "era1/video/dist/era1-awakening-01.ogv",
        "raw_sha256": "…（入库时计算）"
      },
      "source": {
        "kind": "ai-generated",
        "generator": "doubao-seedance-2-5-260628",
        "task_id": "cgt-20260414114820-xxxxx",
        "prompt": "像素风望楼塔在黎明中亮起系统符文…",
        "generated_at": "2026-10-13",
        "resolution": "720p", "duration_s": 8, "fps": 24
      },
      "transcode": {
        "cmd": "ffmpeg -i raw -vf scale=-1:720 -q:v 6 -q:a 6 -g:v 64 dist.ogv",
        "tool": "ffmpeg (libtheora/libvorbis)", "date": "2026-10-13"
      },
      "license": "AI 生成；成品仅作过场播放（AI PNG 不进 presentation 的红线不适用于视频母片→ogv 过场，但商店物料仍按 F-147 标注）",
      "usage": { "scene": "awakening", "trigger": "era1 首次觉醒", "skippable": true,
                 "fallback_web": "era1/video/dist/era1-awakening-01-key.png" }
    },
    {
      "id": "era1-watchtower-idle",
      "type": "image",
      "era": "era1", "category": "towers",
      "files": { "dist": "era1/towers/dist/era1-watchtower-idle.png" },
      "source": { "kind": "human-pixel-overdraw", "reference": "docs/assets/concept-E1-watchtower.png",
                  "generator": null, "palette_colors": 9, "size": "16x16" },
      "license": "自绘（原创）"
    }
  ]
}
```

字段纪律（对齐 14 号台账三条红线）：
- **AI 生成 PNG 依旧不进 `presentation/`**——`E:\game-assets\{era}/…/raw/` 等价于 `docs/assets/` 的"重绘底稿区"，成品必须人工像素重绘后过校验器；
- 视频母片（AI 生成 mp4）→ ogv 过场属于"动态演出素材"，允许直用（它不进 sprite 管线、无网格对齐/超色板问题），但 **prompt/模型/task_id 必须登记**（复现与审计）；
- 第三方 CC0 素材入 E 盘时，`_licenses/` 与 `docs/assets/licenses/` 双份快照。

### 4.3 从生图到 sprite 的既有结论（重申 + 外延）

既有结论（14 号 §2、15 号）：AI 概念图（docs/assets/concept-*.png）→ 人工像素重绘 → `game/assets/`（第三方 CC0 包）或 `presentation/EraTD/textures/`（自绘子集）→ asset_validator（SIZE-ALLOWLIST/PALETTE-COUNT/FILENAME-KEBAB）→ 渲染端 Nearest 2×。

外延（本报告新增，针对 E 盘作者库）：

```
AI 生图（Seedance/图模型）
  → E:\game-assets\{era}\{cat}\raw\*.png|mp4        （母片+manifest 登记）
  → [图片线] 人工像素重绘（16/32px、≤64 色、kebab）
  → dist\ 过校验（尺寸/色板/命名） → 拷入 game 仓库 presentation\ 或运行时从 E:/user:// 加载
  → [视频线] ffmpeg 转 ogv（§1.2 命令） → dist\*.ogv → res:// 或 user:// 播放
```

### 4.4 视频转码批处理脚本设计（设计稿，交付后续任务实现）

`tools/video/transcode-ogv.sh`（bash，融入既有 run_all.sh 生态；Windows 侧 Git Bash 执行）：

```bash
#!/usr/bin/env bash
# 用法: transcode-ogv.sh [--era era1] [--q 6] [--dry]
# 扫描 E:/game-assets/{era}/video/raw/*.mp4 → dist/*.ogv，并回写 manifest.json
set -euo pipefail
ROOT="${GAME_ASSETS_ROOT:-/e/game-assets}"
QV="${QV:-6}"; QA="${QA:-6}"; GOP=64

for raw in "$ROOT"/${ERA:-*}/video/raw/*.mp4; do
  [[ -e "$raw" ]] || continue
  era_dir=$(dirname "$(dirname "$raw")"); dist="$era_dir/video/dist"; mkdir -p "$dist"
  out="$dist/$(basename "${raw%.mp4}").ogv"
  # 统一 720p/24fps（Seedance 默认 24fps，显式钉死防漂移）
  cmd=(ffmpeg -y -i "$raw" -vf "scale=-1:720" -r 24 -q:v "$QV" -q:a "$QA" -g:v "$GOP" "$out")
  echo "${cmd[*]}"; [[ "${DRY:-0}" == 1 ]] || "${cmd[@]}"
  # 抽 3 帧目检（防 64 位构建 Theora 花屏——见 §1.2 风险）
  for t in 0.5 2 5; do
    ffmpeg -y -ss "$t" -i "$out" -frames:v 1 "$dist/.check-$(basename "${raw%.mp4}")-$t.png" || true
  done
  # manifest 回写由 tools/video/update_manifest.py（读 ffprobe duration/分辨率 + sha256）
done
```

配套 `update_manifest.py` 职责：ffprobe 读元数据 → 计算 sha256 → upsert `entries[]` → 保持 schema 校验。验收口径：dist 无 mp4 残留、manifest 条目与 dist 文件 1:1、抽检帧通过目检。

### 4.5 像素风格统一转码（可选进阶，标注【单源-派生】）

AI 视频与像素塔防的画面风格落差大。派生做法（社区像素化滤镜通用套路，非官方文档）：

```bash
# 先最近邻缩到低分辨率再放大回 720p，得到像素块质感
ffmpeg -i in.mp4 -vf "scale=240:135:flags=neighbor,scale=1280:720:flags=neighbor,format=yuv420p" \
       -r 24 -q:v 7 -q:a 6 -g:v 64 out.ogv
```

建议：觉醒系统演出**保留全清晰视频**（系统=超然于像素世界的叙事对比，风格反差即表达）；仅"游戏内世界"的插入画面用像素化滤镜。此为叙事决策，留待设计裁定。

---

## 5. 叙事节奏：剧情视频不打断心流的先例（研究点 5）

### 5.1 三个先例的解剖

| 游戏 | 做法 | 数据点 | 启示 |
|---|---|---|---|
| **王国保卫战**（Ironhide） | 关卡开始前**文字引导**（写为"给玩家的指令"）；世界地图上的**短动画过场**（逆向工程显示是文本+动画编排脚本，非预渲染视频） | 地图过场=秒级镜头移动+对话气泡 | TD 的叙事主载体是**地图层轻演出+开局势诱文本**，不是播片；重度过场留给章节级（每章 1 次） |
| **植物大战僵尸**（George Fan） | 疯狂戴夫**纯文本对话**插在关卡间；配音是乱码嘟哝（可读文本+喜剧音效分离）；对话点击推进；伏笔结构（5-5 埋 5-10 绑架） | 每段对话 3~6 句、10~20s 可点完 | **文本+点击推进是最不打断心流的叙事容器**；"声音模糊化"省配音成本又不失温度；GDC 2012 十条教学建议=叙事兼教学 |
| **明日方舟**（鹰角） | 剧情关=VN 演出（立绘+文本+少量动效）；有跳过、自动播放；后加 **3x 自动播放速度**；社区仍有"剧情太长读得累""点击快进误触整段跳过"的持续抱怨；存在第三方网页剧情阅读器（玩家在游戏外读剧情的行为信号） | 单段剧情可长达数分钟 | **长叙事在 TD/RPG 里必须有：跳过（按住式防误触）、倍速、已读检测、回看入口**；觉醒系统叙事要控制单段密度 |

### 5.2 通用设计准则（多源交叉：Unity/GD 社区讨论、RPG Maker 论坛、玩家抱怨帖）

1. **永远提供跳过**；最被诟病的是"检查点死亡重来之间夹不可跳过播片"。
2. **跳过与快进分离输入**：单击=下一句/快进，按住/双击=跳过整段（明日方舟教训）。
3. **首看锁、重看放**：关键剧情（觉醒首触发）可软锁 3~8s 后出现跳过键，重触发直接可跳。
4. **回看入口**：图鉴/章节页提供"重播过场"（Godot 实现：CutsceneManager 记录已看标记到存档，重播=重放 ogv）。
5. 播片可兼作**载入遮罩**，但不可反噬（载入完必须能跳）。

### 5.3 本项目时长建议

| 演出类型 | 时长 | 载体 | 跳过 |
|---|---|---|---|
| 觉醒系统一闪（单次觉醒触发） | **3~8s** | 序列帧/AnimationPlayer 优先，ogv 仅镜头感必需 | 2s 后可点 |
| 章节过场（era 切换） | **15~30s** | ogv 全屏（Seedance 2.5 单段 ≤30s 恰好覆盖，见 17 号 §2） | 首看 5s 锁，之后立即可跳+可回看 |
| NPC/系统对话 | 单段 ≤20s 文本 | 文本+立绘（PvZ 模式） | 点击推进+按住跳过 |
| 主菜单背景循环 | 15~30s loop | ogv 循环（Delay Comp=0）或 shader 动效 | N/A（非阻断） |

预算耦合（17 号 §4.2）：章节过场 30s×720p ≈ ¥45/段（Seedance 2.5）；一闪 4~8s×480p ≈ ¥3~5/段。一闪类**优先序列帧**（一次生成为静态资产零复购），视频额度集中给每章 1 条过场。

---

## 6. 落地建议汇总（给后续任务卡）

1. **视频编解码**：全 ogv（Theora/Vorbis），ffmpeg `-q:v 6 -q:a 6 -g:v 64`，720p/24fps 钉死；转码后抽帧目检。
2. **视频场景**：仅非战斗态（章节过场/主菜单循环/觉醒全屏演出）；战斗内零视频。
3. **透明动态元素**：禁视频，AnimatedSprite2D/静态立绘+呼吸 shader。
4. **图片加载**：包内素材 ResourceLoader 线程化；玩家外部包 user:// + FileAccess buffer（可错误处理）；复用 asset_validator 校验外部包。
5. **管线**：`E:\game-assets\{era}\{category}\{raw|dist}` + manifest.json（schema §4.2）+ tools/video/transcode-ogv.sh（§4.4）；AI PNG 红线延续 14 号。
6. **叙事**：3-8s 一闪（序列帧）/15-30s 章节过场（ogv）；跳过=按住式+已读即解锁+图鉴回看。

## 7. 三个最大坑（一句话版）

1. **Theora 无透明+纯 CPU 解码+压缩差**：透明立绘用视频做不到（要色键 shader 绕），战斗中播必掉帧，ogv 比 mp4 大 1.5~3×。
2. **Web 导出视频基本不可用**：性能差+无流+无 seek，Web 版必须备静帧/文字降级路线。
3. **运行时加载 API 陷阱**：外部（非导入）文件 `load()` 一律失效——图片必须 `Image.load_from_file`/buffer，视频必须 `VideoStreamTheora.set_file`；且 `load_from_file` 失败静默、AnimatedTexture 超 2048²/全帧驻内存+白帧 bug。

---

## 8. 来源链接表

### 官方（Godot / ffmpeg 相关）

| 内容 | URL |
|---|---|
| Playing videos（格式/ffmpeg 命令/平台上限/色键/循环） | https://docs.godotengine.org/en/stable/tutorials/animation/playing_videos.html |
| VideoStreamPlayer 类（Web 性能警告） | https://docs.godotengine.org/en/stable/classes/class_videostreamplayer.html |
| VideoStreamTheora 类（.ogv、CPU 解码） | https://docs.godotengine.org/en/stable/classes/class_videostreamtheora.html |
| VideoStream 类（file 属性/set_file） | https://docs.godotengine.org/en/stable/classes/class_videostream.html |
| Runtime file loading and saving（外部图/视频加载官方示例） | https://docs.godotengine.org/en/stable/tutorials/io/runtime_file_loading_and_saving.html |
| Image 类（load_from_file 静态语义） | https://docs.godotengine.org/en/stable/classes/class_image.html |
| AnimatedTexture（2048 警告/内存） | https://docs.godotengine.org/en/stable/classes/class_animatedtexture.html |
| ResourceLoader（load_threaded_*） | https://docs.godotengine.org/en/stable/classes/class_resourceloader.html |
| Godot 4.7 release notes（无视频变更核实） | https://godotengine.org/releases/4.7 |
| 4.7 beta 1 公告 | https://godotengine.org/article/dev-snapshot-godot-4-7-beta-1 |
| VideoStreamTheora 源码（无静态加载器核实） | https://github.com/godotengine/godot/blob/master/modules/theora/video_stream_theora.h |
| godot-proposals #9669（Theora 落后/换码讨论） | https://github.com/godotengine/godot-proposals/discussions/9669 |
| godot-proposals #11375（load_from_buffer 仅提案） | https://github.com/godotengine/godot-proposals/issues/11375 |
| godot-proposals #924（无 URL 流播放） | https://github.com/godotengine/godot-proposals/issues/924 |
| issue #88226（load_from_file 导出后失败先例） | https://github.com/godotengine/godot/issues/88226 |
| issue #79070（AnimatedTexture 白帧） | https://github.com/godotengine/godot/issues/79070 |

### 第三方（多源/单源交叉）

| 内容 | URL |
|---|---|
| r/godot mp4→ogv 花屏讨论 | https://www.reddit.com/r/godot/comments/1i0lvyb/mp4_to_ogv_via_ffmpeg_constant_artifacting |
| r/godot ogv 获取教程 | https://www.reddit.com/r/godot/comments/1cc5ntq/tutorial_how_to_get_video_in_ogg_theora_format |
| Godot Forums ogv/webm（4.x 现状） | https://godotforums.org/d/35840/ogv-and-webm-support-in-godot-4 |
| Video.SE libtheora 高质量命令 | https://video.stackexchange.com/questions/30373/ |
| EIRTeam.FFmpeg（GDExtension 视频解码） | https://github.com/EIRTeam/EIRTeam.FFmpeg |
| EIRTeam 发布帖（r/godot） | https://www.reddit.com/r/godot/comments/15bae49/eirteamffmpeg_gdextension_plugin_that_adds_video |
| GDE GoZen（跨平台视频播放插件） | https://voylin.itch.io/gde-gozen-video-playback-addon-for-godot |
| r/godot 4 Web 导出质量讨论 | https://www.reddit.com/r/godot/comments/1cjw057/why_is_godot_4_so_much_worse_at_webhtml_games |
| Forum 运行时设纹理 | https://forum.godotengine.org/t/how-to-import-an-image-file-and-set-it-as-a-texture-while-in-game/12356 |
| Forum res:// 外纹理加载 | https://forum.godotengine.org/t/load-textures-at-runtime-from-outside-the-res-file-system/3495 |
| Forum AnimatedTexture 讨论 | https://godotforums.org/d/23404-animatedtexture-why-multiple-textures |
| Bink Video（游戏内视频/alpha 先例） | https://www.radgametools.com/bnkmain.htm |
| Bink alpha 平面 | https://www.radgametools.com/binkalpha.htm |
| Bink Wikipedia | https://en.wikipedia.org/wiki/Bink_Video |
| Kingdom Rush 战役设计分析（Game Developer） | https://www.gamedeveloper.com/design/kingdom-rush---the-wonderful-campaign-level-design |
| Kingdom Rush 地图过场逆向 | https://relentlessoptimizer.com/gaming/2022/02/16/optimizing-kingdom-rush |
| Kingdom Rush Wiki（关卡引入文本） | https://kingdomrushtd.fandom.com/wiki/Kingdom_Rush |
| PvZ 对话全记录 | https://plantsvszombies.fandom.com/wiki/Plants_vs._Zombies/Dialogue |
| Crazy Dave 页（乱码配音/伏笔） | https://plantsvszombies.fandom.com/wiki/Crazy_Dave |
| George Fan GDC 2012 十条教学建议 | https://www.gamedeveloper.com/design/gdc-2012-10-tutorial-tips-from-i-plants-vs-zombies-i-creator-george-fan |
| GDC Vault：How I Got My Mom to Play PvZ | https://www.gdcvault.com/play/1015541/How-I-Got-My-Mom |
| 明日方舟叙事呈现社区讨论 | https://www.reddit.com/r/arknights/comments/ybd0gg/i_love_arknights_story_but_actually_reading_it |
| 明日方舟 CN 3x 自动播放维护公告 | https://x.com/AK_CN_Shitpost/status/2030937909898211823 |
| Arknights Story Reader（第三方） | https://akgcc.github.io/story |
| VN 跳过/快进惯例（Lemma Soft） | https://lemmasoft.renai.us/forums/viewtopic.php?t=2114-style |
| 最长不可跳过过场（玩家情绪样本） | https://www.reddit.com/r/gaming/comments/1osxa3x/what_is_the_longest_unskippable_cutscene_in |
| Unity 论坛：放弃部分可跳的实现权衡 | https://discussions.unity.com/t/ive-given-up-on-allowing-player-to-skip-certain-cutscenes/739274 |

> 注：本项目内部依据（Kenney 2× Nearest 先例、AI 图红线、Seedance 时长/价格）引自仓库内 14/15/17 号报告，未重复外链。
