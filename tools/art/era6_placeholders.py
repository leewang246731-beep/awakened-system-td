#!/usr/bin/env python3
"""
era6_placeholders.py — 终章（深空）占位美术程序合成器（任务 T-142，Wave 8，
E 泳道，组件 C-28）

用途：为终章（深空时代，era6:era-finale）确定性合成占位像素图——两塔
（奇点炮/时空塔，era6/towers.json，均 max1 单档）、八敌+堕落守望者 Boss
（era6/enemies.json）、传送阵载具（era.json logistics.carrier=teleporter，
∞ 件/s 末档）与传送带终章皮肤（ladder 末位 ∞ 档：belt 纹理色板换装，复用
T-119 双层动画全套几何契约）、终章面板色板（C-22 换肤底稿）、深空地形子集
（虚空岩床/光线路/星门墙 9 槽）。
背景：T-128/T-142 era4/era5 全程序合成先例第 3 次重演（终章无 CC0 成套
素材源，按 T-078 预批准回退路线 100%「代码合成像素图」）。
红线对齐：这是**程序绘制**（确定性：固定坐标+固定 18 色深空子板，无随机
数、无 AI 生图），与 21 号 §2.1「AI PNG 不进 presentation/游戏 sprite
管线」红线不冲突；产物可由本脚本逐字节复现，人工重绘升级时以本脚本为底稿。

视觉身份（31 号 §1.2 终章行）：主色星门靛蓝 #3E55C9（=E1 晨曦赭金补色，
首尾色彩闭环）/ 辅暗物质紫黑 #1D1B33、零点白青 #BFE8F2；母题形状=**星门**
（守望塔最终形态——塔顶展开成环）；纹理语言=星云层积、引力透镜弧、记忆
光尘；专属母题=**星门环+裂缝**（塔"开"成了门：六时代几何母题在此闭合成
圆）。明度压深（饱和度回落、Gris 式"层积回归"）。Cinematics A-06（E5→终章
跃迁，能量尖塔塔顶展开成星门环，六色层积收束为靛蓝）为终章帧参考。

特殊形态裁定（任务卡口径，同 era4/era5 裁先例）：
  时空塔=星门环静态底稿——canonical=塔顶展开环+裂缝注记（星门母题件），
  时间涟漪/脉冲属渲染层（SpriteRotationQuarters/着色器先例同律）。
  堕落守望者 Boss 画布=96×112（设计案 §13.1「终章 Boss 96×112」逐字锚点，
  T-136 升格：内容表 canvas_size_px=96+canvas_height_px=112 双字段对齐；
  SIZE-ALLOWLIST 扩 96/112 随 REV-20261018-T136 登记——28 号 §5「常规 Boss
  64~96」带上沿 96=宽、112=终章专属纵幅。

传送带终章皮肤（∞ 档，era6/era.json logistics tier5=teleporter null=∞——
trip 传送形态的皮肤对位）：纹理色板换装，几何与帧律逐字节复用 T-119 冻结
契约（41 号 §4 三方镜像表，与 game/src/EraCore/Presentation/
BeltRenderDecisions.cs 常量一致，C# 侧 Era6ArtMappingTests 逐像素复验）：
    直线段 = shaders/belt-scroll.gdshader UV 滚动——本包供其两张 16px 纹理：
      belt/belt-body.png   动画层（带体表面，沿行进方向周期 16px，可无缝平铺）
      belt/belt-edge.png   静态层（带边护栏+裂缝柱钉，不随 UV 滚动）
    转弯段 = anim/belt-turn-anim.png 8 帧 tile 动画（4×2 网格 16px 帧，画布
      64×32；帧序=行优先 row*4+col）
      + belt/belt-turn-edge.png 静态弯口边框（与帧叠加，双层渲染）。
    帧边界对齐契约（与 E3/E4/E5 皮肤完全同几何，仅色板换装）：
      转弯路径坐标 u（像素沿带心线，南入北行东出）：
        竖臂 x∈[5,10],y∈[11,15]: u=15-y；弯心 x∈[5,10],y∈[5,10]:
        u=5+(x-5)+(10-y)；横臂 x∈[11,15],y∈[5,10]: u=15+(x-10)。全程
        u∈[0,20]→转弯步长 21px。
      第 k 帧带体色 = PAT16[(u − 2k) mod 16]（图案向下游流动；每帧前进 2px，
      8 帧×2px=16px=图案周期 → 循环无缝闭合）；直段步长 16px/格。
      PAT16（周期 16，终章深空换装）：暗物质基材×2 档 + 星门靛蓝导流条×2
      （idx 2 亮/idx 10 暗）——E3 煤黑×黄铜/E4 沥青×警示黄/E5 基材×聚变紫
      同构异色。
    带速（∞ 档）由决策层 scroll 相位量驱动（ShaderOffsetPx），纹理仅为
    图案相位源——速度不烙进纹理（同一纹理可服务任意带速档）。

输出（--out DIR，默认 presentation/EraTD/textures/era6/）：
  towers/tower-singularity-l1.png                48×48 奇点炮（高能档）
  towers/tower-spacetime-l1.png                  32×32 时空塔（星门母题，max1 单档）
  enemies/enemy-void-swarm.png … 8 种            32×32 常规敌
  enemies/boss-fallen-watcher.png                96×112 堕落守望者（§13.1 终章锚点）
  units/teleporter-right.png                     32×32 传送阵（终章载具）
  ui/panel-era6.png                              32×32 面板色板
  tiles/floor-voidstone / -nebula / road-lightray{,-alt} /
        wall-stargate{,-alt} / floor-rubble-void{,-alt} /
        water-voidsea                            16×16 地形 9 槽
  belt/belt-body.png / belt-edge.png /
        belt-turn-edge.png                       16×16 传送带双层纹理（终章皮肤）
  anim/belt-turn-anim.png                        64×32 转弯 8 帧（4×2 网格）

包文件数=27（era6 内容 id 对齐：两塔均 max_tier=1 → 2 升级位文件——
era4 30/era5 29 的同构异数，槽类结构一致，REV 登记）。

全部输出须过 tools/ci/asset_validator.py（尺寸 16/32/48/64/96/112 白名单
（96/112=T-136 终章 Boss 96×112 升格扩展）、≤64 色、
kebab 命名）。--self-test 另做五类强校验：①asset_validator 全目录；②转弯 8 帧
帧边界对齐（u 律+双臂逐像素平移+循环闭合+八帧两两不同+带边零重叠）；③静态层/
动画层透明区互斥（双层叠加契约）；④时空塔四相条带（phase0=canonical+
四相两两不同）；⑤确定性（二次生成 sha256 逐字节全等）。

依赖：仅 Python 3.8+ 标准库（struct/zlib/argparse/hashlib/tempfile）——与
asset_validator 同纪律，不引 Pillow。CLI：python era6_placeholders.py
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

# ---------------------------------------------------------------- 终章深空色板
# 18 色（≤64 master 上限，逐文件口径）：星门靛蓝 3（31 号终章主色 #3E55C9
# 居中——E1 晨曦赭金的补色）/ 暗物质紫黑 3（辅 #1D1B33——基材·轮廓·暗部）/
# 零点白青 3（辅 #BFE8F2——透镜弧·高光）/ 星云 3（星云层积——nebula 蓝紫
# 层带）/ 光尘 3（记忆光尘——dust 淡青白微光）/ 透镜 3（引力透镜弧——lens
# 弧光——E5 全息对位）
PALETTE = {
    "indigo_light": (122, 140, 224),
    "indigo_main": (62, 85, 201),    # 31 号终章主色 星门靛蓝 #3E55C9
    "indigo_dark": (38, 55, 150),
    "void_light": (58, 55, 92),
    "void_mid": (37, 34, 64),
    "void_dark": (24, 22, 44),       # 31 号终章辅色 暗物质紫黑 #1D1B33 近邻
    "frost_light": (222, 244, 248),
    "frost_main": (191, 232, 242),   # 31 号终章辅色 零点白青 #BFE8F2
    "frost_dark": (140, 192, 210),
    "nebula_light": (94, 78, 168),
    "nebula_mid": (68, 55, 140),
    "nebula_dark": (48, 38, 110),
    "dust_light": (232, 238, 252),
    "dust_mid": (200, 214, 240),
    "dust_dark": (166, 184, 220),
    "lens_light": (150, 170, 235),
    "lens_mid": (110, 130, 210),
    "lens_dark": (80, 98, 180),
}

# ------------------------------------------------ 传送带几何契约（F-127 核心）
# 与 game/src/EraCore/Presentation/BeltRenderDecisions.cs 的冻结常量一一对应
# （T-119 冻结契约原样复用——终章皮肤只换色板不动几何）：
BODY_TILE_PX = 16        # 带体源 tile 边长（32px 世界格 2× 整数放大，同地形先例）
TURN_FRAMES = 8          # 转弯帧数（14 §3「转弯 8 帧双层 tile 动画」）
PX_PER_FRAME = 2         # 每帧图案前进 px（8 帧×2=16=图案周期 → 无缝循环）
STRAIGHT_PATH_PX = 16    # 直段每格路径长（=源 tile 高，可无缝平铺）
TURN_PATH_PX = 21        # 转弯路径 u∈[0,20] 共 21px（u 律见模块 docstring）
TURN_FRAMES_PER_ROW = 4  # 动画条带 4×2 网格（横条 8 帧为 128px 宽超尺寸白名单）

# 带体表面图案（周期 16，终章深空换装）：暗物质基材两档 + 星门靛蓝导流条
# （idx2 亮/idx10 暗）——E3/E4/E5 同构异色
PAT16 = (
    "void_mid", "void_mid", "indigo_main", "void_mid", "void_mid",
    "void_mid", "void_mid", "void_mid", "void_light", "void_light",
    "indigo_dark", "void_light", "void_light", "void_light", "void_light",
    "void_light",
)


def turn_path_u(x, y):
    """转弯 tile 路径坐标 u（南入北行东出的规范右弯）；带外返回 None。
    与 era3/era4/era5_placeholders.turn_path_u 逐式相同（冻结契约：几何不随皮肤变）。"""
    if 5 <= x <= 10 and 11 <= y <= 15:        # 竖臂（自南缘上行）
        return 15 - y
    if 5 <= x <= 10 and 5 <= y <= 10:         # 弯心方块（对角灯丝）
        return 5 + (x - 5) + (10 - y)
    if 11 <= x <= 15 and 5 <= y <= 10:        # 横臂（东出）
        return 15 + (x - 10)
    return None


class Canvas:
    """最小像素画布：RGBA 网格，初始全透明 (0,0,0,0)。与 era2/3/4/5 合成器
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


