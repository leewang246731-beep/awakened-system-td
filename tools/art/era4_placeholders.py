#!/usr/bin/env python3
"""
era4_placeholders.py — E4 占位美术程序合成器（任务 T-128，Wave 7，E 泳道，组件 C-28）

用途：为 E4（现代时代）确定性合成占位像素图——四塔×升级位（导弹/激光/雷达/
地雷，era4/towers.json）、九敌+夜枭 Boss（era4/enemies.json）、卡车载具
（era.json logistics.truck）与传送带 E4 皮肤（logistics 第 3 档=15 件/s：
belt 纹理色板换装，复用 T-119 双层动画全套几何契约）、E4 面板色板（C-22 换肤
底稿）、现代地形子集（混凝土/公路/玻璃幕墙 9 槽）。
背景：28 号冻结清单 §6 待做 #8 后半（E4 美术包=T-128）与 §5 Boss 画布裁定
先例重演（内容表 canvas_size_px=96 → 取 SIZE-ALLOWLIST 合规下限 64）；E4 无
CC0 成套素材源（Kenney 三包均前代主题），按 T-078 预批准回退路线+T-119 全量
程序合成先例，100% 走「代码合成像素图」。
红线对齐：这是**程序绘制**（确定性：固定坐标+固定 18 色现代子板，无随机数、
无 AI 生图），与 21 号 §2.1「AI PNG 不进 presentation/游戏 sprite 管线」红线
不冲突；产物可由本脚本逐字节复现，人工重绘升级时以本脚本为底稿。

视觉身份（31 号 §1.2 E4 行）：主色电网蓝 #3D7EA6 / 辅混凝土灰 #9A9A94、
警示黄 #E3C14F；母题形状=混凝土雷达塔（浇筑塔身+抛物面天线+玻璃幕墙）；
纹理语言=混凝土浇筑纹、电路走线、玻璃反光；专属母题=**播放键三角+雷达扇扫**
（三角化播放键，扫描扇形掠过——母题几何链「三角=远古矛尖→古典山墙→工业
桁架→现代播放键」第 4 环）。Cinematics A-04（E3→E4 跃迁，铁塔浇筑成混凝土
雷达塔，煤黑让位电网蓝）为 E4 帧参考。

特殊形态裁定（任务卡口径）：
  雷达=旋转——决策层静态底稿（本包供 canonical 朝向单帧：抛物面开口朝上+
  静态扇扫注记弧），旋转属渲染层（SpriteRotationQuarters 同律，T-119 传送带
  四向=渲染层旋转先例）；升动画帧须走 REV（28 号 §6 待做 #3 同款挂账）。
  地雷=埋设半隐——上半部（感应穹顶+触发须）出露于地表线，下半部没入地面带
  （埋设体轮廓以暗色隐现），对应 era4:tower-mine「雷场布设」行形。

传送带 E4 皮肤（15/s 档，era.json logistics tier3=conveyor_belt 15 件/s）：
  纹理色板换装，几何与帧律逐字节复用 T-119 冻结契约（41 号 §4 三方镜像表，
  与 game/src/EraCore/Presentation/BeltRenderDecisions.cs 常量一致，C# 侧
  Era4ArtMappingTests 逐像素复验）：
    直线段 = shaders/belt-scroll.gdshader UV 滚动——本包供其两张 16px 纹理：
      belt/belt-body.png   动画层（带体表面，沿行进方向周期 16px，可无缝平铺）
      belt/belt-edge.png   静态层（带边护栏+警示柱钉，不随 UV 滚动）
    转弯段 = anim/belt-turn-anim.png 8 帧 tile 动画（4×2 网格 16px 帧，画布
      64×32；帧序=行优先 row*4+col）
      + belt/belt-turn-edge.png 静态弯口边框（与帧叠加，双层渲染）。
    帧边界对齐契约（与 E3 皮肤完全同几何，仅色板换装）：
      转弯路径坐标 u（像素沿带心线，南入北行东出）：
        竖臂 x∈[5,10],y∈[11,15]: u=15-y；弯心 x∈[5,10],y∈[5,10]:
        u=5+(x-5)+(10-y)；横臂 x∈[11,15],y∈[5,10]: u=15+(x-10)。全程
        u∈[0,20]→转弯步长 21px。
      第 k 帧带体色 = PAT16[(u − 2k) mod 16]（图案向下游流动；每帧前进 2px，
      8 帧×2px=16px=图案周期 → 循环无缝闭合）；直段步长 16px/格。
      PAT16（周期 16，E4 现代换装）：沥青胎面×2 档 + 警示黄防滑条×2
      （idx 2 亮/idx 10 暗）——E3 煤黑×黄铜同构异色。
    带速（15/s 档）由决策层 scroll 相位量驱动（ShaderOffsetPx），纹理仅为
    图案相位源——速度不烙进纹理（同一纹理可服务任意带速档）。

输出（--out DIR，默认 presentation/EraTD/textures/era4/）：
  towers/tower-missile-l1.png / -l2.png            32×32 导弹发射箱塔
  towers/tower-laser-l1.png / -l2.png              48×48 混凝土激光尖塔
  towers/tower-radar-l1.png                        32×32 混凝土雷达塔（max1 单档）
  towers/tower-mine-l1.png                         32×32 埋设地雷（max1 单档）
  enemies/enemy-militia.png … 8 种                 32×32 常规敌
  enemies/boss-nightowl.png                        64×64 夜枭 Boss（28 §5 裁定下限）
  units/truck-right.png                            32×32 卡车（E4 载具，现代色板）
  ui/panel-era4.png                                32×32 面板色板
  tiles/floor-concrete / -grated / road-asphalt{,-alt} /
        wall-glass-curtain{,-alt} / floor-rubble-concrete{,-alt} /
        water-modern                              16×16 地形 9 槽
  belt/belt-body.png / belt-edge.png /
        belt-turn-edge.png                        16×16 传送带双层纹理（E4 皮肤）
  anim/belt-turn-anim.png                          64×32 转弯 8 帧（4×2 网格）

全部输出须过 tools/ci/asset_validator.py（尺寸 16/32/48/64 白名单、≤64 色、
kebab 命名）。--self-test 另做四类强校验：①asset_validator 全目录；②转弯 8 帧
帧边界对齐（u 律+双臂逐像素平移+循环闭合+八帧两两不同+带边零重叠）；③静态层/
动画层透明区互斥（双层叠加契约）；④确定性（二次生成 sha256 逐字节全等）。

依赖：仅 Python 3.8+ 标准库（struct/zlib/argparse/hashlib/tempfile）——与
asset_validator 同纪律，不引 Pillow。CLI：python era4_placeholders.py
[--out DIR] [--self-test]
"""

from __future__ import annotations

import argparse
import hashlib
import os
import struct
import sys
import tempfile
import zlib

# ---------------------------------------------------------------- E4 现代色板
# 18 色（≤64 master 上限，逐文件口径）：电网蓝 3（31 号主色 #3D7EA6 居中）/
# 混凝土灰 3（辅 #9A9A94）/ 警示黄 3（辅 #E3C14F）/ 沥青黑 3（公路·车胎·
# 机械暗部——E3 煤黑对位）/ 玻璃 3（幕墙反光·座舱·雷达罩）/ 电路绿 3（走线·
# 指示灯——E4 纹理语言第三件）
PALETTE = {
    "grid_light": (117, 168, 199),
    "grid_main": (61, 126, 166),     # 31 号 E4 主色 电网蓝 #3D7EA6
    "grid_dark": (36, 82, 114),
    "concrete_light": (214, 213, 207),
    "concrete_main": (154, 154, 148),  # 31 号 E4 辅色 混凝土灰 #9A9A94
    "concrete_dark": (108, 108, 103),
    "warn_light": (240, 218, 128),
    "warn_main": (227, 193, 79),      # 31 号 E4 辅色 警示黄 #E3C14F
    "warn_dark": (172, 143, 47),
    "asphalt_light": (88, 92, 96),
    "asphalt_mid": (60, 64, 68),
    "asphalt_dark": (40, 43, 46),
    "glass_light": (206, 228, 238),
    "glass_mid": (156, 196, 216),
    "glass_dark": (104, 148, 176),
    "circuit_light": (118, 202, 156),
    "circuit_mid": (62, 160, 108),
    "circuit_dark": (32, 108, 74),
}

# ------------------------------------------------ 传送带几何契约（F-127 核心）
# 与 game/src/EraCore/Presentation/BeltRenderDecisions.cs 的冻结常量一一对应
# （T-119 冻结契约原样复用——E4 皮肤只换色板不动几何）：
BODY_TILE_PX = 16        # 带体源 tile 边长（32px 世界格 2× 整数放大，同地形先例）
TURN_FRAMES = 8          # 转弯帧数（14 §3「转弯 8 帧双层 tile 动画」）
PX_PER_FRAME = 2         # 每帧图案前进 px（8 帧×2=16=图案周期 → 无缝循环）
STRAIGHT_PATH_PX = 16    # 直段每格路径长（=源 tile 高，可无缝平铺）
TURN_PATH_PX = 21        # 转弯路径 u∈[0,20] 共 21px（u 律见模块 docstring）
TURN_FRAMES_PER_ROW = 4  # 动画条带 4×2 网格（横条 8 帧为 128px 宽超尺寸白名单）

# 带体表面图案（周期 16，E4 现代换装）：沥青胎面两档 + 警示黄防滑条
# （idx2 亮/idx10 暗）——E3 煤黑×黄铜同构异色
PAT16 = (
    "asphalt_dark", "asphalt_dark", "warn_main", "asphalt_dark", "asphalt_dark",
    "asphalt_dark", "asphalt_dark", "asphalt_dark", "asphalt_mid", "asphalt_mid",
    "warn_dark", "asphalt_mid", "asphalt_mid", "asphalt_mid", "asphalt_mid",
    "asphalt_mid",
)


def turn_path_u(x, y):
    """转弯 tile 路径坐标 u（南入北行东出的规范右弯）；带外返回 None。
    与 era3_placeholders.turn_path_u 逐式相同（冻结契约：几何不随皮肤变）。"""
    if 5 <= x <= 10 and 11 <= y <= 15:        # 竖臂（自南缘上行）
        return 15 - y
    if 5 <= x <= 10 and 5 <= y <= 10:         # 弯心方块（对角灯丝）
        return 5 + (x - 5) + (10 - y)
    if 11 <= x <= 15 and 5 <= y <= 10:        # 横臂（东出）
        return 15 + (x - 10)
    return None


class Canvas:
    """最小像素画布：RGBA 网格，初始全透明 (0,0,0,0)。与 era2/era3 合成器
    同款（PNG 写出逐字节确定：zlib level 9、无辅助块、行滤波全 0）。"""

    def __init__(self, width, height):
        self.width = width
        self.height = height
        self.px = [[(0, 0, 0, 0)] * width for _ in range(height)]

    def set(self, x, y, color):
        if 0 <= x < self.width and 0 <= y < self.height:
            self.px[y][x] = (color[0], color[1], color[2], 255)

    def set_rgba(self, x, y, rgba):
        if 0 <= x < self.width and 0 <= y < self.height:
            self.px[y][x] = rgba

    def get(self, x, y):
        return self.px[y][x]

    def fill(self, x, y, w, h, color):
        for yy in range(y, y + h):
            for xx in range(x, x + w):
                self.set(xx, yy, color)

    def rect(self, x, y, w, h, fill_color, edge_color=None):
        if fill_color is not None:
            self.fill(x, y, w, h, fill_color)
        if edge_color is not None:
            self.hline(x, y, w, edge_color)
            self.hline(x, y + h - 1, w, edge_color)
            self.vline(x, y, h, edge_color)
            self.vline(x + w - 1, y, h, edge_color)

    def hline(self, x, y, w, color):
        if w <= 0:
            return
        for xx in range(x, x + w):
            self.set(xx, y, color)

    def vline(self, x, y, h, color):
        if h <= 0:
            return
        for yy in range(y, y + h):
            self.set(x, yy, color)

    def blit(self, other, ox, oy):
        for y in range(other.height):
            for x in range(other.width):
                if 0 <= oy + y < self.height and 0 <= ox + x < self.width:
                    self.px[oy + y][ox + x] = other.px[y][x]

    def opaque(self):
        """不透明像素坐标集合（双层叠加契约断言用）。"""
        return {(x, y) for y in range(self.height)
                for x in range(self.width) if self.px[y][x][3] != 0}

    def write(self, path):
        raw = bytearray()
        for row in self.px:
            raw.append(0)  # PNG 行滤波 type 0（C# 测试解码契约：全 0 滤波）
            for (r, g, b, a) in row:
                raw += bytes((r, g, b, a))

        def chunk(ctype, data):
            return (struct.pack(">I", len(data)) + ctype + data
                    + struct.pack(">I", zlib.crc32(ctype + data) & 0xFFFFFFFF))

        png = b"\x89PNG\r\n\x1a\n"
        png += chunk(b"IHDR", struct.pack(">IIBBBBB", self.width, self.height, 8, 6, 0, 0, 0))
        png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        png += chunk(b"IEND", b"")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(png)


# ---------------------------------------------------------------- 现代部件库

def play_triangle(c, x, y, h=5, key="grid_main"):
    """播放键三角（E4 专属母题，31 号 §1.2）：指向右的实心三角。
    x,y=左边中点；h=高（奇数）；宽=h//2+1（右顶点=母题指向）。"""
    half = h // 2
    for dy in range(-half, half + 1):
        w = half - abs(dy) + 1
        c.hline(x, y + dy, w, PALETTE[key])


def stud(c, x, y, key="warn_main"):
    """柱钉：单像素警示点（E4 纹理语言基准件——现代紧固/标识点）。"""
    c.set(x, y, PALETTE[key])


def concrete_slab(c, x, y, w, h, base="concrete_main", light="concrete_light",
                  dark="concrete_dark", pour=True):
    """浇筑混凝土板：左亮右暗+顶亮底暗+水平浇筑缝+四角系筋孔
    （混凝土浇筑纹=E4 纹理语言首件）。"""
    c.fill(x, y, w, h, PALETTE[base])
    c.vline(x, y, h, PALETTE[light])
    c.vline(x + w - 1, y, h, PALETTE[dark])
    c.hline(x, y, w, PALETTE[light])
    c.hline(x, y + h - 1, w, PALETTE[dark])
    if pour and h >= 9:
        for py in range(y + 4, y + h - 3, 5):
            c.hline(x + 1, py, w - 2, PALETTE[dark])  # 浇筑缝
    if w >= 6 and h >= 6:
        for (sx, sy) in ((x + 1, y + 1), (x + w - 2, y + 1),
                         (x + 1, y + h - 2), (x + w - 2, y + h - 2)):
            c.set(sx, sy, PALETTE[dark])  # 模板系筋孔


def circuit(c, x, y, w, key="circuit_mid"):
    """电路走线（E4 纹理语言第三件）：水平走线+两端焊盘点。"""
    c.hline(x, y, w, PALETTE[key])
    c.set(x, y, PALETTE["circuit_light"])
    c.set(x + w - 1, y, PALETTE["circuit_light"])


def hazard_band(c, x, y, w, h=3):
    """警示黄斜纹带（现代工业标识；E4 升级位标记基准件）：沥青底+警示斜纹。"""
    c.fill(x, y, w, h, PALETTE["asphalt_dark"])
    for xx in range(x, x + w):
        for dy in range(h):
            if (xx + dy) % 4 < 2:
                c.set(xx, y + dy, PALETTE["warn_main"])


def glass_panel(c, x, y, w, h, frame="grid_dark"):
    """玻璃幕墙板（玻璃反光=E4 纹理语言第二件）：玻璃体+框+对角高光。"""
    c.fill(x, y, w, h, PALETTE["glass_mid"])
    c.rect(x, y, w, h, None, PALETTE[frame])
    for i in range(1, min(w, h) - 1):  # 对角反光（固定 45° 掠面）
        if x + i < x + w - 1 and y + i < y + h - 1:
            c.set(x + i, y + i, PALETTE["glass_light"])


def radar_dish(c, cx, top=3):
    """抛物面天线（E4 母题塔顶件）：canonical 开口朝上（旋转=渲染层注记）。
    cx=抛物面轴线 x；top= rim 行。馈源杆+警示馈源头居焦。"""
    rim_w = 13
    x0 = cx - rim_w // 2
    c.hline(x0, top, rim_w, PALETTE["glass_dark"])          # 开口 rim
    for i in range(1, 6):                                    # 抛物面两侧弧
        inset = (i + 1) // 2
        c.set(x0 + inset, top + i, PALETTE["glass_mid"])
        c.set(x0 + rim_w - 1 - inset, top + i, PALETTE["glass_mid"])
    for i in range(2, 6):                                    # 右半内面受光（反光）
        inset = (i + 1) // 2
        c.set(x0 + rim_w - 2 - inset, top + i, PALETTE["glass_light"])
    c.hline(x0 + 4, top + 5, rim_w - 8, PALETTE["glass_dark"])  # 碗底
    c.vline(cx, top - 3, 3, PALETTE["grid_dark"])            # 馈源杆
    c.fill(cx - 1, top - 4, 3, 1, PALETTE["warn_main"])      # 馈源头（焦点）