# ---------------------------------------------------------------- 深空部件库

def gate_ring(c, cx, cy, half_w, key="indigo_main", rift=True):
    """星门环（终章专属母题，31 号 §1.2）：横带环（塔顶展开的环）+
    可选裂缝注记（环上断口——「塔开成了门」）。
    cx,cy=环心；half_w=半宽。"""
    c.hline(cx - half_w, cy, half_w * 2 + 1, PALETTE[key])
    c.set(cx - half_w, cy - 1, PALETTE["frost_main"])
    c.set(cx + half_w, cy - 1, PALETTE["frost_main"])
    c.set(cx - half_w + 2, cy + 1, PALETTE["lens_mid"])
    c.set(cx + half_w - 2, cy + 1, PALETTE["lens_mid"])
    if rift:
        c.set(cx + (half_w // 2), cy, PALETTE["void_dark"])   # 裂缝断口
        c.set(cx + (half_w // 2) - 1, cy, PALETTE["frost_light"])  # 断口辉光


def rift_mark(c, x, y, h=4, key="frost_light"):
    """裂缝（终章第二母题件）：竖向锯齿断口（h 段，逐段右移 1px）。"""
    for i in range(h):
        c.set(x + (i % 2), y + i, PALETTE[key])


def lens_arc(c, cx, cy, r=5, key="lens_mid"):
    """引力透镜弧：右张弧段（力场弧的终章形变——弧更扁）。固定 5 采样点。"""
    for k in range(5):
        ang = k - 2  # -2..2
        x = cx + int(round(0.97 * r)) - abs(ang)
        y = cy + int(round(0.55 * r * ang / 2))
        c.set(x, y, PALETTE[key])


def starfield(c, points, key="dust_mid"):
    """记忆光尘点阵（固定坐标表）。"""
    for (sx, sy) in points:
        c.set(sx, sy, PALETTE[key])


def nebula_strata(c, x, y, w, h, base="nebula_mid"):
    """星云层积板：斜向层带（2px 周期斜纹——层积纹理语言）。"""
    c.fill(x, y, w, h, PALETTE[base])
    for yy in range(h):
        for xx in range(w):
            if (xx + yy) % 4 < 2:
                c.set(x + xx, y + yy, PALETTE["nebula_dark"])
    c.hline(x, y, w, PALETTE["nebula_light"])
    c.hline(x, y + h - 1, w, PALETTE["nebula_dark"])


def void_panel(c, x, y, w, h, base="void_mid", light="void_light",
               dark="void_dark", seam=True):
    """暗物质铸板：左亮右暗+顶亮底暗+水平层积缝+四角定位点。"""
    c.fill(x, y, w, h, PALETTE[base])
    c.vline(x, y, h, PALETTE[light])
    c.vline(x + w - 1, y, h, PALETTE[dark])
    c.hline(x, y, w, PALETTE[light])
    c.hline(x, y + h - 1, w, PALETTE[dark])
    if seam and h >= 9:
        for py in range(y + 4, y + h - 3, 5):
            c.hline(x + 1, py, w - 2, PALETTE[dark])  # 层积缝
    if w >= 6 and h >= 6:
        for (sx, sy) in ((x + 1, y + 1), (x + w - 2, y + 1),
                         (x + 1, y + h - 2), (x + w - 2, y + h - 2)):
            c.set(sx, sy, PALETTE["nebula_dark"])  # 定位系点


def rift_stud(c, x, y, key="frost_main"):
    """裂缝柱钉：单像素零点白青发射点。"""
    c.set(x, y, PALETTE[key])


# ---------------------------------------------------------------- 两塔（era6/towers.json）

def tower_singularity(level):
    """奇点炮（era6:tower-singularity）48×48（max1 单档）：暗物质基座+奇点
    坍缩腔（星门环口径）+双臂汇聚轨（energy 穿甲档辨识=环腔聚能+无弹道直
    射）。**静态底稿**：canonical=环腔奇点注记（坍缩辉光），脉冲属渲染层。"""
    assert level == 1  # 终章两塔均 max_tier=1（内容表；越级调用=合成器契约违例）
    c = Canvas(48, 48)
    void_panel(c, 10, 36, 28, 10)                            # 暗物质基座
    rift_mark(c, 13, 39, 5)                                  # 裂缝母题徽（终章专属）
    starfield(c, ((24, 41), (30, 42), (33, 40)), "dust_mid")  # 基座光尘
    c.fill(19, 27, 10, 9, PALETTE["void_mid"])               # 炮座环
    c.hline(19, 27, 10, PALETTE["void_light"])
    c.hline(19, 35, 10, PALETTE["void_dark"])
    # 双臂汇聚轨（左/右向环腔收束）
    for (rx0, rx1, step) in ((8, 20, 1), (40, 28, -1)):
        for i in range(4):
            x = rx0 + (rx1 - rx0) * i // 4
            c.fill(min(x, x + 3), 14 + i, 3, 2, PALETTE["void_mid"])
            c.set(min(x, x + 3), 14 + i, PALETTE["indigo_dark"])
    c.vline(20, 8, 12, PALETTE["indigo_dark"])               # 左汇聚轨（立）
    c.vline(21, 8, 12, PALETTE["void_light"])
    c.vline(26, 8, 12, PALETTE["indigo_dark"])               # 右汇聚轨（立）
    c.vline(27, 8, 12, PALETTE["void_dark"])
    for ry in (10, 16, 22):                                  # 汇聚线圈环
        c.hline(21, ry, 6, PALETTE["indigo_dark"])
        c.set(22, ry - 1, PALETTE["lens_mid"])
        c.set(25, ry - 1, PALETTE["lens_mid"])
    # 奇点坍缩腔（星门环口径+奇点）
    gate_ring(c, 24, 6, 5, "indigo_main", rift=False)
    c.fill(23, 5, 3, 3, PALETTE["frost_main"])               # 奇点辉光
    c.set(24, 6, PALETTE["frost_light"])
    c.set(23, 5, PALETTE["dust_light"])
    c.set(25, 7, PALETTE["dust_light"])
    lens_arc(c, 30, 20, 5, "lens_mid")                       # 引力透镜弧注记
    lens_arc(c, 18, 20, 5, "lens_mid")
    return c


def tower_spacetime(level):
    """时空塔（era6:tower-spacetime）32×32（max1 单档）：**星门**=本代母题塔
    （守望塔最终形态——塔顶展开成环+裂缝）；slow 支援档辨识=无炮管+环门口径。
    **静态底稿+动画帧**（T-157 ⑥ 升帧）：本函数=canonical 相位单帧（星门环+
    裂缝注记——决策层静态基准帧，towers/ 落盘即此）；时间涟漪=
    anim/tower-spacetime-anim.png 四相条带（tower_spacetime_frame——第 0 相
    与本帧逐像素同）。"""
    assert level == 1  # 终章两塔均 max_tier=1（内容表；越级调用=合成器契约违例）
    c = Canvas(32, 32)
    void_panel(c, 8, 25, 16, 6)                              # 暗物质基座
    rift_mark(c, 11, 27, 4)                                  # 裂缝母题徽
    rift_stud(c, 21, 28)
    starfield(c, ((16, 28), (13, 26)), "dust_mid")           # 基座光尘
    rows = (  # 守望塔渐收塔身（行, x0, w）——六形态守望塔的最终形态
        (23, 13, 6), (20, 12, 8), (17, 13, 6), (14, 14, 4),
    )
    for (yy, x0, w) in rows:
        c.fill(x0, yy, w, 1, PALETTE["void_light"])
        c.set(x0, yy, PALETTE["lens_dark"])
        c.set(x0 + w - 1, yy, PALETTE["void_dark"])
    c.set(16, 21, PALETTE["indigo_main"])                    # 塔身能量涌点
    c.set(15, 18, PALETTE["lens_mid"])
    c.set(16, 15, PALETTE["indigo_main"])
    # 星门环（塔顶展开成环——母题件）+门内深空
    c.fill(9, 5, 15, 10, PALETTE["void_dark"])               # 门内深空
    starfield(c, ((12, 7), (18, 8), (15, 11), (21, 6), (11, 12)), "dust_mid")
    c.set(15, 9, PALETTE["dust_light"])
    gate_ring(c, 16, 5, 8, "indigo_main", rift=True)         # 上环沿（含裂缝）
    gate_ring(c, 16, 14, 8, "indigo_dark", rift=False)       # 下环沿
    c.vline(8, 6, 8, PALETTE["indigo_main"])                 # 环左右柱
    c.vline(24, 6, 8, PALETTE["indigo_dark"])
    c.set(8, 5, PALETTE["frost_main"])
    c.set(24, 5, PALETTE["frost_main"])
    # 时间涟漪注记（canonical 相位；涟漪=渲染层）
    for (tx, ty) in ((5, 9), (6, 13), (26, 9), (27, 13)):
        c.set(tx, ty, PALETTE["lens_mid"])
    c.set(16, 16, PALETTE["frost_main"])                     # 环门源点
    return c


# ------------------------------------------------ 时空塔动画帧（T-157 ⑥）

TOWER_ANIM_FRAMES = 4          # 时间涟漪四相（0=canonical 静止，1~3=涟漪渐扩）
TOWER_ANIM_PER_ROW = 2         # 条带 2×2 网格（32px 帧 ×4 = 64×64 画布）


def tower_spacetime_frame(phase):
    """时空塔单帧 32×32（时间涟漪第 phase 相，T-157 ⑥——28 号 §6 待做 #3
    同款动画帧挂账的清账）：第 0 相=canonical 静止底稿（与
    towers/tower-spacetime-l1.png 逐像素同——决策层静态基准帧不变）；
    第 1~3 相=环门两侧涟漪梯逐级外扩（每相一对透镜弧竖梯，从环柱向画面
    边缘步进一格）+门内光尘换相位闪烁+环门源点伴辉（第 2 相起）。四相
    循环读作「时间涟漪」——星门开启的周期扰动（纯程序化，无随机数无
    AI 生图）。"""
    assert 0 <= phase < TOWER_ANIM_FRAMES
    c = tower_spacetime(1)
    if phase == 0:
        return c
    # a) 涟漪梯：环柱（x=8/24）外侧逐相一对透镜弧竖梯（y 6..13）
    for k in range(phase):
        c.vline(7 - k, 6, 8, PALETTE["lens_mid"])
        c.vline(24 + k, 6, 8, PALETTE["lens_mid"])
    # b) 门内光尘换相位闪烁（不同相位点亮不同尘点——星门内的时流）
    twinkles = ((13, 10), (19, 7), (14, 12))
    for k in range(phase):
        tx, ty = twinkles[k]
        c.set(tx, ty, PALETTE["dust_light"])
    # c) 环门源点伴辉：第 2 相起下方伴点、第 3 相两侧伴点
    if phase >= 2:
        c.set(16, 17, PALETTE["lens_light"])
    if phase >= 3:
        c.set(15, 16, PALETTE["frost_light"])
        c.set(17, 16, PALETTE["frost_light"])
    return c


def tower_spacetime_strip():
    """时空塔动画条带 64×64（2×2 网格，帧序=行优先 row*2+col）：四相时间
    涟漪循环（0 静止 → 1~3 涟漪渐扩 → 回 0 收束）。单帧 32px×四帧横排=
    128px 超 SIZE-ALLOWLIST，取 2×2 网格（belt-turn-anim 同律）。"""
    strip = Canvas(TOWER_ANIM_PER_ROW * 32, (TOWER_ANIM_FRAMES // TOWER_ANIM_PER_ROW) * 32)
    for k in range(TOWER_ANIM_FRAMES):
        strip.blit(tower_spacetime_frame(k),
                   (k % TOWER_ANIM_PER_ROW) * 32, (k // TOWER_ANIM_PER_ROW) * 32)
    return strip


# ---------------------------------------------------------------- 八敌+Boss（era6/enemies.json）

def _shadow(c, x, y, w):
    """脚下接地阴影（1px 暗物质横带）。"""
    c.hline(x, y, w, PALETTE["void_mid"])


def _hover_glow(c, xs, y):
    """悬浮光晕（深空悬浮单位接地件：靛蓝托线）。"""
    for x in xs:
        c.set(x, y, PALETTE["indigo_dark"])


def _legs(c, xs, y, h=5, key="void_dark"):
    for lx in xs:
        c.fill(lx, y, 2, h, PALETTE[key])


def enemy_void_swarm():
    """虚空蜂群（era6:enemy-void-swarm）32×32：虚空微尘云团——靛蓝/光尘微粒
    聚成漩涡（swarm 辨识：无甲微粒成群——虚空集集体）。"""
    c = Canvas(32, 32)
    _shadow(c, 8, 29, 16)
    motes = (  # 云团轮廓带（外环稀疏）
        (10, 10), (14, 7), (19, 6), (24, 9), (26, 14), (25, 19), (21, 23),
        (15, 24), (10, 21), (8, 15), (12, 13), (17, 10), (22, 12), (24, 16),
        (20, 19), (15, 18), (11, 17), (13, 20), (18, 15), (21, 16),
    )
    for (mx, my) in motes:
        c.set(mx, my, PALETTE["dust_dark"])
    core = ((16, 12), (14, 15), (18, 14), (16, 17), (13, 12), (19, 17))
    for (mx, my) in core:
        c.set(mx, my, PALETTE["indigo_main"])
    for (mx, my) in ((15, 13), (17, 15), (16, 16)):        # 靛蓝涡心
        c.set(mx, my, PALETTE["indigo_light"])
    for (lx, ly) in ((12, 11), (20, 13), (17, 21), (23, 15)):  # 透镜弧闪点
        c.set(lx, ly, PALETTE["lens_mid"])
    c.set(16, 9, PALETTE["frost_main"])                     # 云顶辉光
    c.set(9, 16, PALETTE["void_light"])                     # 云底暗面
    c.set(24, 20, PALETTE["void_light"])
    return c


def enemy_null_frame():
    """虚空机框（era6:enemy-null-frame）32×32：虚空合金机骨+星云胸甲+靛蓝
    单眼（armor=28 中坚辨识：骨架形态+单眼+相位步枪）。"""
    c = Canvas(32, 32)
    _shadow(c, 9, 29, 15)
    c.fill(13, 5, 6, 4, PALETTE["void_light"])              # 头壳
    c.hline(13, 5, 6, PALETTE["lens_dark"])
    c.fill(14, 6, 4, 2, PALETTE["void_dark"])               # 面罩
    c.set(16, 7, PALETTE["frost_main"])                     # 单眼（零点白青）
    c.set(15, 7, PALETTE["indigo_main"])
    c.vline(16, 4, 1, PALETTE["frost_light"])               # 头顶裂缝钉
    nebula_strata(c, 11, 10, 10, 8)                         # 星云胸甲（层积）
    c.set(13, 12, PALETTE["indigo_main"])                   # 胸口能量核
    c.set(14, 12, PALETTE["indigo_light"])
    c.hline(11, 17, 10, PALETTE["void_dark"])               # 骨盆梁
    _legs(c, (12, 18), 18, h=6)                             # 双腿（虚空机骨）
    c.set(12, 24, PALETTE["void_light"])
    c.set(19, 24, PALETTE["void_light"])
    c.fill(21, 11, 7, 2, PALETTE["void_dark"])              # 相位步枪
    c.set(28, 11, PALETTE["indigo_main"])                   # 枪口（相位光）
    c.set(23, 13, PALETTE["nebula_light"])                  # 能匣位
    c.set(22, 13, PALETTE["void_mid"])
    c.set(10, 12, PALETTE["lens_mid"])                      # 背部透镜弧
    c.set(9, 11, PALETTE["lens_light"])
    return c


def enemy_siege_colossus():
    """攻城巨像（era6:enemy-siege-colossus）32×32：重型四足巨像+肩部坍缩桩阵+
    引力巨爪（对建筑 ×3/armor=32 攻城辨识：重甲+破城械）。"""
    c = Canvas(32, 32)
    _shadow(c, 2, 29, 28)
    void_panel(c, 5, 8, 22, 10)                             # 重型 hull（暗物质铸板）
    nebula_strata(c, 8, 10, 6, 3)                           # 星云观察带
    c.set(9, 11, PALETTE["frost_main"])
    c.set(13, 11, PALETTE["indigo_main"])                   # 传感眼
    c.fill(24, 5, 5, 7, PALETTE["void_dark"])               # 肩部坍缩桩阵
    c.hline(24, 5, 5, PALETTE["indigo_main"])
    c.hline(24, 8, 5, PALETTE["indigo_dark"])
    for px_ in (24, 26, 28):
        c.set(px_, 12, PALETTE["dust_mid"])                 # 桩尖（光尘）
    c.fill(2, 11, 5, 7, PALETTE["void_dark"])               # 引力巨爪
    c.set(1, 10, PALETTE["lens_dark"])
    c.set(1, 13, PALETTE["lens_dark"])
    c.set(1, 16, PALETTE["lens_dark"])
    c.vline(4, 12, 4, PALETTE["void_mid"])                  # 引力杆（透镜弧）
    c.fill(6, 18, 4, 4, PALETTE["void_mid"])                # 四足（反关节）
    c.fill(4, 22, 4, 3, PALETTE["void_dark"])
    c.fill(3, 25, 6, 2, PALETTE["void_mid"])
    c.fill(19, 18, 4, 4, PALETTE["void_mid"])
    c.fill(21, 22, 4, 3, PALETTE["void_dark"])
    c.fill(20, 25, 6, 2, PALETTE["void_mid"])
    c.set(6, 6, PALETTE["indigo_light"])                    # 排热口（靛蓝）
    c.set(7, 5, PALETTE["indigo_dark"])
    c.set(20, 18, PALETTE["indigo_dark"])                   # 足部接地光
    c.set(5, 25, PALETTE["indigo_dark"])
    return c


def enemy_phase_striker():
    """相位突击体（era6:enemy-phase-striker）32×32：无腿相位镖体+后部虚空
    尾迹+相位撕裂口（opening_charge 辨识：疾驰+相位闪烁）。"""
    c = Canvas(32, 32)
    _hover_glow(c, range(6, 26), 28)
    c.fill(8, 14, 16, 5, PALETTE["void_light"])             # 相位镖体
    c.hline(8, 14, 16, PALETTE["lens_dark"])
    c.hline(8, 18, 16, PALETTE["void_dark"])
    c.fill(24, 15, 5, 3, PALETTE["void_mid"])               # 前锥（朝右前倾）
    c.hline(24, 15, 5, PALETTE["lens_dark"])
    c.set(28, 16, PALETTE["frost_main"])                    # 锥顶撞角（零点白青）
    c.set(27, 15, PALETTE["frost_light"])
    rift_mark(c, 12, 15, 4, "frost_light")                  # 相位撕裂口（裂缝）
    c.set(14, 16, PALETTE["indigo_main"])
    c.fill(11, 15, 2, 2, PALETTE["nebula_mid"])             # 座舱条（星云）
    c.set(12, 16, PALETTE["nebula_light"])
    c.fill(5, 15, 3, 4, PALETTE["void_dark"])               # 后部相位推进器
    for (tx, ty) in ((2, 15), (3, 16), (2, 17)):            # 虚空尾迹
        c.set(tx, ty, PALETTE["indigo_dark"])
    c.set(1, 16, PALETTE["indigo_main"])
    c.set(4, 14, PALETTE["lens_dark"])
    c.set(4, 19, PALETTE["lens_dark"])
    c.set(9, 12, PALETTE["indigo_dark"])                    # 背鳍
    c.set(10, 11, PALETTE["indigo_main"])
    c.set(11, 12, PALETTE["indigo_dark"])
    c.set(14, 20, PALETTE["void_mid"])                      # 腹侧导流片
    c.set(18, 20, PALETTE["void_mid"])
    return c


def enemy_lancer_array():
    """长枪阵列（era6:enemy-lancer-array）32×32：悬浮机体+三联超导长枪阵列
    （架枪定点射辨识=长枪列+悬停姿态，stationary_ranged_attack 行形对位）。"""
    c = Canvas(32, 32)
    _hover_glow(c, range(8, 24), 28)
    c.fill(12, 12, 8, 7, PALETTE["void_mid"])               # 机体
    c.hline(12, 12, 8, PALETTE["void_light"])
    c.fill(14, 14, 4, 3, PALETTE["void_dark"])              # 环形传感
    c.set(15, 15, PALETTE["frost_main"])                    # 锁定眼
    c.set(16, 15, PALETTE["indigo_main"])
    for (ax, ay) in ((8, 11), (23, 11), (8, 20), (23, 20)):
        c.hline(ax, ay, 3, PALETTE["void_dark"])            # 四悬臂
    for (rx, ry) in ((5, 9), (24, 9), (5, 22), (24, 22)):   # 悬浮转子（靛蓝光圈）
        c.set(rx, ry, PALETTE["indigo_main"])
        c.set(rx + 1, ry - 1, PALETTE["indigo_light"])
        c.set(rx - 1, ry - 1, PALETTE["indigo_dark"])
    # 三联超导长枪阵列（横贯三杆）
    for (gy, gk) in ((14, "void_light"), (16, "indigo_main"), (18, "void_light")):
        c.hline(2, gy, 28, PALETTE[gk])
        c.set(1, gy, PALETTE["nebula_light"])               # 枪尾相位环
        c.set(29, gy, PALETTE["frost_light"])               # 枪尖（出射点）
    c.set(30, 16, PALETTE["dust_light"])
    for gx in (8, 14, 20):                                  # 枪身耦合节
        c.set(gx, 15, PALETTE["lens_mid"])
    c.set(16, 21, PALETTE["nebula_mid"])                    # 下挂能匣
    return c


def enemy_aegis_paladin():
    """圣盾机甲（era6:enemy-aegis-paladin）32×32：通高圣盾力场（透镜弧场+
    靛蓝发射柱）+重装机骨（shield_wall 辨识：圣盾覆身——正面减伤 20% 行形）。"""
    c = Canvas(32, 32)
    _shadow(c, 9, 29, 16)
    c.fill(21, 5, 5, 4, PALETTE["void_light"])              # 头壳（盾后出露）
    c.set(24, 7, PALETTE["lens_mid"])
    c.vline(23, 3, 2, PALETTE["frost_main"])
    nebula_strata(c, 21, 9, 5, 7)                           # 星云重装躯干
    c.set(23, 11, PALETTE["indigo_main"])
    c.hline(21, 16, 5, PALETTE["void_dark"])
    _legs(c, (21, 24), 17, key="void_dark")
    c.fill(4, 3, 2, 22, PALETTE["indigo_dark"])             # 圣盾发射柱（上下）
    c.hline(3, 3, 4, PALETTE["indigo_main"])
    c.hline(3, 24, 4, PALETTE["indigo_main"])
    c.set(5, 12, PALETTE["indigo_light"])                   # 柱耦合节点
    c.set(5, 16, PALETTE["indigo_light"])
    c.fill(6, 4, 12, 19, PALETTE["lens_dark"])              # 圣盾力场（透镜弧场）
    lens_arc(c, 9, 10, 4, "lens_light")                     # 场内透镜弧
    lens_arc(c, 9, 17, 4, "lens_light")
    for (dx_, dy_) in ((8, 6), (15, 12), (10, 21)):         # 光尘闪点
        c.set(dx_, dy_, PALETTE["dust_mid"])
    c.set(17, 6, PALETTE["frost_main"])                     # 场面辉光
    c.set(8, 21, PALETTE["frost_main"])
    c.fill(26, 10, 2, 7, PALETTE["void_dark"])              # 背部能匣架
    c.set(26, 9, PALETTE["indigo_main"])
    return c


def enemy_entropy_wraith():
    """熵蚀幽体（era6:enemy-entropy-wraith）32×32：四旋翼+下挂熵蚀相变腔
    （暗物质坍缩液）+滴落注记（death_explode 辨识：载荷腔+警示裂缝）。"""
    c = Canvas(32, 32)
    _hover_glow(c, range(9, 23), 28)
    c.fill(12, 10, 8, 6, PALETTE["void_mid"])               # 机体
    c.hline(12, 10, 8, PALETTE["void_light"])
    c.set(14, 12, PALETTE["lens_mid"])                      # 传感对
    c.set(17, 12, PALETTE["lens_mid"])
    for (ax, ay) in ((7, 9), (20, 9), (7, 17), (20, 17)):
        c.hline(ax, ay, 4, PALETTE["void_dark"])            # 四臂
    for (rx, ry) in ((4, 7), (23, 7), (4, 19), (23, 19)):   # 旋翼光圈（靛蓝）
        c.hline(rx, ry, 4, PALETTE["indigo_dark"])
        c.set(rx, ry - 1, PALETTE["indigo_main"])
    c.fill(13, 16, 6, 6, PALETTE["nebula_dark"])            # 熵蚀相变腔（星云暗）
    c.fill(14, 17, 4, 4, PALETTE["indigo_dark"])            # 坍缩液（靛蓝暗）
    c.set(15, 18, PALETTE["indigo_main"])
    c.hline(13, 16, 6, PALETTE["frost_main"])               # 腔箍（零点白青警示）
    c.hline(13, 21, 6, PALETTE["lens_dark"])
    rift_mark(c, 15, 22, 3, "frost_light")                  # 滴落裂缝注记
    c.set(16, 25, PALETTE["indigo_main"])
    c.set(13, 9, PALETTE["frost_light"])                    # 顶警灯
    return c


def enemy_choir_core():
    """合唱核心（era6:enemy-choir-core）32×32：悬浮八面核心+环绕星门环+合唱
    波纹弧（war_drums 辨识：指挥网中枢——环+多声部波纹）。"""
    c = Canvas(32, 32)
    _hover_glow(c, range(10, 22), 29)
    c.fill(13, 8, 6, 2, PALETTE["dust_light"])              # 八面核（上棱）
    c.fill(11, 10, 10, 8, PALETTE["dust_dark"])             # 核体
    c.fill(13, 18, 6, 2, PALETTE["void_light"])             # 下棱
    c.vline(11, 10, 8, PALETTE["dust_mid"])
    c.vline(20, 10, 8, PALETTE["void_light"])
    c.fill(14, 12, 4, 4, PALETTE["void_dark"])              # 核心窗（深空）
    c.set(15, 13, PALETTE["frost_main"])                    # 核心（零点白青）
    c.set(16, 13, PALETTE["indigo_main"])
    c.set(15, 14, PALETTE["nebula_light"])                  # 指令瞳（星云亮）
    gate_ring(c, 16, 7, 7, "indigo_dark", rift=False)       # 环绕星门环（上）
    gate_ring(c, 16, 21, 7, "indigo_dark", rift=True)       # 环绕星门环（下，含裂缝）
    c.set(9, 7, PALETTE["indigo_main"])
    c.set(22, 21, PALETTE["indigo_main"])
    c.vline(16, 5, 3, PALETTE["void_mid"])                  # 顶天线
    c.set(16, 4, PALETTE["frost_light"])
    for (bx, by, k) in ((6, 12, "lens_light"), (26, 12, "lens_light"),
                        (8, 16, "lens_mid"), (24, 16, "lens_mid"),
                        (10, 20, "indigo_dark"), (22, 20, "indigo_dark")):
        c.set(bx, by, PALETTE[k])                           # 合唱波纹（多声部弧）
    c.set(12, 11, PALETTE["void_dark"])                     # 核肩散热
    c.set(19, 11, PALETTE["void_dark"])
    return c


def boss_fallen_watcher():
    """堕落守望者（era6:enemy-boss）96×112（设计案 §13.1 终章 Boss 逐字锚点：
    canvas_size_px=96 宽 + canvas_height_px=112 高双字段对齐，T-136 升格——
    SIZE-ALLOWLIST 扩 96/112 随 REV-20261018-T136 登记）：被千年记忆压垮的
    初代守望者——宽体暗物质蒙皮+胸口守望者之眼（星门环口径+靛蓝瞳）+重力
    脉冲同心弧（gravity_pulse 辨识）+双肩坍缩塔+顶冠残环（星门母题的堕落
    形态——环已破裂）。纵向 112=终章专属纵幅：冠环-舰体-脉冲弧三段拉满
    （千年重量的视觉高度）。"""
    c = Canvas(96, 112)
    _shadow(c, 12, 106, 72)
    span = (  # 悬浮宽体平面形（行 y, 半宽 hw；cx=48）——菱形前缘+展肩
        (34, 6), (35, 9), (36, 13), (37, 17), (38, 21), (39, 25), (40, 28),
        (41, 31), (42, 33), (43, 35), (44, 36), (45, 37), (46, 37), (47, 37),
        (48, 37), (49, 37), (50, 37), (51, 37), (52, 37), (53, 36), (54, 35),
        (55, 33), (56, 31), (57, 28), (58, 25), (59, 21), (60, 17), (61, 13),
        (62, 9),
    )
    for (yy, hw) in span:
        c.hline(48 - hw, yy, hw * 2 + 1, PALETTE["void_dark"])   # 暗物质蒙皮
    for (yy, hw) in span:  # 前缘星云层积缝（两缘竖铺层带）
        c.set(48 - hw, yy, PALETTE["void_mid"])
        c.set(48 + hw, yy, PALETTE["void_mid"])
    c.fill(33, 38, 30, 26, PALETTE["void_dark"])            # 中央舰体隆起
    for i in range(11):                                     # 舰体层积缝
        c.set(33 + i, 38 + i, PALETTE["void_mid"])
        c.set(62 - i, 38 + i, PALETTE["void_mid"])
    # 守望者之眼：星门环口径 + 靛蓝瞳（重力脉冲源）——守望塔六形态之眼
    c.fill(41, 44, 14, 12, PALETTE["nebula_dark"])          # 眼窝（星云暗）
    c.hline(40, 45, 16, PALETTE["indigo_main"])             # 眼环（上）
    c.hline(40, 46, 16, PALETTE["indigo_main"])             # 眼环（下）
    c.set(40, 44, PALETTE["indigo_light"])
    c.set(55, 44, PALETTE["indigo_light"])
    c.fill(44, 49, 8, 4, PALETTE["indigo_main"])            # 瞳（星门靛蓝）
    c.set(45, 49, PALETTE["indigo_light"])
    rift_mark(c, 44, 43, 4, "frost_light")                  # 眼上裂缝（千年记忆）
    gate_ring(c, 48, 24, 15, "indigo_dark", rift=True)      # 顶冠残环（已破裂）
    c.set(33, 24, PALETTE["indigo_main"])
    c.set(63, 24, PALETTE["indigo_main"])
    c.vline(48, 25, 5, PALETTE["void_mid"])                 # 冠杆
    for px_ in (24, 72):                                    # 双肩坍缩塔
        c.fill(px_ - 2, 40, 7, 12, PALETTE["void_mid"])
        c.hline(px_ - 2, 40, 7, PALETTE["indigo_main"])
        c.set(px_, 51, PALETTE["indigo_light"])
    for (ex, ey) in ((28, 58), (64, 58)):                   # 两侧相位推进口
        c.fill(ex, ey, 5, 7, PALETTE["indigo_dark"])
        c.set(ex + 2, ey + 7, PALETTE["indigo_main"])
    # 重力脉冲同心弧（gravity_pulse：900t 周期 3.5 格压制——canonical 双弧注记）
    for (ay, k) in ((76, "indigo_dark"), (82, "indigo_main")):
        c.hline(34, ay, 28, PALETTE[k])
        c.set(31, ay - 1, PALETTE[k])
        c.set(65, ay - 1, PALETTE[k])
        c.set(28, ay - 2, PALETTE["indigo_dark"])
        c.set(69, ay - 2, PALETTE["indigo_dark"])
    c.hline(38, 88, 20, PALETTE["indigo_dark"])             # 第三弧（收束）
    c.set(35, 87, PALETTE["indigo_dark"])
    c.set(59, 87, PALETTE["indigo_dark"])
    for (sx, sy) in ((24, 50), (36, 44), (58, 44), (69, 50)):
        c.set(sx, sy, PALETTE["lens_mid"])                  # 蒙皮透镜微光
    for (dx_, dy_) in ((18, 54), (76, 54), (30, 66)):       # 记忆光尘（飘散）
        c.set(dx_, dy_, PALETTE["dust_mid"])
    c.set(47, 22, PALETTE["frost_main"])                    # 机顶频闪（贴残环）
    c.set(48, 22, PALETTE["frost_light"])
    return c


# ---------------------------------------------------------------- 载具（F-042 终章档）

def teleporter_right():
    """传送阵（终章载具，era.json logistics.carrier=teleporter；∞ 档并列机动
    运输形态）32×32（朝右）：悬浮星门环舱+暗物质货舱+零点白青导流（深空色板
    正式档——era5 drone-right 的传送对位件）。"""
    c = Canvas(32, 32)
    _hover_glow(c, range(4, 28), 28)
    void_panel(c, 3, 8, 14, 10, seam=False)                 # 货舱（暗物质铸板）
    c.hline(3, 13, 14, PALETTE["indigo_main"])              # 舱体靛蓝条带
    rift_mark(c, 7, 16, 3)                                  # 舱侧裂缝徽（小号母题）
    c.fill(18, 10, 9, 7, PALETTE["void_mid"])               # 机体舱（传送段）
    c.hline(18, 10, 9, PALETTE["void_light"])
    c.vline(18, 10, 7, PALETTE["void_light"])
    c.fill(20, 12, 5, 3, PALETTE["lens_dark"])              # 导流条（透镜弧）
    c.set(21, 13, PALETTE["lens_light"])
    c.set(19, 9, PALETTE["frost_main"])                     # 顶置相位灯
    c.set(25, 9, PALETTE["frost_main"])
    c.fill(27, 12, 2, 3, PALETTE["indigo_dark"])            # 前传送环
    c.set(29, 13, PALETTE["indigo_main"])                   # 前灯
    c.set(30, 13, PALETTE["frost_light"])
    c.set(2, 11, PALETTE["nebula_light"])                   # 尾灯
    # 顶悬浮星门环×2（传送阵列）
    for (gx, gw) in ((5, 2), (13, 2), (21, 3)):
        c.hline(gx - 1, 5, gw + 2, PALETTE["indigo_dark"])
        c.set(gx, 4, PALETTE["indigo_main"])
        c.set(gx + gw - 1, 4, PALETTE["indigo_main"])
    c.set(10, 19, PALETTE["void_mid"])                      # 腹侧导流
    c.set(14, 19, PALETTE["void_mid"])
    return c


# ---------------------------------------------------------------- UI 色板（F-132 系）

def panel_palette():
    """终章面板色板 32×32：星门靛蓝描边+暗物质底+4×4 色块矩阵（靛蓝/星云/
    光尘/基材四行），供 C-22 面板终章换肤取色（色板底稿，非成品 UI 图）。"""
    c = Canvas(32, 32)
    c.fill(0, 0, 32, 32, PALETTE["void_dark"])
    c.rect(0, 0, 32, 32, None, PALETTE["indigo_main"])
    c.rect(2, 2, 28, 28, None, PALETTE["indigo_dark"])
    c.fill(4, 4, 24, 24, PALETTE["void_mid"])
    rows = (
        ("indigo_light", "indigo_main", "indigo_dark", "frost_main"),
        ("nebula_light", "nebula_mid", "nebula_dark", "lens_dark"),
        ("dust_light", "dust_mid", "dust_dark", "lens_mid"),
        ("void_light", "void_mid", "void_dark", "frost_light"),
    )
    for ry, row in enumerate(rows):
        for rx, key in enumerate(row):
            c.fill(5 + rx * 6, 5 + ry * 6, 5, 5, PALETTE[key])
    return c


# ---------------------------------------------------------------- 地形 9 槽（16×16）

def tile_floor_voidstone():
    """虚空岩床（Plain 主槽）：暗物质基面+星云层积纹+光尘点。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["void_mid"])
    for y in range(16):
        for x in range(16):
            if (x + y) % 6 < 2:
                c.set(x, y, PALETTE["nebula_dark"])         # 层积纹
    c.hline(0, 0, 16, PALETTE["void_light"])
    c.vline(0, 0, 16, PALETTE["void_light"])
    c.hline(0, 15, 16, PALETTE["void_dark"])
    c.vline(15, 0, 16, PALETTE["void_dark"])
    c.hline(0, 7, 16, PALETTE["void_dark"])                 # 层积缝（横）
    c.vline(8, 0, 7, PALETTE["void_dark"])                  # 纵缝（上半）
    c.vline(3, 8, 8, PALETTE["void_dark"])                  # 纵缝（下半错缝）
    c.vline(12, 8, 8, PALETTE["void_dark"])
    for (dx_, dy_) in ((5, 3), (12, 5), (6, 11), (13, 12)):
        c.set(dx_, dy_, PALETTE["dust_mid"])                # 记忆光尘
    c.set(5, 4, PALETTE["dust_light"])
    c.set(13, 11, PALETTE["dust_light"])
    for (px_, py_) in ((2, 2), (14, 10)):
        c.set(px_, py_, PALETTE["nebula_dark"])             # 定位系点
    c.set(9, 12, PALETTE["void_light"])                     # 层积抹痕
    return c


def tile_floor_nebula():
    """星云地砖（Forest 代位——深空无林地，星云层积场代绿带；
    era3 grated/era4 grated/era5 lattice 同构异色）。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["nebula_dark"])
    for y in range(16):
        for x in range(16):
            band = (x - y) % 4
            if band == 0:
                c.set(x, y, PALETTE["nebula_mid"])          # 星云斜层带
    for (dx_, dy_) in ((4, 4), (10, 4), (2, 10), (8, 10), (14, 10), (5, 13), (12, 13)):
        c.set(dx_, dy_, PALETTE["dust_mid"])                # 光尘点（密阵）
    for (dx_, dy_) in ((7, 6), (13, 6), (3, 13)):
        c.set(dx_, dy_, PALETTE["dust_light"])              # 亮点
    c.hline(0, 0, 16, PALETTE["nebula_mid"])
    c.vline(15, 0, 16, PALETTE["nebula_dark"])
    return c


def tile_road_lightray(dash_offset):
    """光线路地砖（Road）：暗物质面+零点白青光线带+靛蓝导流虚线（offset 区分
    正/副槽）——「引力透镜弧」纹理语言的地形件。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["void_mid"])
    for (sx, sy, key) in ((5, 4, "void_light"), (12, 5, "void_light"),
                          (3, 10, "void_dark"), (14, 11, "void_light"),
                          (8, 9, "void_dark"), (6, 13, "void_light")):
        c.set(sx, sy, PALETTE[key])                         # 基材杂点
    c.hline(0, 1, 16, PALETTE["dust_dark"])                 # 路缘线（双缘）
    c.hline(0, 14, 16, PALETTE["dust_dark"])
    for dx_ in (2 + dash_offset, 9 + dash_offset):          # 中线光线（沿行进向）
        c.fill(dx_, 7, 4, 2, PALETTE["frost_main"])
        c.set(dx_, 7, PALETTE["frost_light"])
    if dash_offset:                                         # 副槽=透镜耦合节点
        c.fill(11, 2, 3, 3, PALETTE["indigo_dark"])
        c.set(12, 3, PALETTE["lens_light"])
    return c


def tile_wall_stargate(lit_variant):
    """星门墙砖（Wall）：双发射柱+透镜弧场体+靛蓝弧光+底部暗物质裙带；
    lit_variant=True 副槽=门场点亮（frost 满场）。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["void_mid"])               # 场框底
    c.vline(0, 0, 16, PALETTE["void_dark"])                 # 双发射柱（左右）
    c.vline(1, 0, 16, PALETTE["indigo_dark"])
    c.vline(14, 0, 16, PALETTE["void_dark"])
    c.vline(15, 0, 16, PALETTE["indigo_dark"])
    field = PALETTE["frost_main"] if lit_variant else PALETTE["lens_dark"]
    c.fill(2, 1, 12, 14, field)                             # 透镜弧场体
    lens_arc(c, 5, 5, 3, "lens_light")                      # 场内透镜弧
    lens_arc(c, 10, 11, 3, "lens_light")
    for (ix, iy) in ((6, 9), (11, 4)):                      # 场内裂缝注记
        rift_mark(c, ix, iy, 3, "frost_light" if not lit_variant else "indigo_light")
    if lit_variant:
        c.set(5, 7, PALETTE["frost_light"])                 # 点亮辉光
        c.set(10, 4, PALETTE["frost_light"])
    c.hline(2, 15, 12, PALETTE["void_dark"])                # 暗物质裙带
    c.set(4, 15, PALETTE["indigo_dark"])
    c.set(11, 15, PALETTE["indigo_dark"])
    return c


def tile_floor_rubble_void(dark_variant):
    """虚空废墟地砖（Rock 代位）：暗物质底+虚空碎板+断口光尘线。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["void_mid"])
    chunks = ((2, 2, 3, 2, "void_light"), (7, 3, 4, 2, "nebula_mid"),
              (12, 2, 2, 3, "nebula_mid"), (3, 8, 4, 3, "nebula_mid"),
              (9, 9, 3, 2, "void_light"), (13, 12, 2, 2, "void_dark"),
              (6, 13, 3, 2, "void_dark"), (1, 13, 2, 2, "nebula_mid"))
    if dark_variant:
        chunks = ((3, 2, 2, 3, "void_dark"), (8, 2, 3, 2, "nebula_mid"),
                  (13, 4, 2, 2, "void_light"), (2, 8, 3, 2, "void_light"),
                  (7, 10, 4, 3, "void_dark"), (12, 9, 3, 2, "nebula_mid"),
                  (3, 13, 3, 2, "nebula_mid"), (14, 14, 2, 2, "void_dark"))
    for (x, y, w, h, key) in chunks:
        c.fill(x, y, w, h, PALETTE[key])
        c.set(x, y + h - 1, PALETTE["void_dark"])
    c.vline(5, 5, 4, PALETTE["indigo_dark"])                # 断口光尘线
    c.set(5, 5, PALETTE["nebula_light"])
    c.hline(9, 7, 4, PALETTE["indigo_dark"])
    c.set(12, 7, PALETTE["nebula_light"])
    for (mx, my) in ((8, 6), (13, 10), (4, 12)):
        c.set(mx, my, PALETTE["dust_mid"])                  # 游离光尘
    return c


def tile_water_voidsea():
    """虚空海（Water）：虚空能量海（周期 8 斜带，era2/3/4/5 water 同构异色）
    +星云波光。"""
    c = Canvas(16, 16)
    for y in range(16):
        for x in range(16):
            band = (x + y) % 8
            key = "lens_dark" if band in (0, 1) else ("indigo_dark" if band == 4 else "void_dark")
            c.set(x, y, PALETTE[key])
    for (x, y) in ((3, 2), (11, 6), (7, 12)):
        c.set(x, y, PALETTE["frost_light"])                 # 波光高光
    c.set(5, 9, PALETTE["indigo_main"])                     # 岸线反染
    return c


# ---------------------------------------------------------------- 传送带双层（终章皮肤）

def belt_body():
    """直段带体 16×16（动画层，终章皮肤）：竖向条带 cols 5..10，
    行色=PAT16[y%16]——沿 V 方向周期 16=源 tile 高 → shader UV 滚动可无缝
    平铺（几何与 E3/E4/E5 皮肤逐式相同，仅色板换装）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    for y in range(BODY_TILE_PX):
        for x in range(5, 11):
            c.set(x, y, PALETTE[PAT16[y % 16]])
    return c


def belt_edge():
    """直段带边 16×16（静态层，终章皮肤）：双侧护栏+裂缝柱钉，带外全透明
    （与带体零重叠——护栏=暗物质立缘，柱钉=零点白青裂缝钉，E3/E4/E5 同构）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    c.vline(3, 0, 16, PALETTE["void_dark"])
    c.vline(4, 0, 16, PALETTE["nebula_dark"])
    c.vline(11, 0, 16, PALETTE["nebula_dark"])
    c.vline(12, 0, 16, PALETTE["void_dark"])
    for y in (1, 5, 9, 13):  # 护栏裂缝柱钉
        rift_stud(c, 4, y)
        rift_stud(c, 11, y)
    return c


def belt_turn_edge():
    """弯口带边 16×16（静态层，终章皮肤）：规范右弯（南入东出）外/内弯护栏
    +对角补角，与带体（u∈[0,20] 条带）零重叠——不透明集与 E3/E4/E5 皮肤逐
    坐标相同（冻结契约：几何不随皮肤变）。左弯=渲染层水平镜像（决策层 FlipX）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    # 外弯（西北侧）：竖段 col3/4 rows6..15 + 对角 (4,5)(4,4) + 横段 row3/4 cols6..15
    c.vline(3, 6, 10, PALETTE["void_dark"])
    c.vline(4, 6, 10, PALETTE["nebula_dark"])
    c.set(4, 5, PALETTE["void_dark"])
    c.set(4, 4, PALETTE["void_dark"])
    c.set(5, 4, PALETTE["nebula_dark"])
    c.hline(6, 3, 10, PALETTE["void_dark"])
    c.hline(6, 4, 10, PALETTE["nebula_dark"])
    # 内弯（东南侧）：竖段 col11/12 rows11..15 + 横段 row11/12 cols11..15
    c.vline(11, 11, 5, PALETTE["nebula_dark"])
    c.vline(12, 11, 5, PALETTE["void_dark"])
    c.hline(11, 11, 5, PALETTE["nebula_dark"])
    c.hline(12, 12, 5, PALETTE["void_dark"])
    for y in (9, 13):  # 裂缝柱钉
        rift_stud(c, 4, y)
    for x in (9, 13):
        rift_stud(c, x, 4)
    rift_stud(c, 11, 13)
    rift_stud(c, 13, 11)
    return c


def belt_turn_frame(phase):
    """转弯单帧 16×16（动画层，终章皮肤）：条带=竖臂∪弯心∪横臂
    （turn_path_u 定义域），每像素色=PAT16[(u−phase) mod 16]——
    phase=帧号×PX_PER_FRAME（u 律与 E3/E4/E5 皮肤逐式相同）。"""
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
    与 C# Era6ArtMappingTests 双侧断言）。"""
    strip = Canvas(TURN_FRAMES_PER_ROW * BODY_TILE_PX,
                   (TURN_FRAMES // TURN_FRAMES_PER_ROW) * BODY_TILE_PX)
    for k in range(TURN_FRAMES):
        strip.blit(belt_turn_frame(k * PX_PER_FRAME),
                   (k % TURN_FRAMES_PER_ROW) * BODY_TILE_PX,
                   (k // TURN_FRAMES_PER_ROW) * BODY_TILE_PX)
    return strip


# ---------------------------------------------------------------- 驱动

TOWERS = {
    "tower-singularity": tower_singularity,      # max_tier1 → l1 单档（高能档 48px）
    "tower-spacetime": tower_spacetime,          # max_tier1 → l1 单档（星门母题塔）
}
TOWER_LEVELS = {
    "tower-singularity": (1,),
    "tower-spacetime": (1,),
}

ENEMIES = {
    "enemy-void-swarm": enemy_void_swarm,
    "enemy-null-frame": enemy_null_frame,
    "enemy-siege-colossus": enemy_siege_colossus,
    "enemy-phase-striker": enemy_phase_striker,
    "enemy-lancer-array": enemy_lancer_array,
    "enemy-aegis-paladin": enemy_aegis_paladin,
    "enemy-entropy-wraith": enemy_entropy_wraith,
    "enemy-choir-core": enemy_choir_core,
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
    boss = os.path.join(out_root, "enemies", "boss-fallen-watcher.png")
    boss_fallen_watcher().write(boss)
    wrote.append(boss)
    tp = os.path.join(out_root, "units", "teleporter-right.png")
    teleporter_right().write(tp)
    wrote.append(tp)
    ui = os.path.join(out_root, "ui", "panel-era6.png")
    panel_palette().write(ui)
    wrote.append(ui)
    tiles = {
        "floor-voidstone": lambda: tile_floor_voidstone(),
        "floor-nebula": lambda: tile_floor_nebula(),
        "road-lightray": lambda: tile_road_lightray(0),
        "road-lightray-alt": lambda: tile_road_lightray(1),
        "wall-stargate": lambda: tile_wall_stargate(False),
        "wall-stargate-alt": lambda: tile_wall_stargate(True),
        "floor-rubble-void": lambda: tile_floor_rubble_void(False),
        "floor-rubble-void-alt": lambda: tile_floor_rubble_void(True),
        "water-voidsea": lambda: tile_water_voidsea(),
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
    tower_anim = os.path.join(out_root, "anim", "tower-spacetime-anim.png")
    tower_spacetime_strip().write(tower_anim)
    wrote.append(tower_anim)
    return wrote


# ---------------------------------------------------------------- 自检

def _sha256(path):
    with open(path, "rb") as fh:
        return hashlib.sha256(fh.read()).hexdigest()


def check_turn_strip(out_root):
    """转弯 8 帧帧边界对齐断言（终章皮肤，F-127 验收核心）：
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
        second = os.path.join(tmp, "era6")
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


def check_tower_anim_strip():
    """时空塔四相条带断言（T-157 ⑥）：①画布 64×64（2×2 网格 32px 帧）；
    ②第 0 相=canonical 静止底稿（与 tower_spacetime(1) 逐像素同——
    决策层静态基准帧契约）；③四相两两不同（循环非退化）。返回 (ok, 说明)。"""
    strip = tower_spacetime_strip()
    if (strip.width, strip.height) != (64, 64):
        return False, f"strip size {strip.width}x{strip.height} != 64x64"
    canonical = tower_spacetime(1)
    frames = []
    for k in range(TOWER_ANIM_FRAMES):
        fx, fy = (k % TOWER_ANIM_PER_ROW) * 32, (k // TOWER_ANIM_PER_ROW) * 32
        frames.append({(x, y): strip.get(fx + x, fy + y)
                       for y in range(32) for x in range(32)})
    base = {(x, y): canonical.get(x, y) for y in range(32) for x in range(32)}
    if frames[0] != base:
        return False, "phase0 != canonical static base"
    for a in range(TOWER_ANIM_FRAMES):
        for b in range(a + 1, TOWER_ANIM_FRAMES):
            if frames[a] == frames[b]:
                return False, f"phase{a} == phase{b} (degenerate)"
    return True, "4-phase time ripple, phase0=canonical"


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
    tower_ok, tower_msg = check_tower_anim_strip()
    count, mismatches = check_determinism(out_root)
    if rejects or not belt_ok or not tower_ok or mismatches:
        print(f"self-test FAIL: static_rejects={len(rejects)} "
              f"belt_ok={belt_ok}({belt_msg}) tower_ok={tower_ok}({tower_msg}) "
              f"determinism_mismatch={len(mismatches)}")
        return 1, report, belt_msg
    print(f"self-test ok: static_files={report['files']} rejected=0 warned={report['warned']} "
          f"belt: {belt_msg} tower: {tower_msg} determinism={count}/{count} sha256-equal")
    return 0, report, belt_msg


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="终章占位美术程序合成器（T-142）：两塔/八敌+堕落守望者/"
                    "传送阵/UI 色板/地形 9 槽/传送带终章皮肤（∞ 档双层动画）")
    parser.add_argument("--out", default=None,
                        help="输出根目录（默认=presentation/EraTD/textures/era6）")
    parser.add_argument("--self-test", action="store_true",
                        help="生成后调 asset_validator+8 帧对齐+确定性自校验（任一失败非 0 退出）")
    args = parser.parse_args(argv)

    if args.out:
        out_root = os.path.normpath(args.out)
    else:
        out_root = os.path.normpath(os.path.join(
            os.path.dirname(os.path.abspath(__file__)),
            "..", "..", "game", "presentation", "EraTD", "textures", "era6"))

    for p in build_all(out_root):
        print(f"wrote {p}")

    if args.self_test:
        rc, _report, _msg = run_self_test(out_root)
        return rc
    return 0


if __name__ == "__main__":
    sys.exit(main())