# ---------------------------------------------------------------- 四塔（era4/towers.json）

def tower_missile(level):
    """导弹塔（era4:tower-missile）32×32：混凝土基座+箱式发射器（双管/四管
    L2）；splash 溅射档辨识=集束发射箱+警示管口环。L2=侧挂辅助发射舱+炮塔
    警示带（E4 升级位=警示黄标记，E2 金/E3 黄铜同律）。"""
    c = Canvas(32, 32)
    concrete_slab(c, 6, 22, 20, 9)                       # 浇筑基座
    play_triangle(c, 9, 26, 5)                           # 播放键母题徽（E4 专属）
    circuit(c, 15, 26, 8)                                # 基座电路走线
    stud(c, 24, 26)
    c.fill(11, 17, 10, 5, PALETTE["asphalt_mid"])        # 炮塔环
    c.hline(11, 17, 10, PALETTE["asphalt_light"])
    c.hline(11, 21, 10, PALETTE["asphalt_dark"])
    c.fill(10, 4, 12, 13, PALETTE["asphalt_mid"])        # 发射箱体
    c.vline(10, 4, 13, PALETTE["asphalt_light"])
    c.vline(21, 4, 13, PALETTE["asphalt_dark"])
    c.hline(10, 4, 12, PALETTE["asphalt_light"])
    for tx in (12, 17):                                  # 双发射管（L1）
        c.fill(tx, 5, 3, 10, PALETTE["grid_dark"])
        c.hline(tx, 5, 3, PALETTE["warn_main"])          # 警示管口环
        c.hline(tx, 14, 3, PALETTE["warn_dark"])
    c.set(13, 5, PALETTE["warn_light"])
    c.set(18, 5, PALETTE["warn_light"])
    if level >= 2:
        for px in (6, 23):                               # 侧挂辅助发射舱
            c.fill(px, 7, 3, 8, PALETTE["grid_dark"])
            c.hline(px, 7, 3, PALETTE["warn_main"])
        hazard_band(c, 11, 19, 10, 2)                    # 炮塔警示带（L2 标识）
        c.set(13, 16, PALETTE["warn_light"])
        c.set(18, 16, PALETTE["warn_light"])
    return c


def tower_laser(level):
    """激光塔（era4:tower-laser）48×48：浇筑混凝土塔身+玻璃幕墙亮带+塔顶棱
    镜发射头+电路脊；energy 穿甲档辨识=棱镜聚能头+无弹道直射。L2=双侧警示
    电容组+塔腰警示带+双脊电路。"""
    c = Canvas(48, 48)
    concrete_slab(c, 10, 36, 28, 10)                     # 浇筑基座
    play_triangle(c, 14, 40, 5)
    circuit(c, 22, 41, 12)
    concrete_slab(c, 18, 12, 12, 24, pour=True)          # 塔身（浇筑缝+系筋孔）
    glass_panel(c, 26, 13, 3, 22)                        # 幕墙亮带（反光，中列受光）
    c.vline(19, 14, 19, PALETTE["circuit_dark"])         # 电路脊
    for py in (14, 19, 24, 29, 33):
        c.set(19, py, PALETTE["circuit_light"])          # 焊盘
    play_triangle(c, 22, 22, 7, "grid_main")             # 塔身母题徽
    c.fill(16, 6, 16, 6, PALETTE["grid_main"])           # 发射头壳体
    c.hline(16, 6, 16, PALETTE["grid_light"])
    c.fill(21, 2, 6, 4, PALETTE["glass_light"])          # 棱镜聚能仓
    c.vline(20, 2, 5, PALETTE["grid_dark"])
    c.vline(27, 2, 5, PALETTE["grid_dark"])
    c.hline(21, 2, 6, PALETTE["warn_main"])
    c.set(23, 1, PALETTE["warn_light"])                  # 出射光芯（贴口沿）
    c.set(24, 1, PALETTE["warn_light"])
    if level >= 2:
        c.fill(12, 24, 4, 8, PALETTE["warn_main"])       # 双侧警示电容组
        c.fill(32, 24, 4, 8, PALETTE["warn_main"])
        c.hline(12, 24, 4, PALETTE["warn_light"])
        c.hline(32, 24, 4, PALETTE["warn_light"])
        hazard_band(c, 16, 33, 16, 3)                    # 塔腰警示带（L2 标识）
        c.hline(20, 27, 8, PALETTE["circuit_mid"])       # 双脊电路分支
        c.hline(20, 30, 8, PALETTE["circuit_mid"])
        c.set(28, 27, PALETTE["circuit_light"])
        c.set(28, 30, PALETTE["circuit_light"])
    return c


def tower_radar(level):
    """雷达（era4:tower-radar）32×32（max1 单档）：混凝土雷达塔=本代母题塔
    （浇筑塔身+抛物面天线）；支援档辨识=无炮管+馈源天线。**静态底稿裁定**：
    canonical=抛物面开口朝上+静态扇扫注记弧（右上一象限 circuit 虚点弧）；
    旋转属渲染层（SpriteRotationQuarters，T-119 传送带四向先例）——决策层
    静态单帧，不预烘动画帧。"""
    assert level == 1  # 支援设施不升级（内容表 max_tier=1；越级调用=合成器契约违例）
    c = Canvas(32, 32)
    concrete_slab(c, 7, 25, 18, 6)                       # 浇筑基座
    play_triangle(c, 9, 28, 5)                           # 播放键母题徽
    stud(c, 22, 28)
    c.fill(13, 13, 6, 12, PALETTE["concrete_main"])      # 浇筑塔身（渐收）
    c.vline(13, 13, 12, PALETTE["concrete_light"])
    c.vline(18, 13, 12, PALETTE["concrete_dark"])
    for py in (18, 23):
        c.hline(13, py, 6, PALETTE["concrete_dark"])     # 浇筑缝
    c.fill(11, 12, 10, 2, PALETTE["asphalt_mid"])        # 天线平台
    c.hline(11, 12, 10, PALETTE["asphalt_light"])
    radar_dish(c, 16, top=6)                             # 抛物面天线（canonical 朝上）
    for (sx, sy) in ((23, 4), (25, 6), (26, 8), (27, 10), (27, 12)):
        c.set(sx, sy, PALETTE["circuit_light"])          # 静态扇扫注记弧（渲染层旋转）
    return c


def tower_mine(level):
    """地雷（era4:tower-mine）32×32（max1 单档）：**埋设半隐**——地表线 y=21
    以上出露感应穹顶+触发须+LED，以下没入地面带（埋设体轮廓暗色隐现）；
    splash 面封锁档辨识=穹顶压感壳+警示接触环。"""
    assert level == 1  # 利基行不升级（内容表 max_tier=1；越级调用=合成器契约违例）
    c = Canvas(32, 32)
    c.fill(2, 21, 28, 10, PALETTE["asphalt_mid"])        # 地面带（埋设床）
    c.hline(2, 21, 28, PALETTE["asphalt_dark"])          # 地表线
    c.vline(2, 22, 9, PALETTE["asphalt_dark"])
    c.vline(29, 22, 9, PALETTE["asphalt_dark"])
    for (gx, gy) in ((5, 24), (12, 27), (19, 24), (26, 27), (8, 29), (23, 29)):
        c.set(gx, gy, PALETTE["concrete_dark"])          # 地面碎石
    rows = (  # 感应穹顶（半椭球，出露上半）
        (20, 10, 12), (19, 10, 12), (18, 10, 12), (17, 10, 12),
        (16, 11, 10), (15, 11, 10), (14, 11, 10),
        (13, 12, 8), (12, 13, 6), (11, 15, 4),
    )
    for (yy, x0, w) in rows:
        c.fill(x0, yy, w, 1, PALETTE["concrete_main"])
    c.vline(10, 14, 7, PALETTE["concrete_light"])        # 穹顶明暗缘
    c.vline(21, 14, 7, PALETTE["concrete_dark"])
    c.hline(10, 20, 12, PALETTE["warn_main"])            # 警示接触环（地表交圈）
    c.set(10, 20, PALETTE["warn_dark"])
    c.set(21, 20, PALETTE["warn_dark"])
    c.set(16, 15, PALETTE["circuit_light"])              # 感应 LED
    c.set(15, 15, PALETTE["circuit_dark"])
    c.set(17, 15, PALETTE["circuit_dark"])
    for (ax, ay) in ((12, 9), (19, 9)):                  # 触发须
        c.vline(ax, ay, 4, PALETTE["grid_dark"])
        c.set(ax, ay - 1, PALETTE["warn_light"])
    c.fill(11, 22, 10, 3, PALETTE["asphalt_dark"])       # 埋设体轮廓（半隐）
    c.set(11, 22, PALETTE["concrete_dark"])
    c.set(20, 22, PALETTE["concrete_dark"])
    return c


# ---------------------------------------------------------------- 九敌+Boss（era4/enemies.json）

def _shadow(c, x, y, w):
    """脚下接地阴影（1px 沥青横带）。"""
    c.hline(x, y, w, PALETTE["asphalt_mid"])


def _legs(c, xs, y, h=5, key="asphalt_dark"):
    for lx in xs:
        c.fill(lx, y, 2, h, PALETTE[key])


def enemy_militia():
    """武装民兵（era4:enemy-militia）32×32：便帽+工装+步枪（swarm 辨识：
    无甲轻装成群——民兵速成动员）。"""
    c = Canvas(32, 32)
    _shadow(c, 11, 29, 11)
    c.fill(13, 7, 5, 3, PALETTE["concrete_light"])  # 面
    c.set(17, 8, PALETTE["asphalt_dark"])  # 眼
    c.fill(12, 5, 7, 2, PALETTE["asphalt_mid"])  # 便帽
    c.hline(12, 5, 7, PALETTE["asphalt_light"])
    c.fill(12, 10, 8, 7, PALETTE["concrete_main"])  # 工装躯干
    c.hline(12, 10, 8, PALETTE["concrete_light"])
    c.vline(16, 11, 5, PALETTE["asphalt_mid"])  # 背带
    c.fill(12, 15, 8, 2, PALETTE["asphalt_mid"])  # 战术腰封
    _legs(c, (13, 17), 17)
    c.fill(21, 12, 6, 2, PALETTE["asphalt_dark"])  # 步枪
    c.set(27, 12, PALETTE["grid_dark"])  # 枪口
    c.set(20, 13, PALETTE["asphalt_mid"])  # 枪托
    c.set(24, 14, PALETTE["warn_dark"])  # 弹匣位
    return c


def enemy_assault_trooper():
    """突击兵（era4:enemy-assault-trooper）32×32：头盔+护目镜+防插板携行具
    （armor=12 中坚辨识：全盔+板材背心+制式步枪）。"""
    c = Canvas(32, 32)
    _shadow(c, 9, 29, 15)
    c.fill(13, 7, 5, 3, PALETTE["concrete_light"])
    c.set(17, 8, PALETTE["asphalt_dark"])
    c.fill(12, 3, 7, 3, PALETTE["asphalt_mid"])  # 头盔
    c.hline(12, 3, 7, PALETTE["asphalt_light"])
    c.fill(12, 5, 7, 1, PALETTE["asphalt_dark"])  # 盔沿
    c.fill(13, 5, 5, 1, PALETTE["glass_mid"])  # 护目镜
    c.set(17, 5, PALETTE["glass_light"])
    c.fill(12, 10, 8, 8, PALETTE["asphalt_mid"])  # 携行具
    c.fill(14, 11, 4, 3, PALETTE["concrete_dark"])  # 前插板
    c.set(15, 12, PALETTE["warn_main"])  # 队标贴
    c.fill(12, 16, 8, 2, PALETTE["concrete_dark"])
    _legs(c, (13, 17), 18, key="asphalt_dark")
    c.fill(21, 11, 7, 2, PALETTE["asphalt_dark"])  # 制式步枪
    c.set(28, 11, PALETTE["grid_dark"])
    c.set(23, 13, PALETTE["warn_dark"])  # 弹匣
    c.vline(22, 13, 3, PALETTE["asphalt_mid"])  # 握把
    return c


def enemy_siege_mech():
    """攻城机甲（era4:enemy-siege-mech）32×32：浇筑装甲机甲+肩部导弹舱+破拆
    爪（对建筑 ×3/armor=20 攻城辨识：重甲+工程破拆械）。"""
    c = Canvas(32, 32)
    _shadow(c, 3, 29, 26)
    concrete_slab(c, 5, 8, 20, 10)                     # 浇筑装甲 hull
    hazard_band(c, 7, 15, 16, 2)                       # hull 警示带
    c.fill(9, 10, 5, 2, PALETTE["glass_mid"])          # 观察窗
    c.set(10, 10, PALETTE["glass_light"])
    c.fill(24, 6, 5, 7, PALETTE["asphalt_dark"])       # 肩部导弹舱
    c.hline(24, 6, 5, PALETTE["warn_main"])
    c.hline(24, 9, 5, PALETTE["warn_dark"])
    c.set(25, 13, PALETTE["warn_light"])
    c.fill(3, 12, 4, 6, PALETTE["grid_dark"])          # 破拆爪
    c.set(2, 11, PALETTE["grid_dark"])
    c.set(2, 14, PALETTE["grid_dark"])
    c.set(2, 17, PALETTE["grid_dark"])
    c.fill(9, 18, 4, 4, PALETTE["asphalt_dark"])       # 反关节腿×2
    c.fill(8, 22, 4, 3, PALETTE["grid_dark"])
    c.fill(6, 25, 6, 2, PALETTE["asphalt_dark"])       # 足
    c.fill(19, 18, 4, 4, PALETTE["asphalt_dark"])
    c.fill(20, 22, 4, 3, PALETTE["grid_dark"])
    c.fill(20, 25, 6, 2, PALETTE["asphalt_dark"])
    c.set(6, 7, PALETTE["warn_light"])                 # 排气口
    c.set(7, 6, PALETTE["warn_dark"])
    return c


def enemy_raid_quad():
    """掠袭四驱（era4:enemy-raid-quad）32×32：四驱突袭车+防滚架+警灯
    （opening_charge 辨识：疾驰扬尘+前倾加速）。"""
    c = Canvas(32, 32)
    _shadow(c, 3, 29, 26)
    c.fill(6, 15, 16, 4, PALETTE["asphalt_mid"])       # 车架
    c.hline(6, 15, 16, PALETTE["asphalt_light"])
    c.vline(8, 11, 4, PALETTE["grid_dark"])            # 防滚架
    c.vline(14, 11, 4, PALETTE["grid_dark"])
    c.hline(8, 11, 7, PALETTE["grid_dark"])
    c.fill(9, 10, 4, 2, PALETTE["warn_main"])          # 警灯条
    c.set(10, 9, PALETTE["warn_light"])
    c.fill(21, 13, 5, 5, PALETTE["grid_main"])         # 车头（电网蓝引擎盖）
    c.hline(21, 13, 5, PALETTE["grid_light"])
    c.set(26, 15, PALETTE["warn_light"])               # 前灯
    c.set(12, 12, PALETTE["glass_mid"])                # 驾驶员护目
    for wx in (8, 24):                                 # 轮（辐条）
        c.fill(wx - 2, 21, 5, 5, PALETTE["asphalt_dark"])
        c.set(wx, 23, PALETTE["concrete_light"])
        c.set(wx - 2, 23, PALETTE["concrete_light"])
        c.set(wx + 2, 23, PALETTE["concrete_light"])
    c.set(2, 14, PALETTE["concrete_light"])            # 扬尘（车尾）
    c.set(1, 16, PALETTE["concrete_main"])
    c.set(3, 17, PALETTE["concrete_main"])
    return c


def enemy_missile_trooper():
    """导弹兵（era4:enemy-missile-trooper）32×32：肩扛发射筒+跪姿锁定
    （stationary_ranged_attack 辨识：肩射筒+固定姿）。"""
    c = Canvas(32, 32)
    _shadow(c, 8, 29, 17)
    c.fill(12, 6, 5, 3, PALETTE["concrete_light"])  # 面
    c.set(16, 7, PALETTE["asphalt_dark"])
    c.fill(11, 4, 7, 2, PALETTE["asphalt_mid"])  # 盔
    c.hline(11, 4, 7, PALETTE["asphalt_light"])
    c.fill(12, 10, 7, 7, PALETTE["concrete_main"])  # 作训服
    c.vline(15, 11, 5, PALETTE["asphalt_mid"])
    c.fill(12, 15, 7, 2, PALETTE["asphalt_mid"])
    c.fill(13, 17, 2, 7, PALETTE["asphalt_dark"])  # 跪姿腿
    c.fill(17, 17, 2, 6, PALETTE["asphalt_dark"])
    c.fill(9, 3, 14, 4, PALETTE["asphalt_dark"])  # 肩扛发射筒
    c.hline(9, 3, 14, PALETTE["asphalt_light"])
    c.vline(22, 3, 4, PALETTE["warn_main"])  # 筒口环
    c.vline(9, 3, 4, PALETTE["grid_dark"])  # 尾喷
    c.set(8, 4, PALETTE["grid_dark"])
    c.set(8, 5, PALETTE["grid_dark"])
    c.set(16, 9, PALETTE["warn_dark"])  # 火控盒
    c.set(17, 9, PALETTE["circuit_light"])
    return c


def enemy_riot_armor():
    """防爆装甲兵（era4:enemy-riot-armor）32×32：通高防爆盾+观察窗+警示带
    （shield_wall 辨识：塔盾覆身+全盔——护盾 480 点行形）。"""
    c = Canvas(32, 32)
    _shadow(c, 8, 29, 17)
    c.fill(21, 5, 5, 4, PALETTE["asphalt_mid"])  # 全盔（盾后出露）
    c.set(25, 7, PALETTE["glass_mid"])
    c.fill(20, 3, 7, 2, PALETTE["asphalt_mid"])
    c.hline(20, 3, 7, PALETTE["asphalt_light"])
    c.fill(21, 9, 5, 6, PALETTE["concrete_dark"])  # 重甲躯干
    c.fill(21, 15, 5, 2, PALETTE["asphalt_mid"])
    _legs(c, (21, 24), 17, key="asphalt_dark")
    concrete_slab(c, 5, 3, 13, 23, pour=False)  # 通高防爆盾
    c.fill(7, 6, 9, 3, PALETTE["glass_mid"])  # 观察窗
    c.set(8, 6, PALETTE["glass_light"])
    c.set(9, 7, PALETTE["glass_light"])
    hazard_band(c, 6, 21, 11, 3)  # 盾面警示带
    stud(c, 7, 18)
    stud(c, 15, 18)
    c.fill(26, 10, 2, 8, PALETTE["asphalt_dark"])  # 电击棍
    c.set(26, 9, PALETTE["warn_main"])
    return c


def enemy_kamikaze_drone():
    """自爆无人机（era4:enemy-kamikaze-drone）32×32：四旋翼+下挂战斗部
    （death_explode 辨识：旋翼盘+警示弹头+电路灯）。"""
    c = Canvas(32, 32)
    _shadow(c, 9, 29, 14)
    c.fill(13, 12, 6, 6, PALETTE["asphalt_mid"])       # 机体
    c.hline(13, 12, 6, PALETTE["asphalt_light"])
    for (ax, ay) in ((7, 11), (20, 11), (7, 19), (20, 19)):
        c.hline(ax, ay, 5, PALETTE["grid_dark"])       # 四臂
    for (rx, ry) in ((4, 9), (23, 9), (4, 21), (23, 21)):
        c.fill(rx, ry, 5, 1, PALETTE["concrete_light"])  # 旋翼盘（运动模糊横盘）
        c.set(rx, ry - 1, PALETTE["concrete_main"])
        c.set(rx + 4, ry - 1, PALETTE["concrete_main"])
    c.fill(14, 18, 4, 3, PALETTE["warn_main"])         # 下挂战斗部
    c.set(15, 18, PALETTE["warn_light"])
    c.hline(14, 20, 4, PALETTE["warn_dark"])
    c.set(14, 13, PALETTE["circuit_light"])            # 电路灯
    c.set(18, 15, PALETTE["circuit_light"])
    c.set(16, 13, PALETTE["circuit_mid"])
    return c


def enemy_ew_officer():
    """电子战军官（era4:enemy-ew-officer）32×32：大檐帽+天线阵列背囊+手持终端
    （war_drums 辨识：指挥网中枢——天线+终端屏+警示肩章）。"""
    c = Canvas(32, 32)
    _shadow(c, 8, 29, 17)
    c.fill(13, 6, 5, 3, PALETTE["concrete_light"])  # 面
    c.set(17, 7, PALETTE["asphalt_dark"])
    c.fill(12, 4, 7, 2, PALETTE["asphalt_mid"])  # 大檐帽
    c.hline(11, 5, 9, PALETTE["asphalt_dark"])  # 帽檐
    c.fill(12, 10, 8, 8, PALETTE["concrete_main"])  # 军官大衣
    c.hline(12, 10, 8, PALETTE["concrete_light"])
    c.vline(16, 11, 6, PALETTE["asphalt_mid"])
    c.fill(12, 16, 8, 2, PALETTE["asphalt_mid"])
    _legs(c, (13, 17), 18, key="asphalt_dark")
    c.set(12, 10, PALETTE["warn_main"])  # 警示肩章
    c.set(19, 10, PALETTE["warn_main"])
    c.fill(22, 5, 4, 10, PALETTE["grid_dark"])  # 天线阵列背囊
    c.hline(22, 5, 4, PALETTE["asphalt_light"])
    c.vline(23, 0, 5, PALETTE["grid_dark"])  # 主天线
    c.set(23, 0, PALETTE["warn_light"])
    c.vline(25, 2, 3, PALETTE["asphalt_mid"])  # 副天线
    c.set(25, 1, PALETTE["warn_main"])
    circuit(c, 22, 10, 4)  # 背囊走线
    c.fill(5, 14, 5, 4, PALETTE["asphalt_dark"])  # 手持终端
    c.fill(6, 15, 3, 2, PALETTE["circuit_mid"])  # 终端屏
    c.set(6, 15, PALETTE["circuit_light"])
    return c


def boss_nightowl():
    """夜枭（era4:enemy-boss）64×64：隐形轰炸飞翼——纯黑雷达散射蒙皮+双圆盘
    传感器（枭目）+腹内空袭弹舱+翼尖电网蓝航行灯（stealth/airstrike 辨识：
    飞翼无尾+菱形折面缝）。画布 64（28 §5 裁定先例重演：内容表
    canvas_size_px=96，96 不在校验器清单，取「常规 Boss 64~96」下限 64 合规；
    升 96 须随 SIZE-ALLOWLIST 扩展走 REV）。"""
    c = Canvas(64, 64)
    _shadow(c, 6, 60, 52)
    span = (  # 飞翼平面形（行, x0, x1）——菱形前缘+后掠翼尖
        (20, 30, 33), (21, 27, 36), (22, 24, 39), (23, 21, 42), (24, 18, 45),
        (25, 15, 48), (26, 12, 51), (27, 10, 53), (28, 8, 55), (29, 7, 56),
        (30, 6, 57), (31, 6, 57), (32, 6, 57), (33, 7, 56), (34, 8, 55),
        (35, 10, 53), (36, 13, 50), (37, 16, 47), (38, 20, 43), (39, 25, 38),
        (40, 29, 34), (41, 30, 33),
    )
    for (yy, x0, x1) in span:
        c.hline(x0, yy, x1 - x0 + 1, PALETTE["asphalt_dark"])  # 蒙皮
    for (yy, x0, x1) in span:  # 前缘折面缝（上缘两行铺中灰——雷达散射折面）
        c.hline(x0, yy, 1, PALETTE["asphalt_mid"])
        c.hline(x1, yy, 1, PALETTE["asphalt_mid"])
    c.fill(24, 22, 16, 20, PALETTE["asphalt_dark"])     # 中央机身隆起
    for i in range(10):                                  # 机身菱形折面缝
        c.set(24 + i, 22 + i, PALETTE["asphalt_mid"])
        c.set(39 - i, 22 + i, PALETTE["asphalt_mid"])
    for ex in (25, 35):                                  # 枭目传感器（双圆盘）
        c.fill(ex, 25, 3, 3, PALETTE["glass_light"])
        c.set(ex + 1, 26, PALETTE["warn_main"])          # 瞳居中（警示黄——夜枭锁定光）
        c.hline(ex, 24, 3, PALETTE["asphalt_mid"])       # 眉棱（枭面）
    c.set(23, 23, PALETTE["asphalt_mid"])
    c.set(40, 23, PALETTE["asphalt_mid"])
    c.fill(28, 29, 8, 3, PALETTE["glass_mid"])           # 座舱罩条
    c.set(29, 30, PALETTE["glass_light"])
    c.fill(20, 32, 4, 4, PALETTE["grid_dark"])           # 两侧进气口
    c.fill(40, 32, 4, 4, PALETTE["grid_dark"])
    c.fill(26, 42, 12, 5, PALETTE["asphalt_dark"])       # 腹内弹舱（开启）
    c.hline(25, 42, 14, PALETTE["grid_dark"])            # 舱门轨
    c.hline(25, 46, 14, PALETTE["grid_dark"])
    for bx in (28, 32, 36):                              # 空袭弹序（airstrike）
        c.fill(bx, 43, 2, 3, PALETTE["warn_main"])
        c.set(bx, 43, PALETTE["warn_light"])
    c.set(31, 19, PALETTE["warn_light"])                 # 机顶频闪灯（贴机鼻）
    c.set(32, 19, PALETTE["warn_main"])
    c.fill(5, 27, 2, 4, PALETTE["asphalt_dark"])         # 翼尖小垂尾
    c.fill(57, 27, 2, 4, PALETTE["asphalt_dark"])
    c.set(6, 30, PALETTE["grid_main"])                   # 翼尖电网蓝航行灯（垂尾后置顶）
    c.set(57, 30, PALETTE["grid_main"])
    for (sx, sy) in ((14, 27), (20, 23), (43, 23), (49, 27)):
        c.set(sx, sy, PALETTE["glass_mid"])              # 蒙皮护盾微光（shield 1200）
    return c


# ---------------------------------------------------------------- 载具（F-042 E4 档）

def truck_right():
    """卡车（E4 载具，era.json logistics.truck；15/s 档并列机动运输形态）
    32×32（朝右）：混凝土灰集装箱+电网蓝驾驶室+玻璃挡风（现代色板换装——
    era3/truck-right.png 为 T-119 前置件，本件=E4 时代正式档）。"""
    c = Canvas(32, 32)
    _shadow(c, 2, 29, 28)
    concrete_slab(c, 3, 7, 15, 10, pour=False)         # 集装箱（浇筑板）
    c.hline(3, 12, 15, PALETTE["grid_main"])           # 箱体电网蓝条带
    play_triangle(c, 7, 13, 3, "grid_dark")            # 箱侧播放键徽（小号母题）
    c.fill(19, 9, 9, 8, PALETTE["grid_main"])          # 驾驶室
    c.hline(19, 9, 9, PALETTE["grid_light"])
    c.vline(19, 9, 8, PALETTE["grid_light"])
    c.fill(21, 11, 5, 3, PALETTE["glass_light"])       # 挡风玻璃
    c.hline(21, 11, 5, PALETTE["glass_mid"])
    c.set(20, 8, PALETTE["warn_main"])                 # 顶置警示灯
    c.set(26, 8, PALETTE["warn_main"])
    c.fill(28, 17, 2, 3, PALETTE["asphalt_dark"])      # 前保险杠
    c.set(29, 12, PALETTE["warn_light"])               # 前灯
    c.fill(2, 17, 28, 3, PALETTE["asphalt_dark"])      # 车架
    for wx in (8, 23):                                 # 轮对
        c.fill(wx - 2, 20, 5, 5, PALETTE["asphalt_dark"])
        c.set(wx, 22, PALETTE["concrete_light"])
        c.set(wx - 2, 22, PALETTE["concrete_light"])
        c.set(wx + 2, 22, PALETTE["concrete_light"])
    c.set(2, 11, PALETTE["warn_dark"])                 # 尾灯
    return c


# ---------------------------------------------------------------- UI 色板（F-132 系）

def panel_palette():
    """E4 面板色板 32×32：电网蓝描边+沥青底+4×4 色块矩阵（电网/混凝土/
    沥青/电路四行），供 C-22 面板 E4 换肤取色（色板底稿，非成品 UI 图）。"""
    c = Canvas(32, 32)
    c.fill(0, 0, 32, 32, PALETTE["asphalt_dark"])
    c.rect(0, 0, 32, 32, None, PALETTE["grid_main"])
    c.rect(2, 2, 28, 28, None, PALETTE["grid_dark"])
    c.fill(4, 4, 24, 24, PALETTE["asphalt_mid"])
    rows = (
        ("grid_light", "grid_main", "grid_dark", "warn_main"),
        ("concrete_light", "concrete_main", "concrete_dark", "warn_dark"),
        ("asphalt_light", "asphalt_mid", "asphalt_dark", "glass_mid"),
        ("circuit_light", "circuit_mid", "circuit_dark", "glass_light"),
    )
    for ry, row in enumerate(rows):
        for rx, key in enumerate(row):
            c.fill(5 + rx * 6, 5 + ry * 6, 5, 5, PALETTE[key])
    return c


# ---------------------------------------------------------------- 地形 9 槽（16×16）

def tile_floor_concrete():
    """混凝土地砖：浇筑面+伸缩缝（错缝）+系筋孔（Plain 主槽）。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["concrete_main"])
    c.hline(0, 0, 16, PALETTE["concrete_light"])
    c.vline(0, 0, 16, PALETTE["concrete_light"])
    c.hline(0, 15, 16, PALETTE["concrete_dark"])
    c.vline(15, 0, 16, PALETTE["concrete_dark"])
    c.hline(0, 7, 16, PALETTE["concrete_dark"])   # 伸缩缝（横）
    c.vline(8, 0, 7, PALETTE["concrete_dark"])    # 纵缝（上半）
    c.vline(3, 8, 8, PALETTE["concrete_dark"])    # 纵缝（下半错缝）
    c.vline(12, 8, 8, PALETTE["concrete_dark"])
    for (hx, hy) in ((2, 2), (13, 2), (6, 10), (14, 10), (2, 13)):
        c.set(hx, hy, PALETTE["concrete_dark"])   # 系筋孔
    c.set(5, 4, PALETTE["concrete_light"])        # 浇筑抹痕
    c.set(10, 12, PALETTE["concrete_light"])
    return c


def tile_floor_concrete_grated():
    """混凝土篦栅地砖（Forest 代位——现代城区无林地，排水篦栅代绿带；
    era3 floor-steel-grated 同构异色）。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["asphalt_dark"])
    for y in range(0, 16, 2):
        c.hline(0, y, 16, PALETTE["concrete_dark"])
        c.hline(0, y + 1, 16, PALETTE["asphalt_dark"])
    for x in range(0, 16, 4):
        c.vline(x, 0, 16, PALETTE["asphalt_mid"])
    c.hline(0, 0, 16, PALETTE["concrete_light"])
    c.set(7, 7, PALETTE["asphalt_light"])
    c.set(8, 12, PALETTE["asphalt_light"])
    return c


def tile_road_asphalt(dash_offset):
    """公路地砖（Road）：沥青面+集料点+路缘线+警示中线虚线（offset 区分
    正/副槽）。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["asphalt_mid"])
    for (sx, sy, key) in ((5, 4, "asphalt_light"), (12, 5, "asphalt_light"),
                          (3, 10, "asphalt_dark"), (14, 11, "asphalt_light"),
                          (8, 9, "asphalt_dark"), (6, 13, "asphalt_light")):
        c.set(sx, sy, PALETTE[key])                # 集料点
    c.hline(0, 1, 16, PALETTE["concrete_light"])   # 路缘线（双缘）
    c.hline(0, 14, 16, PALETTE["concrete_light"])
    for dx in (2 + dash_offset, 9 + dash_offset):  # 中线虚线（沿行进向）
        c.fill(dx, 7, 4, 2, PALETTE["warn_main"])
        c.set(dx, 7, PALETTE["warn_light"])
    if dash_offset:                                # 副槽=检修井盖变体
        c.fill(11, 2, 3, 3, PALETTE["concrete_dark"])
        c.set(12, 3, PALETTE["asphalt_light"])
    return c


def tile_wall_glass_curtain(lit_variant):
    """玻璃幕墙墙砖（Wall）：2×2 幕墙格+框棂+对角反光+底部混凝土裙带；
    lit_variant=True 副槽=部分楼层亮灯。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["grid_dark"])     # 框棂底
    for (wx, wy) in ((1, 1), (9, 1), (1, 8), (9, 8)):
        c.fill(wx, wy, 6, 6, PALETTE["glass_mid"])  # 幕墙玻璃格
        c.hline(wx, wy + 1, 6, PALETTE["glass_dark"])  # 玻璃暗口（纵深）
    for (rx, ry) in ((2, 2), (3, 3), (10, 9), (11, 10)):
        c.set(rx, ry, PALETTE["glass_light"])      # 对角反光
    if lit_variant:
        c.fill(1, 8, 6, 6, PALETTE["glass_light"])   # 亮灯层
        c.fill(9, 1, 6, 6, PALETTE["glass_light"])
        c.set(3, 10, PALETTE["warn_light"])          # 室内暖光
        c.set(12, 3, PALETTE["warn_light"])
        c.hline(1, 9, 6, PALETTE["glass_mid"])
        c.hline(9, 2, 6, PALETTE["glass_mid"])
    c.hline(0, 15, 16, PALETTE["concrete_dark"])   # 混凝土裙带
    c.set(4, 15, PALETTE["concrete_main"])
    c.set(11, 15, PALETTE["concrete_main"])
    return c


def tile_floor_rubble_concrete(dark_variant):
    """拆除废墟地砖（Rock 代位）：沥青底+混凝土碎块+钢筋头。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["asphalt_mid"])
    chunks = ((2, 2, 3, 2, "concrete_light"), (7, 3, 4, 2, "concrete_main"),
              (12, 2, 2, 3, "concrete_main"), (3, 8, 4, 3, "concrete_main"),
              (9, 9, 3, 2, "concrete_light"), (13, 12, 2, 2, "concrete_dark"),
              (6, 13, 3, 2, "concrete_dark"), (1, 13, 2, 2, "concrete_main"))
    if dark_variant:
        chunks = ((3, 2, 2, 3, "concrete_dark"), (8, 2, 3, 2, "concrete_main"),
                  (13, 4, 2, 2, "concrete_light"), (2, 8, 3, 2, "concrete_light"),
                  (7, 10, 4, 3, "concrete_dark"), (12, 9, 3, 2, "concrete_main"),
                  (3, 13, 3, 2, "concrete_main"), (14, 14, 2, 2, "concrete_dark"))
    for (x, y, w, h, key) in chunks:
        c.fill(x, y, w, h, PALETTE[key])
        c.set(x, y + h - 1, PALETTE["concrete_dark"])
    c.vline(5, 5, 4, PALETTE["grid_dark"])         # 钢筋头
    c.set(5, 5, PALETTE["warn_dark"])
    c.hline(9, 7, 4, PALETTE["grid_dark"])
    c.set(12, 7, PALETTE["warn_dark"])
    return c


def tile_water_modern():
    """现代水面（Water）：蓝灰港区水（周期 8 斜带，era2/era3 water 同构异色）
    +玻璃反光波光。"""
    c = Canvas(16, 16)
    for y in range(16):
        for x in range(16):
            band = (x + y) % 8
            key = "glass_dark" if band in (0, 1) else ("grid_dark" if band == 4 else "asphalt_dark")
            c.set(x, y, PALETTE[key])
    for (x, y) in ((3, 2), (11, 6), (7, 12)):
        c.set(x, y, PALETTE["glass_light"])  # 波光高光
    c.set(5, 9, PALETTE["grid_main"])        # 岸线反染
    return c


# ---------------------------------------------------------------- 传送带双层（E4 皮肤）

def belt_body():
    """直段带体 16×16（动画层，E4 皮肤）：竖向条带 cols 5..10，
    行色=PAT16[y%16]——沿 V 方向周期 16=源 tile 高 → shader UV 滚动可无缝
    平铺（几何与 E3 皮肤逐式相同，仅色板换装）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    for y in range(BODY_TILE_PX):
        for x in range(5, 11):
            c.set(x, y, PALETTE[PAT16[y % 16]])
    return c


def belt_edge():
    """直段带边 16×16（静态层，E4 皮肤）：双侧护栏+警示柱钉，带外全透明
    （与带体零重叠——护栏=混凝土灰立缘，柱钉=警示黄，E3 钢轨×黄铜同构）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    c.vline(3, 0, 16, PALETTE["asphalt_dark"])
    c.vline(4, 0, 16, PALETTE["concrete_dark"])
    c.vline(11, 0, 16, PALETTE["concrete_dark"])
    c.vline(12, 0, 16, PALETTE["asphalt_dark"])
    for y in (1, 5, 9, 13):  # 护栏警示柱钉
        stud(c, 4, y)
        stud(c, 11, y)
    return c


def belt_turn_edge():
    """弯口带边 16×16（静态层，E4 皮肤）：规范右弯（南入东出）外/内弯护栏
    +对角补角，与带体（u∈[0,20] 条带）零重叠——不透明集与 E3 皮肤逐坐标
    相同（冻结契约：几何不随皮肤变）。左弯=渲染层水平镜像（决策层 FlipX）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    # 外弯（西北侧）：竖段 col3/4 rows6..15 + 对角 (4,5)(4,4) + 横段 row3/4 cols6..15
    c.vline(3, 6, 10, PALETTE["asphalt_dark"])
    c.vline(4, 6, 10, PALETTE["concrete_dark"])
    c.set(4, 5, PALETTE["asphalt_dark"])
    c.set(4, 4, PALETTE["asphalt_dark"])
    c.set(5, 4, PALETTE["concrete_dark"])
    c.hline(6, 3, 10, PALETTE["asphalt_dark"])
    c.hline(6, 4, 10, PALETTE["concrete_dark"])
    # 内弯（东南侧）：竖段 col11/12 rows11..15 + 横段 row11/12 cols11..15
    c.vline(11, 11, 5, PALETTE["concrete_dark"])
    c.vline(12, 11, 5, PALETTE["asphalt_dark"])
    c.hline(11, 11, 5, PALETTE["concrete_dark"])
    c.hline(12, 12, 5, PALETTE["asphalt_dark"])
    for y in (9, 13):  # 警示柱钉
        stud(c, 4, y)
    for x in (9, 13):
        stud(c, x, 4)
    stud(c, 11, 13)
    stud(c, 13, 11)
    return c


def belt_turn_frame(phase):
    """转弯单帧 16×16（动画层，E4 皮肤）：条带=竖臂∪弯心∪横臂
    （turn_path_u 定义域），每像素色=PAT16[(u−phase) mod 16]——
    phase=帧号×PX_PER_FRAME（u 律与 E3 皮肤逐式相同）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    for y in range(BODY_TILE_PX):
        for x in range(BODY_TILE_PX):
            u = turn_path_u(x, y)
            if u is not None:
                c.set(x, y, PALETTE[PAT16[(u - phase) % 16]])
    return c


def belt_turn_strip():
    """转弯 8 帧条带 64×32：4×2 网格（帧序=行优先 row*4+col，帧 k phase=2k）。
    8 帧横排为 128px 宽超 SIZE-ALLOWLIST，取双行网格（帧数契约由 --self-test
    与 C# Era4ArtMappingTests 双侧断言）。"""
    strip = Canvas(TURN_FRAMES_PER_ROW * BODY_TILE_PX,
                   (TURN_FRAMES // TURN_FRAMES_PER_ROW) * BODY_TILE_PX)
    for k in range(TURN_FRAMES):
        strip.blit(belt_turn_frame(k * PX_PER_FRAME),
                   (k % TURN_FRAMES_PER_ROW) * BODY_TILE_PX,
                   (k // TURN_FRAMES_PER_ROW) * BODY_TILE_PX)
    return strip


# ---------------------------------------------------------------- 驱动

TOWERS = {
    "tower-missile": tower_missile,      # tier1/max2 → l1/l2
    "tower-laser": tower_laser,          # tier2/max2 → l1/l2（E3 臼塔同款两级先例）
    "tower-radar": tower_radar,          # max_tier1 → l1 单档（支援设施不升级）
    "tower-mine": tower_mine,            # max_tier1 → l1 单档（利基行不升级）
}
TOWER_LEVELS = {
    "tower-missile": (1, 2),
    "tower-laser": (1, 2),
    "tower-radar": (1,),
    "tower-mine": (1,),
}

ENEMIES = {
    "enemy-militia": enemy_militia,
    "enemy-assault-trooper": enemy_assault_trooper,
    "enemy-siege-mech": enemy_siege_mech,
    "enemy-raid-quad": enemy_raid_quad,
    "enemy-missile-trooper": enemy_missile_trooper,
    "enemy-riot-armor": enemy_riot_armor,
    "enemy-kamikaze-drone": enemy_kamikaze_drone,
    "enemy-ew-officer": enemy_ew_officer,
}


def build_all(out_root):
    wrote = []
    for name, fn in sorted(TOWERS.items()):
        for level in TOWER_LEVELS[name]:
            path = os.path.join(out_root, "towers", f"{name}-l{level}.png")
            fn(level).write(path)
            wrote.append(path)
    for name, fn in sorted(ENEMIES.items()):
        path = os.path.join(out_root, "enemies", f"{name}.png")
        fn().write(path)
        wrote.append(path)
    boss = os.path.join(out_root, "enemies", "boss-nightowl.png")
    boss_nightowl().write(boss)
    wrote.append(boss)
    truck = os.path.join(out_root, "units", "truck-right.png")
    truck_right().write(truck)
    wrote.append(truck)
    ui = os.path.join(out_root, "ui", "panel-era4.png")
    panel_palette().write(ui)
    wrote.append(ui)
    tiles = {
        "floor-concrete": lambda: tile_floor_concrete(),
        "floor-concrete-grated": lambda: tile_floor_concrete_grated(),
        "road-asphalt": lambda: tile_road_asphalt(0),
        "road-asphalt-alt": lambda: tile_road_asphalt(1),
        "wall-glass-curtain": lambda: tile_wall_glass_curtain(False),
        "wall-glass-curtain-alt": lambda: tile_wall_glass_curtain(True),
        "floor-rubble-concrete": lambda: tile_floor_rubble_concrete(False),
        "floor-rubble-concrete-alt": lambda: tile_floor_rubble_concrete(True),
        "water-modern": lambda: tile_water_modern(),
    }
    for name, fn in tiles.items():
        path = os.path.join(out_root, "tiles", f"{name}.png")
        fn().write(path)
        wrote.append(path)
    for name, fn in (("belt-body", belt_body), ("belt-edge", belt_edge),
                     ("belt-turn-edge", belt_turn_edge)):
        path = os.path.join(out_root, "belt", f"{name}.png")
        fn().write(path)
        wrote.append(path)
    anim = os.path.join(out_root, "anim", "belt-turn-anim.png")
    belt_turn_strip().write(anim)
    wrote.append(anim)
    return wrote


# ---------------------------------------------------------------- 自检

def _sha256(path):
    with open(path, "rb") as fh:
        return hashlib.sha256(fh.read()).hexdigest()


def check_turn_strip(out_root):
    """转弯 8 帧帧边界对齐断言（E4 皮肤，F-127 验收核心）：
    ①帧数/网格几何（4×2×16px=64×32）；②u 律——每帧每带体像素色=
    PAT16[(u−2k)%16]；③双臂逐像素平移——竖臂 (x,y)@k+1 == (x,y+2)@k、
    横臂 (x,y)@k+1 == (x−2,y)@k（帧边界对齐的可直读形式）；④循环闭合——
    k+8 相位≡k（律内含）且八帧两两不同（循环非退化）；⑤带体/带边不透明集
    零重叠（双层叠加契约）。返回 (ok, 说明)。"""
    strip = belt_turn_strip()  # 内存基准（build_all 已写盘，确定性下字节同）
    if (strip.width, strip.height) != (64, 32):
        return False, f"strip size {strip.width}x{strip.height} != 64x32"
    frames = []
    for k in range(TURN_FRAMES):
        fx, fy = (k % TURN_FRAMES_PER_ROW) * 16, (k // TURN_FRAMES_PER_ROW) * 16
        frame = {(x, y): strip.get(fx + x, fy + y)
                 for y in range(16) for x in range(16)}
        frames.append(frame)
    # ② u 律
    for k, frame in enumerate(frames):
        for y in range(16):
            for x in range(16):
                u = turn_path_u(x, y)
                if u is None:
                    if frame[(x, y)][3] != 0:
                        return False, f"frame{k} 带外像素不透明 ({x},{y})"
                else:
                    want = PALETTE[PAT16[(u - 2 * k) % 16]] + (255,)
                    if frame[(x, y)] != want:
                        return False, f"frame{k} u 律违例 ({x},{y}) u={u}"
    # ③ 双臂逐像素平移（帧边界对齐）
    for k in range(TURN_FRAMES):
        nxt = frames[(k + 1) % TURN_FRAMES]
        cur = frames[k]
        for y in range(11, 16):  # 竖臂 y+2 需 ≤15 → y ∈ [11,13]
            if y + 2 <= 15:
                for x in range(5, 11):
                    if nxt[(x, y)] != cur[(x, y + 2)]:
                        return False, f"竖臂平移违例 frame{k}→{k+1} ({x},{y})"
        for x in range(11, 16):  # 横臂 x−2 需 ≥11 → x ∈ [13,15]
            if x - 2 >= 11:
                for y in range(5, 11):
                    if nxt[(x, y)] != cur[(x - 2, y)]:
                        return False, f"横臂平移违例 frame{k}→{k+1} ({x},{y})"
    # ④ 八帧两两不同（循环非退化）+ 闭合（2px×8=16=PAT 周期 → 帧律自然回 0）
    for a in range(TURN_FRAMES):
        for b in range(a + 1, TURN_FRAMES):
            if frames[a] == frames[b]:
                return False, f"frame{a} == frame{b}（循环退化）"
    # ⑤ 带体/带边零重叠（双层叠加契约）
    edge = belt_turn_edge()
    edge_cells = edge.opaque()
    body_cells = {(x, y) for y in range(16) for x in range(16)
                  if turn_path_u(x, y) is not None}
    overlap = edge_cells & body_cells
    if overlap:
        return False, f"弯口带边与带体重叠 {sorted(overlap)[:4]}"
    straight_edge = belt_edge().opaque()
    straight_body = {(x, y) for y in range(16) for x in range(5, 11)}
    if straight_edge & straight_body:
        return False, "直段带边与带体重叠"
    return True, (f"frames=8 grid=4x2x16 u-law=ok arms-shift=ok "
                  f"distinct=8 loop=seamless(2px*8=16=PAT16 period) overlay=disjoint")


def check_determinism(out_root):
    """确定性：二次生成（独立临时目录）与首次输出逐文件 sha256 全等。"""
    with tempfile.TemporaryDirectory() as tmp:
        second = os.path.join(tmp, "era4")
        build_all(second)
        count, mismatches = 0, []
        for dirpath, _dirs, files in os.walk(second):
            for fn in sorted(files):
                rel = os.path.relpath(os.path.join(dirpath, fn), second)
                a = os.path.join(out_root, rel)
                b = os.path.join(second, rel)
                if _sha256(a) != _sha256(b):
                    mismatches.append(rel)
                count += 1
        return count, mismatches


def run_self_test(out_root):
    """①asset_validator 全目录；②转弯 8 帧对齐；③确定性 sha256。"""
    here = os.path.dirname(os.path.abspath(__file__))
    ci_dir = os.path.normpath(os.path.join(here, "..", "ci"))
    sys.path.insert(0, ci_dir)
    import asset_validator  # noqa: E402（同仓库零依赖校验器）

    report = asset_validator.run(
        [out_root], asset_validator.DEFAULT_ALLOWED_SIZES,
        asset_validator.DEFAULT_MAX_PALETTE, False)
    rejects = [r for r in report["results"]
               if any(f["severity"] == "REJECT" for f in r["findings"])]
    for r in report["results"]:
        for f in r["findings"]:
            if f["severity"] != "INFO":
                print(f"  {f['severity']} {r['path']}: {f['message']}")

    belt_ok, belt_msg = check_turn_strip(out_root)
    count, mismatches = check_determinism(out_root)
    if rejects or not belt_ok or mismatches:
        print(f"self-test FAIL: static_rejects={len(rejects)} "
              f"belt_ok={belt_ok}({belt_msg}) determinism_mismatch={len(mismatches)}")
        return 1, report, belt_msg
    print(f"self-test ok: static_files={report['files']} rejected=0 warned={report['warned']} "
          f"belt: {belt_msg} determinism={count}/{count} sha256-equal")
    return 0, report, belt_msg


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="E4 占位美术程序合成器（T-128）：四塔/九敌+夜枭/卡车/"
                    "UI 色板/地形 9 槽/传送带 E4 皮肤（15/s 档双层动画）")
    parser.add_argument("--out", default=None,
                        help="输出根目录（默认=presentation/EraTD/textures/era4）")
    parser.add_argument("--self-test", action="store_true",
                        help="生成后调 asset_validator+8 帧对齐+确定性自校验（任一失败非 0 退出）")
    args = parser.parse_args(argv)

    if args.out:
        out_root = os.path.normpath(args.out)
    else:
        out_root = os.path.normpath(os.path.join(
            os.path.dirname(os.path.abspath(__file__)),
            "..", "..", "game", "presentation", "EraTD", "textures", "era4"))

    for p in build_all(out_root):
        print(f"wrote {p}")

    if args.self_test:
        rc, _report, _msg = run_self_test(out_root)
        return rc
    return 0


if __name__ == "__main__":
    sys.exit(main())
