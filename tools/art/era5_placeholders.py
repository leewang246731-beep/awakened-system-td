#!/usr/bin/env python3
"""
era5_placeholders.py — E5 占位美术程序合成器（任务 T-142，Wave 8，E 泳道，组件 C-28）

用途：为 E5（未来时代）确定性合成占位像素图——三塔×升级位（轨道炮/无人机巢/
力场塔，era5/towers.json）、八敌+主宰 Boss（era5/enemies.json）、无人机载具
（era.json logistics.carrier=drone，20 件/s 档）与传送带 E5 皮肤（logistics 第
5 档=20 件/s：belt 纹理色板换装，复用 T-119 双层动画全套几何契约）、E5 面板
色板（C-22 换肤底稿）、未来地形子集（纳米地坪/光流路/力场墙 9 槽）。
背景：T-128 era4 全程序合成先例第 2 次重演（E5 无 CC0 成套素材源，按 T-078
预批准回退路线 100%「代码合成像素图」）；与 era4 同构=母题形变×色彩层积。
红线对齐：这是**程序绘制**（确定性：固定坐标+固定 18 色未来子板，无随机数、
无 AI 生图），与 21 号 §2.1「AI PNG 不进 presentation/游戏 sprite 管线」红线
不冲突；产物可由本脚本逐字节复现，人工重绘升级时以本脚本为底稿。

视觉身份（31 号 §1.2 E5 行）：主色聚变青 #3FC1C9 / 辅聚变紫 #8A5FC9、纳米银白
#DCE4E8；母题形状=**能量尖塔**（纳米流态尖塔+悬浮环——守望塔第 5 形态）；纹理
语言=纳米流态、光流线路、全息格点；专属母题=**力场弧+六边晶格**（三角拉伸成
的力场弧，六边格网——母题几何链「三角=远古矛尖→古典山墙→工业桁架→现代播放
键→未来力场弧→深空裂缝」第 5 环）。Cinematics A-05（E4→E5 跃迁，雷达塔涌起
纳米流态能量尖塔，电网蓝点燃聚变青）为 E5 帧参考。

特殊形态裁定（任务卡口径，同 era4 两裁先例；T-157 ⑥ 部分清账）：
  力场塔=力场弧静态底稿（towers/ 单帧=canonical 相位，决策层静态基准帧）
  +动画帧条带（anim/tower-force-field-anim.png 四相脉冲呼吸——T-157 按
  28 号 §6 待做 #3 同款升帧：第 0 相=canonical 逐像素同、1~3 相双弧渐张+
  环渐亮+源点辉光；REV-20261018-T157 登记冻结面递增）。
  无人机巢=蜂巢六边舱口静态底稿——舱口启闭属渲染层。

传送带 E5 皮肤（20/s 档，era5/era.json logistics tier4=drone 20 件/s——
trip-flight 形态的皮肤对位）：纹理色板换装，几何与帧律逐字节复用 T-119 冻结
契约（41 号 §4 三方镜像表，与 game/src/EraCore/Presentation/
BeltRenderDecisions.cs 常量一致，C# 侧 Era5ArtMappingTests 逐像素复验）：
    直线段 = shaders/belt-scroll.gdshader UV 滚动——本包供其两张 16px 纹理：
      belt/belt-body.png   动画层（带体表面，沿行进方向周期 16px，可无缝平铺）
      belt/belt-edge.png   静态层（带边护栏+发射柱钉，不随 UV 滚动）
    转弯段 = anim/belt-turn-anim.png 8 帧 tile 动画（4×2 网格 16px 帧，画布
      64×32；帧序=行优先 row*4+col）
      + belt/belt-turn-edge.png 静态弯口边框（与帧叠加，双层渲染）。
    帧边界对齐契约（与 E3/E4 皮肤完全同几何，仅色板换装）：
      转弯路径坐标 u（像素沿带心线，南入北行东出）：
        竖臂 x∈[5,10],y∈[11,15]: u=15-y；弯心 x∈[5,10],y∈[5,10]:
        u=5+(x-5)+(10-y)；横臂 x∈[11,15],y∈[5,10]: u=15+(x-10)。全程
        u∈[0,20]→转弯步长 21px。
      第 k 帧带体色 = PAT16[(u − 2k) mod 16]（图案向下游流动；每帧前进 2px，
      8 帧×2px=16px=图案周期 → 循环无缝闭合）；直段步长 16px/格。
      PAT16（周期 16，E5 未来换装）：纳米基材×2 档 + 聚变紫导流条×2
      （idx 2 亮/idx 10 暗）——E3 煤黑×黄铜/E4 沥青×警示黄同构异色。
    带速（20/s 档）由决策层 scroll 相位量驱动（ShaderOffsetPx），纹理仅为
    图案相位源——速度不烙进纹理（同一纹理可服务任意带速档）。

输出（--out DIR，默认 presentation/EraTD/textures/era5/）：
  towers/tower-railgun-l1.png / -l2.png              48×48 超导轨道炮（高能档）
  towers/tower-drone-nest-l1.png / -l2.png           32×32 蜂巢无人机巢
  towers/tower-force-field-l1.png                    32×32 能量尖塔（max1 单档）
  enemies/enemy-nano-swarm.png … 8 种                32×32 常规敌
  enemies/boss-dominator.png                         64×64 主宰 Boss（28 §5 下限）
  units/drone-right.png                              32×32 无人机（E5 载具）
  ui/panel-era5.png                                  32×32 面板色板
  tiles/floor-nano / -lattice / road-flux{,-alt} /
        wall-barrier{,-alt} / floor-rubble-nano{,-alt} /
        water-coolant                                16×16 地形 9 槽
  belt/belt-body.png / belt-edge.png /
        belt-turn-edge.png                           16×16 传送带双层纹理（E5 皮肤）
  anim/belt-turn-anim.png                            64×32 转弯 8 帧（4×2 网格）
  anim/tower-force-field-anim.png                    64×64 力场塔 4 相（2×2 网格，T-157 ⑥）

包文件数=30（era5 内容 id 对齐：三塔=5 升级位文件，era4=四塔 6 件的
30 文件同构异数——「同 manifest 结构」按槽类不按件数，REV 登记）。

全部输出须过 tools/ci/asset_validator.py（尺寸 16/32/48/64 白名单、≤64 色、
kebab 命名）。--self-test 另做五类强校验：①asset_validator 全目录；②转弯 8 帧
帧边界对齐（u 律+双臂逐像素平移+循环闭合+八帧两两不同+带边零重叠）；③静态层/
动画层透明区互斥（双层叠加契约）；④力场塔四相条带（phase0=canonical+
四相两两不同）；⑤确定性（二次生成 sha256 逐字节全等）。

依赖：仅 Python 3.8+ 标准库（struct/zlib/argparse/hashlib/tempfile）——与
asset_validator 同纪律，不引 Pillow。CLI：python era5_placeholders.py
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

# ---------------------------------------------------------------- E5 未来色板
# 18 色（≤64 master 上限，逐文件口径）：聚变青 3（31 号主色 #3FC1C9 居中）/
# 聚变紫 3（辅 #8A5FC9——升级位/力场/标识基准件，E2 金/E3 黄铜/E4 警示黄同位）/
# 纳米银白 3（辅 #DCE4E8——纳米装甲/全息面板）/ 纳米基材 3（机械骨·暗部·
# 轮廓——E4 沥青黑对位）/ 全息 3（全息格点·力场弧·座舱罩——E4 玻璃对位）/
# 光流 3（光流线路·能量导管——E4 电路绿对位）
PALETTE = {
    "cyan_light": (163, 232, 236),
    "cyan_main": (63, 193, 201),     # 31 号 E5 主色 聚变青 #3FC1C9
    "cyan_dark": (28, 124, 136),
    "violet_light": (198, 170, 234),
    "violet_main": (138, 95, 201),   # 31 号 E5 辅色 聚变紫 #8A5FC9
    "violet_dark": (84, 52, 148),
    "silver_light": (240, 244, 246),
    "silver_main": (220, 228, 232),  # 31 号 E5 辅色 纳米银白 #DCE4E8
    "silver_dark": (158, 174, 186),
    "slate_light": (112, 132, 150),
    "slate_mid": (76, 95, 114),
    "slate_dark": (50, 65, 84),
    "holo_light": (216, 246, 250),
    "holo_mid": (152, 216, 228),
    "holo_dark": (90, 160, 175),
    "flux_light": (200, 248, 246),
    "flux_mid": (128, 232, 228),
    "flux_dark": (70, 190, 190),
}

# ------------------------------------------------ 传送带几何契约（F-127 核心）
# 与 game/src/EraCore/Presentation/BeltRenderDecisions.cs 的冻结常量一一对应
# （T-119 冻结契约原样复用——E5 皮肤只换色板不动几何）：
BODY_TILE_PX = 16        # 带体源 tile 边长（32px 世界格 2× 整数放大，同地形先例）
TURN_FRAMES = 8          # 转弯帧数（14 §3「转弯 8 帧双层 tile 动画」）
PX_PER_FRAME = 2         # 每帧图案前进 px（8 帧×2=16=图案周期 → 无缝循环）
STRAIGHT_PATH_PX = 16    # 直段每格路径长（=源 tile 高，可无缝平铺）
TURN_PATH_PX = 21        # 转弯路径 u∈[0,20] 共 21px（u 律见模块 docstring）
TURN_FRAMES_PER_ROW = 4  # 动画条带 4×2 网格（横条 8 帧为 128px 宽超尺寸白名单）

# 带体表面图案（周期 16，E5 未来换装）：纳米基材两档 + 聚变紫导流条
# （idx2 亮/idx10 暗）——E3 煤黑×黄铜/E4 沥青×警示黄同构异色
PAT16 = (
    "slate_dark", "slate_dark", "violet_main", "slate_dark", "slate_dark",
    "slate_dark", "slate_dark", "slate_dark", "slate_mid", "slate_mid",
    "violet_dark", "slate_mid", "slate_mid", "slate_mid", "slate_mid",
    "slate_mid",
)


def turn_path_u(x, y):
    """转弯 tile 路径坐标 u（南入北行东出的规范右弯）；带外返回 None。
    与 era3/era4_placeholders.turn_path_u 逐式相同（冻结契约：几何不随皮肤变）。"""
    if 5 <= x <= 10 and 11 <= y <= 15:        # 竖臂（自南缘上行）
        return 15 - y
    if 5 <= x <= 10 and 5 <= y <= 10:         # 弯心方块（对角灯丝）
        return 5 + (x - 5) + (10 - y)
    if 11 <= x <= 15 and 5 <= y <= 10:        # 横臂（东出）
        return 15 + (x - 10)
    return None


class Canvas:
    """最小像素画布：RGBA 网格，初始全透明 (0,0,0,0)。与 era2/3/4 合成器
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


# ---------------------------------------------------------------- 未来部件库

_FORCE_ARC_X = (0.50, 0.82, 0.97, 1.00, 0.97, 0.82, 0.50)
_FORCE_ARC_Y = (-0.87, -0.75, -0.45, 0.00, 0.45, 0.75, 0.87)


def force_arc(c, cx, cy, r=5, key="cyan_main"):
    """力场弧（E5 专属母题，31 号 §1.2）：右张弧段（三角拉伸成的弧）。
    cx,cy=弧心；r=半径；固定 7 采样点（±60° 张角，纯整数舍入——确定性）。"""
    for k in range(7):
        x = cx + int(round(_FORCE_ARC_X[k] * r))
        y = cy + int(round(_FORCE_ARC_Y[k] * r))
        c.set(x, y, PALETTE[key])


def hex_cell(c, x, y, key="holo_mid", size=3):
    """六边晶格单元（E5 纹理语言基准件）：pointy-top 六边轮廓（固定 7 点）。"""
    w = size
    pts = ((x + w, y), (x + w // 2, y - (w // 2 - w // 4)),
           (x - w // 2, y - (w // 2 - w // 4)), (x - w, y),
           (x - w // 2, y + (w // 2 - w // 4)), (x + w // 2, y + (w // 2 - w // 4)))
    for (px, py) in pts:
        c.set(px, py, PALETTE[key])
    c.set(x, y, PALETTE[key])


def hex_lattice(c, x, y, step=4, key="holo_dark"):
    """六边格网（3×3 晶格点阵——全息格点纹理语言）。"""
    for dy in range(-1, 2):
        for dx in range(-1, 2):
            c.set(x + dx * step, y + dy * step, PALETTE[key])


def flux_line(c, x, y, w, key="flux_mid"):
    """光流线路（E5 纹理语言第三件）：水平光流+两端耦合节点（亮端点）。"""
    c.hline(x, y, w, PALETTE[key])
    c.set(x, y, PALETTE["flux_light"])
    c.set(x + w - 1, y, PALETTE["flux_light"])


def nano_panel(c, x, y, w, h, base="silver_main", light="silver_light",
               dark="silver_dark", seam=True):
    """纳米铸板：左亮右暗+顶亮底暗+水平流态缝+四角定位点
    （纳米流态=纹理语言首件——E4 浇筑缝同构异色）。"""
    c.fill(x, y, w, h, PALETTE[base])
    c.vline(x, y, h, PALETTE[light])
    c.vline(x + w - 1, y, h, PALETTE[dark])
    c.hline(x, y, w, PALETTE[light])
    c.hline(x, y + h - 1, w, PALETTE[dark])
    if seam and h >= 9:
        for py in range(y + 4, y + h - 3, 5):
            c.hline(x + 1, py, w - 2, PALETTE[dark])  # 流态缝
    if w >= 6 and h >= 6:
        for (sx, sy) in ((x + 1, y + 1), (x + w - 2, y + 1),
                         (x + 1, y + h - 2), (x + w - 2, y + h - 2)):
            c.set(sx, sy, PALETTE["slate_dark"])  # 定位系点


def emitter_stud(c, x, y, key="violet_main"):
    """发射柱钉：单像素聚变紫发射点（力场/导流标识基准件）。"""
    c.set(x, y, PALETTE[key])


def ring_band(c, cx, cy, half_w, key="cyan_main"):
    """悬浮环（能量尖塔母题件）：横带环（俯视压扁的轨道环）。
    cx,cy=环心；half_w=半宽。"""
    c.hline(cx - half_w, cy, half_w * 2 + 1, PALETTE[key])
    c.set(cx - half_w, cy - 1, PALETTE["cyan_light"])
    c.set(cx + half_w, cy - 1, PALETTE["cyan_light"])


# ---------------------------------------------------------------- 三塔（era5/towers.json）

def tower_railgun(level):
    """轨道炮（era5:tower-railgun）48×48：纳米基座+超导双轨+加速线圈环+
    聚变青电枢弧（energy 穿甲档辨识=双轨直射+无弹道光弧）。L2=双侧聚变紫
    电容组+炮座导流带+双股线圈（E5 升级位=聚变紫标记，E4 警示黄同律）。"""
    c = Canvas(48, 48)
    nano_panel(c, 10, 36, 28, 10)                          # 纳米基座
    force_arc(c, 13, 41, 5)                                # 力场弧母题徽（E5 专属）
    flux_line(c, 21, 42, 12)                               # 基座光流线路
    emitter_stud(c, 35, 41)
    c.fill(19, 26, 10, 10, PALETTE["slate_mid"])           # 炮座环
    c.hline(19, 26, 10, PALETTE["slate_light"])
    c.hline(19, 35, 10, PALETTE["slate_dark"])
    c.fill(15, 5, 4, 26, PALETTE["slate_mid"])             # 左超导轨
    c.fill(29, 5, 4, 26, PALETTE["slate_mid"])             # 右超导轨
    c.vline(15, 5, 26, PALETTE["slate_light"])
    c.vline(32, 5, 26, PALETTE["slate_dark"])
    for ry in (8, 14, 20, 26):                             # 加速线圈环（六边匝）
        c.hline(15, ry, 18, PALETTE["cyan_dark"])
        c.set(16, ry - 1, PALETTE["cyan_main"])
        c.set(30, ry - 1, PALETTE["cyan_main"])
        c.hline(19, ry, 10, PALETTE["cyan_dark"])
    c.fill(21, 4, 6, 3, PALETTE["cyan_light"])             # 电枢弧（出射口）
    c.hline(21, 4, 6, PALETTE["flux_light"])
    c.set(22, 3, PALETTE["flux_light"])
    c.set(25, 3, PALETTE["flux_light"])
    c.vline(23, 2, 2, PALETTE["violet_main"])              # 炮口相位环
    c.vline(24, 2, 2, PALETTE["violet_main"])
    hex_cell(c, 24, 30, "holo_mid", 2)                     # 谐振腔六边格点
    c.set(24, 31, PALETTE["holo_light"])
    if level >= 2:
        c.fill(9, 16, 4, 12, PALETTE["violet_main"])       # 双侧聚变紫电容组
        c.fill(35, 16, 4, 12, PALETTE["violet_main"])
        c.hline(9, 16, 4, PALETTE["violet_light"])
        c.hline(35, 16, 4, PALETTE["violet_light"])
        for cy_ in (20, 24):
            c.set(10, cy_, PALETTE["cyan_light"])          # 电容耦合节点
            c.set(36, cy_, PALETTE["cyan_light"])
        c.fill(17, 31, 14, 3, PALETTE["slate_dark"])       # 炮座导流带（L2 标识）
        for dx in (18, 21, 24, 27):
            c.set(dx, 32, PALETTE["violet_main"])
        c.hline(20, 27, 8, PALETTE["flux_mid"])            # 双股回流线
        c.hline(20, 29, 8, PALETTE["flux_mid"])
        c.set(27, 27, PALETTE["flux_light"])
        c.set(27, 29, PALETTE["flux_light"])
    return c


def tower_drone_nest(level):
    """无人机巢（era5:tower-drone-nest）32×32：纳米基座+蜂巢六边发射舱+
    待发无人机（pierce 速射档辨识=六边舱群+舱内机影）。L2=侧挂辅助发射舱+
    舱群导流带（E5 升级位=聚变紫标记）。"""
    c = Canvas(32, 32)
    nano_panel(c, 6, 22, 20, 9)                            # 纳米基座
    force_arc(c, 9, 26, 4)                                 # 力场弧母题徽
    flux_line(c, 16, 27, 7)
    emitter_stud(c, 24, 26)
    c.fill(8, 6, 16, 15, PALETTE["slate_mid"])             # 巢体
    c.hline(8, 6, 16, PALETTE["slate_light"])
    c.vline(8, 6, 15, PALETTE["slate_light"])
    c.vline(23, 6, 15, PALETTE["slate_dark"])
    bays = ((12, 10), (20, 10), (12, 17), (20, 17))        # 蜂巢六边舱口 2×2
    for (bx, by) in bays:
        c.fill(bx - 2, by - 2, 5, 5, PALETTE["slate_dark"])
        c.hline(bx - 2, by - 2, 5, PALETTE["cyan_dark"])
        c.hline(bx - 2, by + 2, 5, PALETTE["cyan_dark"])
        c.set(bx - 2, by - 1, PALETTE["holo_mid"])         # 舱口全息边
        c.set(bx + 2, by + 1, PALETTE["holo_mid"])
        c.set(bx, by, PALETTE["cyan_main"])                # 待发无人机影
        c.set(bx - 1, by, PALETTE["silver_main"])
        c.set(bx + 1, by, PALETTE["silver_main"])
    c.fill(15, 3, 2, 3, PALETTE["violet_main"])            # 顶置相位灯
    c.set(15, 2, PALETTE["violet_light"])
    if level >= 2:
        for px_ in (4, 26):                                # 侧挂辅助发射舱
            c.fill(px_, 9, 3, 9, PALETTE["slate_dark"])
            c.hline(px_, 9, 3, PALETTE["violet_main"])
            c.set(px_ + 1, 12, PALETTE["cyan_light"])
        c.fill(9, 19, 14, 3, PALETTE["slate_dark"])        # 舱群导流带（L2 标识）
        for dx in (10, 13, 16, 19, 21):
            c.set(dx, 20, PALETTE["violet_main"])
        c.set(14, 3, PALETTE["flux_light"])                # 双灯相位
        c.set(17, 3, PALETTE["flux_light"])
    return c


def tower_force_field(level):
    """力场塔（era5:tower-force-field）32×32（max1 单档）：**能量尖塔**=本代
    母题塔（纳米流态渐收塔身+悬浮环+力场弧发射）；slow 支援档辨识=无炮管+
    双弧合围。**静态底稿+动画帧**（T-157 ⑥ 升帧）：本函数=canonical 相位
    单帧（悬浮环+双弧发射注记——决策层静态基准帧，towers/ 落盘即此）；
    力场的脉冲呼吸=anim/tower-force-field-anim.png 四相条带
    （tower_force_field_frame——第 0 相与本帧逐像素同）。"""
    assert level == 1  # 支援设施不升级（内容表 max_tier=1；越级调用=合成器契约违例）
    c = Canvas(32, 32)
    nano_panel(c, 8, 25, 16, 6)                            # 纳米基座
    force_arc(c, 11, 28, 4)                                # 力场弧母题徽
    emitter_stud(c, 21, 28)
    hex_lattice(c, 16, 24, 4, "holo_dark")                 # 基座全息格点
    rows = (  # 纳米流态渐收塔身（行, x0, w）——底部宽顶部收
        (23, 13, 6), (20, 12, 8), (17, 12, 8), (14, 13, 6), (11, 14, 4), (9, 15, 2),
    )
    for (yy, x0, w) in rows:
        c.fill(x0, yy, w, 1, PALETTE["silver_main"])
        c.set(x0, yy, PALETTE["silver_light"])
        c.set(x0 + w - 1, yy, PALETTE["silver_dark"])
    c.set(16, 21, PALETTE["cyan_main"])                    # 流态涌点（纳米流态）
    c.set(15, 18, PALETTE["cyan_light"])
    c.set(17, 15, PALETTE["cyan_light"])
    c.set(16, 12, PALETTE["cyan_main"])
    ring_band(c, 16, 8, 6, "cyan_main")                    # 悬浮环（母题件）
    ring_band(c, 16, 6, 4, "cyan_dark")
    c.set(10, 8, PALETTE["cyan_light"])
    c.set(22, 8, PALETTE["cyan_light"])
    c.set(16, 4, PALETTE["flux_light"])                    # 塔顶谐振芯
    c.set(15, 4, PALETTE["holo_mid"])
    c.set(17, 4, PALETTE["holo_mid"])
    # 双弧发射注记（canonical 相位；脉冲呼吸=渲染层）
    for (ax, ay, adx) in ((8, 10, -1), (24, 10, 1)):
        c.set(ax, ay, PALETTE["holo_light"])
        c.set(ax + adx, ay + 2, PALETTE["holo_mid"])
        c.set(ax + adx * 2, ay + 4, PALETTE["holo_dark"])
        c.set(ax + adx * 2, ay + 6, PALETTE["holo_dark"])
    c.set(16, 9, PALETTE["violet_main"])                   # 力场源点（聚变紫）
    return c


# ------------------------------------------------ 力场塔动画帧（T-157 ⑥）

TOWER_ANIM_FRAMES = 4          # 脉冲呼吸四相（0=canonical 静止，1~3=渐张）
TOWER_ANIM_PER_ROW = 2         # 条带 2×2 网格（32px 帧 ×4 = 64×64 画布）


def tower_force_field_frame(phase):
    """力场塔单帧 32×32（脉冲呼吸第 phase 相，T-157 ⑥——28 号 §6 待做 #3
    同款动画帧挂账的清账）：第 0 相=canonical 静止底稿（与
    towers/tower-force-field-l1.png 逐像素同——决策层静态基准帧不变）；
    第 1~3 相=双弧逐级外延（每相沿对角再张一格）+悬浮环渐亮（环上亮点
    逐相位增加）+力场源点辉光（第 2 相起相邻点起辉）。四相循环读作
    「脉冲呼吸」——力场的周期张弛（T-142 特殊形态裁定「脉冲呼吸属渲染
    层」的帧律供料面；纯程序化，无随机数无 AI 生图）。"""
    assert 0 <= phase < TOWER_ANIM_FRAMES
    c = tower_force_field(1)
    if phase == 0:
        return c
    # a) 双弧外延：canonical 弧尾 (6,16)/(26,16)，每相沿对角再张一格
    #    （左弧向左下、右弧向右下——弧的展开方向）
    left_ext = ((5, 18), (4, 20), (3, 22))
    right_ext = ((27, 18), (28, 20), (29, 22))
    for k in range(phase):
        lx, ly = left_ext[k]
        c.set(lx, ly, PALETTE["holo_dark"])
        rx, ry = right_ext[k]
        c.set(rx, ry, PALETTE["holo_dark"])
    # b) 悬浮环渐亮：环上亮点随相位增加（cyan_light/flux 交替）
    ring_lights = ((13, 5), (19, 5), (12, 9), (20, 9), (14, 3), (18, 3))
    for k in range(phase * 2):
        rx, ry = ring_lights[k]
        c.set(rx, ry, PALETTE["cyan_light"] if k % 2 == 0 else PALETTE["flux_mid"])
    # c) 力场源点辉光：第 2 相起相邻点起辉、第 3 相两侧伴点
    if phase >= 2:
        c.set(16, 10, PALETTE["flux_light"])
    if phase >= 3:
        c.set(15, 9, PALETTE["violet_light"])
        c.set(17, 9, PALETTE["violet_light"])
    return c


def tower_force_field_strip():
    """力场塔动画条带 64×64（2×2 网格，帧序=行优先 row*2+col）：四相脉冲
    呼吸循环（0 静止 → 1~3 渐张 → 回 0 收束）。单帧 32px×四帧横排=128px
    超 SIZE-ALLOWLIST，取 2×2 网格（belt-turn-anim 同律）。"""
    strip = Canvas(TOWER_ANIM_PER_ROW * 32, (TOWER_ANIM_FRAMES // TOWER_ANIM_PER_ROW) * 32)
    for k in range(TOWER_ANIM_FRAMES):
        strip.blit(tower_force_field_frame(k),
                   (k % TOWER_ANIM_PER_ROW) * 32, (k // TOWER_ANIM_PER_ROW) * 32)
    return strip


# ---------------------------------------------------------------- 八敌+Boss（era5/enemies.json）

def _shadow(c, x, y, w):
    """脚下接地阴影（1px 基材横带）。"""
    c.hline(x, y, w, PALETTE["slate_mid"])


def _hover_glow(c, xs, y):
    """悬浮光晕（E5 悬浮单位接地件：青色托线）。"""
    for x in xs:
        c.set(x, y, PALETTE["cyan_dark"])


def _legs(c, xs, y, h=5, key="slate_dark"):
    for lx in xs:
        c.fill(lx, y, 2, h, PALETTE[key])


def enemy_nano_swarm():
    """纳米蜂群（era5:enemy-nano-swarm）32×32：微粒云团——银白/聚变青纳米
    单体聚成游涡（swarm 辨识：无甲微粒成群——集群体无固定形）。"""
    c = Canvas(32, 32)
    _shadow(c, 8, 29, 16)
    motes = (  # 云团轮廓带（外环稀疏）
        (10, 10), (14, 7), (19, 6), (24, 9), (26, 14), (25, 19), (21, 23),
        (15, 24), (10, 21), (8, 15), (12, 13), (17, 10), (22, 12), (24, 16),
        (20, 19), (15, 18), (11, 17), (13, 20), (18, 15), (21, 16),
    )
    for (mx, my) in motes:
        c.set(mx, my, PALETTE["silver_main"])
    core = ((16, 12), (14, 15), (18, 14), (16, 17), (13, 12), (19, 17))  # 核心密团
    for (mx, my) in core:
        c.set(mx, my, PALETTE["cyan_main"])
    for (mx, my) in ((15, 13), (17, 15), (16, 16)):        # 聚变青涡心
        c.set(mx, my, PALETTE["cyan_light"])
    for (hx, hy) in ((12, 11), (20, 13), (17, 21), (23, 15)):  # 全息格点（格网闪点）
        c.set(hx, hy, PALETTE["holo_mid"])
    c.set(16, 9, PALETTE["holo_light"])                     # 云顶反光
    c.set(9, 16, PALETTE["silver_dark"])                    # 云底暗面
    c.set(24, 20, PALETTE["silver_dark"])
    return c


def enemy_combat_frame():
    """作战机框（era5:enemy-combat-frame）32×32：合金机骨+银白胸甲+聚变青
    单眼（armor=24 中坚辨识：骨架形态+单眼+制式步枪）。"""
    c = Canvas(32, 32)
    _shadow(c, 9, 29, 15)
    c.fill(13, 5, 6, 4, PALETTE["slate_mid"])               # 头壳
    c.hline(13, 5, 6, PALETTE["slate_light"])
    c.fill(14, 6, 4, 2, PALETTE["holo_dark"])               # 面罩
    c.set(16, 7, PALETTE["cyan_light"])                     # 单眼（聚变青）
    c.set(15, 7, PALETTE["cyan_dark"])
    c.vline(16, 4, 1, PALETTE["violet_main"])               # 头顶相位钉
    nano_panel(c, 11, 10, 10, 8)                            # 银白胸甲（纳米铸板）
    c.set(13, 12, PALETTE["cyan_main"])                     # 胸口能量核
    c.set(14, 12, PALETTE["cyan_light"])
    c.hline(11, 17, 10, PALETTE["slate_dark"])              # 骨盆梁
    _legs(c, (12, 18), 18, h=6)                             # 双腿（机骨）
    c.set(12, 24, PALETTE["slate_light"])
    c.set(19, 24, PALETTE["slate_light"])
    c.fill(21, 11, 7, 2, PALETTE["slate_dark"])             # 制式步枪
    c.set(28, 11, PALETTE["cyan_main"])                     # 枪口（光枪）
    c.set(23, 13, PALETTE["violet_dark"])                   # 弹匣位（能匣）
    c.set(22, 13, PALETTE["slate_mid"])
    c.set(10, 12, PALETTE["holo_mid"])                      # 背部天线
    c.set(9, 11, PALETTE["holo_light"])
    return c


def enemy_siege_titan():
    """攻城泰坦（era5:enemy-siege-titan）32×32：重型四足泰坦+肩部破城桩阵+
    液压巨爪（对建筑 ×3/armor=26 攻城辨识：重甲+工程破城械）。"""
    c = Canvas(32, 32)
    _shadow(c, 2, 29, 28)
    nano_panel(c, 5, 8, 22, 10)                             # 重型 hull（纳米铸板）
    c.fill(8, 10, 6, 3, PALETTE["holo_dark"])               # 观察窗带
    c.set(9, 11, PALETTE["holo_light"])
    c.set(13, 11, PALETTE["cyan_main"])                     # 传感眼
    c.fill(24, 5, 5, 7, PALETTE["slate_dark"])              # 肩部破城桩阵
    c.hline(24, 5, 5, PALETTE["violet_main"])
    c.hline(24, 8, 5, PALETTE["violet_dark"])
    for px_ in (24, 26, 28):
        c.set(px_, 12, PALETTE["silver_main"])              # 桩尖（银白）
    c.fill(2, 11, 5, 7, PALETTE["slate_dark"])              # 液压巨爪
    c.set(1, 10, PALETTE["silver_dark"])
    c.set(1, 13, PALETTE["silver_dark"])
    c.set(1, 16, PALETTE["silver_dark"])
    c.vline(4, 12, 4, PALETTE["slate_mid"])                 # 液压杆
    c.fill(6, 18, 4, 4, PALETTE["slate_mid"])               # 四足（反关节）
    c.fill(4, 22, 4, 3, PALETTE["slate_dark"])
    c.fill(3, 25, 6, 2, PALETTE["slate_mid"])
    c.fill(19, 18, 4, 4, PALETTE["slate_mid"])
    c.fill(21, 22, 4, 3, PALETTE["slate_dark"])
    c.fill(20, 25, 6, 2, PALETTE["slate_mid"])
    c.set(6, 6, PALETTE["violet_light"])                    # 排热口（聚变紫）
    c.set(7, 5, PALETTE["violet_dark"])
    c.set(20, 18, PALETTE["cyan_dark"])                     # 足部接地光
    c.set(5, 25, PALETTE["cyan_dark"])
    return c


def enemy_hover_striker():
    """掠空突击体（era5:enemy-hover-striker）32×32：无腿悬浮镖体+后部聚变
    推进流+前倾加速（opening_charge 辨识：疾驰光流+前倾姿态）。"""
    c = Canvas(32, 32)
    _hover_glow(c, range(6, 26), 28)
    c.fill(8, 14, 16, 5, PALETTE["silver_main"])            # 镖体（纳米银白）
    c.hline(8, 14, 16, PALETTE["silver_light"])
    c.hline(8, 18, 16, PALETTE["silver_dark"])
    c.fill(24, 15, 5, 3, PALETTE["slate_mid"])              # 前锥（朝右前倾）
    c.hline(24, 15, 5, PALETTE["slate_light"])
    c.set(28, 16, PALETTE["violet_main"])                   # 锥顶撞角（聚变紫）
    c.set(27, 15, PALETTE["violet_light"])
    c.fill(11, 15, 6, 2, PALETTE["holo_mid"])               # 座舱条（全息）
    c.set(12, 16, PALETTE["holo_light"])
    c.fill(5, 15, 3, 4, PALETTE["slate_dark"])              # 后部推进器
    for (tx, ty) in ((2, 15), (3, 16), (2, 17)):            # 推进光流（尾迹）
        c.set(tx, ty, PALETTE["cyan_main"])
    c.set(1, 16, PALETTE["cyan_light"])
    c.set(4, 14, PALETTE["cyan_dark"])
    c.set(4, 19, PALETTE["cyan_dark"])
    c.set(9, 12, PALETTE["cyan_dark"])                      # 背鳍
    c.set(10, 11, PALETTE["cyan_main"])
    c.set(11, 12, PALETTE["cyan_dark"])
    c.set(14, 20, PALETTE["slate_mid"])                     # 腹侧导流片
    c.set(18, 20, PALETTE["slate_mid"])
    return c


def enemy_lancer_drone():
    """长枪无人机（era5:enemy-lancer-drone）32×32：悬浮机体+超导长枪（架枪
    定点射辨识=长枪+悬停姿态，stationary_ranged_attack 行形对位）。"""
    c = Canvas(32, 32)
    _hover_glow(c, range(8, 24), 28)
    c.fill(12, 12, 8, 7, PALETTE["slate_mid"])              # 机体
    c.hline(12, 12, 8, PALETTE["slate_light"])
    c.fill(14, 14, 4, 3, PALETTE["holo_dark"])              # 环形传感
    c.set(15, 15, PALETTE["cyan_light"])                    # 锁定眼
    c.set(16, 15, PALETTE["cyan_main"])
    for (ax, ay) in ((8, 11), (23, 11), (8, 20), (23, 20)):
        c.hline(ax, ay, 3, PALETTE["slate_dark"])           # 四悬臂
    for (rx, ry) in ((5, 9), (24, 9), (5, 22), (24, 22)):   # 悬浮转子（光圈）
        c.set(rx, ry, PALETTE["cyan_main"])
        c.set(rx + 1, ry - 1, PALETTE["cyan_light"])
        c.set(rx - 1, ry - 1, PALETTE["cyan_dark"])
    c.hline(2, 16, 28, PALETTE["slate_mid"])                # 超导长枪（横贯）
    c.hline(2, 15, 28, PALETTE["silver_light"])
    c.set(1, 15, PALETTE["violet_main"])                    # 枪尾相位环
    c.set(29, 16, PALETTE["cyan_light"])                    # 枪尖（出射点）
    c.set(30, 16, PALETTE["flux_light"])
    for gx in (8, 14, 20):                                  # 枪身耦合节
        c.set(gx, 17, PALETTE["holo_mid"])
    c.set(16, 21, PALETTE["violet_dark"])                   # 下挂能匣
    return c


def enemy_barrier_mech():
    """屏障机甲（era5:enemy-barrier-mech）32×32：通高力场屏障（全息场+聚变紫
    发射柱）+重装机骨（shield_wall 辨识：力场塔盾覆身——正面减伤 20% 行形）。"""
    c = Canvas(32, 32)
    _shadow(c, 9, 29, 16)
    c.fill(21, 5, 5, 4, PALETTE["slate_mid"])               # 头壳（盾后出露）
    c.set(24, 7, PALETTE["holo_mid"])
    c.vline(23, 3, 2, PALETTE["violet_main"])
    c.fill(21, 9, 5, 7, PALETTE["silver_main"])             # 重装躯干
    c.set(23, 11, PALETTE["cyan_main"])
    c.hline(21, 16, 5, PALETTE["slate_dark"])
    _legs(c, (21, 24), 17, key="slate_dark")
    c.fill(4, 3, 2, 22, PALETTE["violet_dark"])             # 屏障发射柱（上下）
    c.hline(3, 3, 4, PALETTE["violet_main"])
    c.hline(3, 24, 4, PALETTE["violet_main"])
    c.set(5, 12, PALETTE["violet_light"])                   # 柱耦合节点
    c.set(5, 16, PALETTE["violet_light"])
    c.fill(6, 4, 12, 19, PALETTE["holo_mid"])               # 力场屏障（全息场体）
    for (hx, hy) in ((8, 8), (13, 12), (9, 17), (15, 20)):  # 场内六边晶格
        hex_cell(c, hx, hy, "holo_light", 2)
    for (vx, vy) in ((7, 5), (16, 22), (11, 14)):           # 聚变紫弧光
        c.set(vx, vy, PALETTE["violet_light"])
    c.set(17, 6, PALETTE["holo_light"])                     # 场面反光
    c.set(8, 21, PALETTE["holo_light"])
    c.fill(26, 10, 2, 7, PALETTE["slate_dark"])             # 背部能匣架
    c.set(26, 9, PALETTE["cyan_main"])
    return c


def enemy_melt_drone():
    """熔蚀无人机（era5:enemy-melt-drone）32×32：四旋翼+下挂熔蚀剂槽（聚变
    青腐蚀液）+滴落注记（death_explode 辨识：载荷槽+警示弧）。"""
    c = Canvas(32, 32)
    _hover_glow(c, range(9, 23), 28)
    c.fill(12, 10, 8, 6, PALETTE["slate_mid"])              # 机体
    c.hline(12, 10, 8, PALETTE["slate_light"])
    c.set(14, 12, PALETTE["holo_mid"])                      # 传感对
    c.set(17, 12, PALETTE["holo_mid"])
    for (ax, ay) in ((7, 9), (20, 9), (7, 17), (20, 17)):
        c.hline(ax, ay, 4, PALETTE["slate_dark"])           # 四臂
    for (rx, ry) in ((4, 7), (23, 7), (4, 19), (23, 19)):   # 旋翼光圈
        c.hline(rx, ry, 4, PALETTE["cyan_main"])
        c.set(rx, ry - 1, PALETTE["cyan_light"])
    c.fill(13, 16, 6, 6, PALETTE["holo_dark"])              # 熔蚀剂槽（半透）
    c.fill(14, 17, 4, 4, PALETTE["cyan_main"])              # 腐蚀液（聚变青）
    c.set(15, 18, PALETTE["cyan_light"])
    c.hline(13, 16, 6, PALETTE["violet_main"])              # 槽箍（聚变紫警示弧）
    c.hline(13, 21, 6, PALETTE["violet_dark"])
    c.set(15, 22, PALETTE["cyan_dark"])                     # 滴落注记
    c.set(16, 23, PALETTE["cyan_main"])
    c.set(16, 24, PALETTE["cyan_light"])
    c.set(13, 9, PALETTE["violet_light"])                   # 顶警灯
    return c


def enemy_nexus_core():
    """枢纽核（era5:enemy-nexus-core）32×32：悬浮八面核心+环绕轨道环+广播
    弧（war_drums 辨识：指挥网中枢——环+广播波纹）。"""
    c = Canvas(32, 32)
    _hover_glow(c, range(10, 22), 29)
    c.fill(13, 8, 6, 2, PALETTE["silver_light"])            # 八面核（上棱）
    c.fill(11, 10, 10, 8, PALETTE["silver_main"])           # 核体
    c.fill(13, 18, 6, 2, PALETTE["silver_dark"])            # 下棱
    c.vline(11, 10, 8, PALETTE["silver_light"])
    c.vline(20, 10, 8, PALETTE["silver_dark"])
    c.fill(14, 12, 4, 4, PALETTE["holo_mid"])               # 核心窗
    c.set(15, 13, PALETTE["cyan_light"])                    # 核心（聚变青）
    c.set(16, 13, PALETTE["cyan_main"])
    c.set(15, 14, PALETTE["violet_main"])                   # 指令瞳（聚变紫）
    ring_band(c, 16, 7, 7, "cyan_dark")                     # 环绕轨道环（上）
    ring_band(c, 16, 21, 7, "cyan_dark")                    # 环绕轨道环（下）
    c.set(9, 7, PALETTE["cyan_light"])
    c.set(22, 21, PALETTE["cyan_light"])
    c.vline(16, 5, 3, PALETTE["slate_mid"])                 # 顶天线
    c.set(16, 4, PALETTE["violet_light"])
    for (bx, by, k) in ((6, 12, "holo_light"), (26, 12, "holo_light"),
                        (8, 16, "holo_mid"), (24, 16, "holo_mid"),
                        (10, 20, "holo_dark"), (22, 20, "holo_dark")):
        c.set(bx, by, PALETTE[k])                           # 广播弧（波纹注记）
    c.set(12, 11, PALETTE["slate_dark"])                    # 核肩散热
    c.set(19, 11, PALETTE["slate_dark"])
    return c


def boss_dominator():
    """主宰（era5:enemy-boss）64×64：悬浮霸主——宽体暗甲+中央主宰核（聚变青
    环+聚变紫瞳）+重力脉冲同心弧（gravity_pulse 辨识：周身压制波环）+双肩
    引力塔+顶环冕。画布 64（28 §5 裁定先例重演：内容表 canvas_size_px=96，
    96 不在校验器清单，取「常规 Boss 64~96」下限 64 合规；终章 Boss 96×112
    另档——升 96 须随 SIZE-ALLOWLIST 扩展走 REV）。"""
    c = Canvas(64, 64)
    _shadow(c, 6, 60, 52)
    span = (  # 悬浮宽体平面形（行, x0, x1）——菱形前缘+展肩
        (18, 28, 35), (19, 25, 38), (20, 22, 41), (21, 19, 44), (22, 16, 47),
        (23, 13, 50), (24, 11, 52), (25, 9, 54), (26, 8, 55), (27, 7, 56),
        (28, 7, 56), (29, 7, 56), (30, 7, 56), (31, 8, 55), (32, 9, 54),
        (33, 11, 52), (34, 13, 50), (35, 16, 47), (36, 20, 43), (37, 25, 38),
    )
    for (yy, x0, x1) in span:
        c.hline(x0, yy, x1 - x0 + 1, PALETTE["slate_dark"])  # 暗甲蒙皮
    for (yy, x0, x1) in span:  # 前缘纳米流态缝（上缘两行铺中色）
        c.hline(x0, yy, 1, PALETTE["slate_mid"])
        c.hline(x1, yy, 1, PALETTE["slate_mid"])
    c.fill(22, 20, 20, 18, PALETTE["slate_dark"])           # 中央舰体隆起
    for i in range(8):                                      # 舰体流态缝
        c.set(22 + i, 20 + i, PALETTE["slate_mid"])
        c.set(41 - i, 20 + i, PALETTE["slate_mid"])
    # 主宰核：聚变青环 + 聚变紫瞳（重力脉冲源）
    c.fill(28, 24, 8, 8, PALETTE["holo_dark"])              # 核窗
    c.hline(27, 28, 10, PALETTE["cyan_main"])               # 核环（上）
    c.hline(27, 27, 10, PALETTE["cyan_main"])               # 核环（下）
    c.set(28, 26, PALETTE["cyan_light"])
    c.set(35, 26, PALETTE["cyan_light"])
    c.fill(30, 29, 4, 2, PALETTE["violet_main"])            # 瞳（聚变紫）
    c.set(31, 29, PALETTE["violet_light"])
    ring_band(c, 32, 16, 10, "cyan_dark")                   # 顶环冕（悬浮环）
    c.set(22, 16, PALETTE["cyan_light"])
    c.set(42, 16, PALETTE["cyan_light"])
    c.vline(32, 17, 3, PALETTE["slate_mid"])                # 冕杆
    for px_ in (14, 48):                                    # 双肩引力塔
        c.fill(px_ - 2, 22, 5, 8, PALETTE["slate_mid"])
        c.hline(px_ - 2, 22, 5, PALETTE["violet_main"])
        c.set(px_, 30, PALETTE["cyan_light"])
    for (ex, ey) in ((18, 33), (44, 33)):                   # 两侧聚变推进口
        c.fill(ex, ey, 3, 4, PALETTE["cyan_dark"])
        c.set(ex + 1, ey + 4, PALETTE["cyan_main"])
    # 重力脉冲同心弧（gravity_pulse：900t 周期 3 格压制——canonical 双弧注记）
    for (ay, k) in ((44, "violet_dark"), (47, "violet_main")):
        c.hline(24, ay, 16, PALETTE[k])
        c.set(22, ay - 1, PALETTE[k])
        c.set(41, ay - 1, PALETTE[k])
        c.set(20, ay - 2, PALETTE["violet_dark"])
        c.set(43, ay - 2, PALETTE["violet_dark"])
    c.hline(27, 50, 10, PALETTE["violet_dark"])             # 第三弧（收束）
    c.set(25, 49, PALETTE["violet_dark"])
    c.set(38, 49, PALETTE["violet_dark"])
    for (sx, sy) in ((16, 27), (24, 23), (39, 23), (47, 27)):
        c.set(sx, sy, PALETTE["holo_mid"])                  # 蒙皮力场微光
    c.set(31, 13, PALETTE["flux_light"])                    # 机顶频闪（贴冕环）
    c.set(32, 13, PALETTE["violet_light"])
    return c


# ---------------------------------------------------------------- 载具（F-042 E5 档）

def drone_right():
    """无人机（E5 载具，era.json logistics.carrier=drone；20/s 档并列机动
    运输形态）32×32（朝右）：纳米银白货舱+聚变青推进环+全息护罩条（未来色板
    正式档——era4 truck-right 的悬浮对位件）。"""
    c = Canvas(32, 32)
    _hover_glow(c, range(4, 28), 28)
    nano_panel(c, 3, 8, 14, 10, seam=False)                 # 货舱（纳米铸板）
    c.hline(3, 13, 14, PALETTE["cyan_main"])                # 舱体聚变青条带
    force_arc(c, 7, 16, 3, "cyan_dark")                     # 舱侧力场弧徽（小号母题）
    c.fill(18, 10, 9, 7, PALETTE["slate_mid"])              # 机体舱（推进段）
    c.hline(18, 10, 9, PALETTE["slate_light"])
    c.vline(18, 10, 7, PALETTE["slate_light"])
    c.fill(20, 12, 5, 3, PALETTE["holo_mid"])               # 护罩条（全息）
    c.set(21, 13, PALETTE["holo_light"])
    c.set(19, 9, PALETTE["violet_main"])                    # 顶置相位灯
    c.set(25, 9, PALETTE["violet_main"])
    c.fill(27, 12, 2, 3, PALETTE["cyan_dark"])              # 前推进环
    c.set(29, 13, PALETTE["cyan_main"])                     # 前灯
    c.set(30, 13, PALETTE["flux_light"])
    c.set(2, 11, PALETTE["violet_dark"])                    # 尾灯
    for (ax, ay) in ((6, 6), (14, 6), (22, 6)):             # 顶悬臂×3
        c.vline(ax, ay, 2, PALETTE["slate_dark"])
        c.hline(ax - 2, ay - 1, 5, PALETTE["cyan_dark"])    # 旋翼光圈
        c.set(ax, ay - 2, PALETTE["cyan_light"])
    c.set(10, 19, PALETTE["slate_mid"])                     # 腹侧导流
    c.set(14, 19, PALETTE["slate_mid"])
    return c


# ---------------------------------------------------------------- UI 色板（F-132 系）

def panel_palette():
    """E5 面板色板 32×32：聚变青描边+基材底+4×4 色块矩阵（聚变青/聚变紫/
    银白/基材四行），供 C-22 面板 E5 换肤取色（色板底稿，非成品 UI 图）。"""
    c = Canvas(32, 32)
    c.fill(0, 0, 32, 32, PALETTE["slate_dark"])
    c.rect(0, 0, 32, 32, None, PALETTE["cyan_main"])
    c.rect(2, 2, 28, 28, None, PALETTE["cyan_dark"])
    c.fill(4, 4, 24, 24, PALETTE["slate_mid"])
    rows = (
        ("cyan_light", "cyan_main", "cyan_dark", "violet_main"),
        ("violet_light", "violet_main", "violet_dark", "silver_dark"),
        ("silver_light", "silver_main", "silver_dark", "holo_mid"),
        ("slate_light", "slate_mid", "slate_dark", "holo_light"),
    )
    for ry, row in enumerate(rows):
        for rx, key in enumerate(row):
            c.fill(5 + rx * 6, 5 + ry * 6, 5, 5, PALETTE[key])
    return c


# ---------------------------------------------------------------- 地形 9 槽（16×16）

def tile_floor_nano():
    """纳米地砖：铸态面+六边晶格点+定位系点（Plain 主槽）。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["silver_main"])
    c.hline(0, 0, 16, PALETTE["silver_light"])
    c.vline(0, 0, 16, PALETTE["silver_light"])
    c.hline(0, 15, 16, PALETTE["silver_dark"])
    c.vline(15, 0, 16, PALETTE["silver_dark"])
    c.hline(0, 7, 16, PALETTE["silver_dark"])   # 流态缝（横）
    c.vline(8, 0, 7, PALETTE["silver_dark"])    # 纵缝（上半）
    c.vline(3, 8, 8, PALETTE["silver_dark"])    # 纵缝（下半错缝）
    c.vline(12, 8, 8, PALETTE["silver_dark"])
    for (hx, hy) in ((5, 3), (12, 5), (6, 11), (13, 12)):
        c.set(hx, hy, PALETTE["holo_dark"])     # 六边晶格点阵
    c.set(5, 4, PALETTE["holo_mid"])
    c.set(13, 11, PALETTE["holo_mid"])
    for (px_, py_) in ((2, 2), (14, 10)):
        c.set(px_, py_, PALETTE["slate_dark"])  # 定位系点
    c.set(9, 12, PALETTE["silver_light"])       # 流态抹痕
    return c


def tile_floor_nano_lattice():
    """纳米晶格地砖（Forest 代位——未来城区无林地，全息格点阵代绿带；
    era3 steel-grated/era4 concrete-grated 同构异色）。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["slate_dark"])
    for y in range(0, 16, 2):
        c.hline(0, y, 16, PALETTE["slate_mid"])
    for (hx, hy) in ((4, 4), (10, 4), (2, 10), (8, 10), (14, 10), (5, 13), (12, 13)):
        c.set(hx, hy, PALETTE["holo_mid"])      # 全息格点（密阵）
    for (hx, hy) in ((7, 6), (13, 6), (3, 13)):
        c.set(hx, hy, PALETTE["holo_light"])    # 亮点
    c.hline(0, 0, 16, PALETTE["slate_light"])
    c.vline(15, 0, 16, PALETTE["slate_mid"])
    return c


def tile_road_flux(dash_offset):
    """光流路地砖（Road）：基材面+聚变青光流线+聚变紫导流虚线（offset 区分
    正/副槽）——「光流线路」纹理语言的地形件。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["slate_mid"])
    for (sx, sy, key) in ((5, 4, "slate_light"), (12, 5, "slate_light"),
                          (3, 10, "slate_dark"), (14, 11, "slate_light"),
                          (8, 9, "slate_dark"), (6, 13, "slate_light")):
        c.set(sx, sy, PALETTE[key])             # 基材杂点
    c.hline(0, 1, 16, PALETTE["silver_dark"])   # 路缘线（双缘）
    c.hline(0, 14, 16, PALETTE["silver_dark"])
    for dx in (2 + dash_offset, 9 + dash_offset):  # 中线光流（沿行进向）
        c.fill(dx, 7, 4, 2, PALETTE["cyan_main"])
        c.set(dx, 7, PALETTE["cyan_light"])
    if dash_offset:                             # 副槽=耦合节点变体
        c.fill(11, 2, 3, 3, PALETTE["cyan_dark"])
        c.set(12, 3, PALETTE["holo_light"])
    return c


def tile_wall_barrier(lit_variant):
    """力场墙砖（Wall）：双发射柱+全息场体+聚场紫弧光+底部基材裙带；
    lit_variant=True 副槽=场体点亮（holo_light 满场）。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["slate_mid"])   # 场框底
    c.vline(0, 0, 16, PALETTE["slate_dark"])     # 双发射柱（左右）
    c.vline(1, 0, 16, PALETTE["silver_dark"])
    c.vline(14, 0, 16, PALETTE["slate_dark"])
    c.vline(15, 0, 16, PALETTE["silver_dark"])
    field = PALETTE["holo_light"] if lit_variant else PALETTE["holo_mid"]
    c.fill(2, 1, 12, 14, field)                  # 全息场体
    for (hx, hy) in ((4, 4), (9, 7), (6, 11), (12, 3), (11, 12)):
        c.set(hx, hy, PALETTE["holo_dark"])      # 场内格点
    for (vx, vy) in ((5, 3), (10, 9), (7, 6), (12, 12)):
        c.set(vx, vy, PALETTE["violet_light"])   # 聚变紫弧光
    if lit_variant:
        c.set(5, 7, PALETTE["flux_light"])       # 点亮光流
        c.set(10, 4, PALETTE["flux_light"])
    c.hline(2, 15, 12, PALETTE["slate_dark"])    # 基材裙带
    c.set(4, 15, PALETTE["silver_dark"])
    c.set(11, 15, PALETTE["silver_dark"])
    return c


def tile_floor_rubble_nano(dark_variant):
    """战场废墟地砖（Rock 代位）：基材底+纳米碎板+断口光流线。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["slate_mid"])
    chunks = ((2, 2, 3, 2, "silver_light"), (7, 3, 4, 2, "silver_main"),
              (12, 2, 2, 3, "silver_main"), (3, 8, 4, 3, "silver_main"),
              (9, 9, 3, 2, "silver_light"), (13, 12, 2, 2, "silver_dark"),
              (6, 13, 3, 2, "silver_dark"), (1, 13, 2, 2, "silver_main"))
    if dark_variant:
        chunks = ((3, 2, 2, 3, "silver_dark"), (8, 2, 3, 2, "silver_main"),
                  (13, 4, 2, 2, "silver_light"), (2, 8, 3, 2, "silver_light"),
                  (7, 10, 4, 3, "silver_dark"), (12, 9, 3, 2, "silver_main"),
                  (3, 13, 3, 2, "silver_main"), (14, 14, 2, 2, "silver_dark"))
    for (x, y, w, h, key) in chunks:
        c.fill(x, y, w, h, PALETTE[key])
        c.set(x, y + h - 1, PALETTE["silver_dark"])
    c.vline(5, 5, 4, PALETTE["cyan_dark"])      # 断口光流线（ severed flux）
    c.set(5, 5, PALETTE["violet_dark"])
    c.hline(9, 7, 4, PALETTE["cyan_dark"])
    c.set(12, 7, PALETTE["violet_dark"])
    for (mx, my) in ((8, 6), (13, 10), (4, 12)):
        c.set(mx, my, PALETTE["cyan_main"])     # 游离纳米微尘
    return c


def tile_water_coolant():
    """冷却池（Water）：聚变冷却液池（周期 8 斜带，era2/3/4 water 同构异色）
    +光流波光。"""
    c = Canvas(16, 16)
    for y in range(16):
        for x in range(16):
            band = (x + y) % 8
            key = "holo_dark" if band in (0, 1) else ("cyan_dark" if band == 4 else "slate_dark")
            c.set(x, y, PALETTE[key])
    for (x, y) in ((3, 2), (11, 6), (7, 12)):
        c.set(x, y, PALETTE["flux_light"])       # 波光高光
    c.set(5, 9, PALETTE["cyan_main"])            # 岸线反染
    return c


# ---------------------------------------------------------------- 传送带双层（E5 皮肤）

def belt_body():
    """直段带体 16×16（动画层，E5 皮肤）：竖向条带 cols 5..10，
    行色=PAT16[y%16]——沿 V 方向周期 16=源 tile 高 → shader UV 滚动可无缝
    平铺（几何与 E3/E4 皮肤逐式相同，仅色板换装）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    for y in range(BODY_TILE_PX):
        for x in range(5, 11):
            c.set(x, y, PALETTE[PAT16[y % 16]])
    return c


def belt_edge():
    """直段带边 16×16（静态层，E5 皮肤）：双侧护栏+发射柱钉，带外全透明
    （与带体零重叠——护栏=纳米银白立缘，柱钉=聚变紫发射钉，E3 钢轨×黄铜/
    E4 混凝土×警示黄同构）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    c.vline(3, 0, 16, PALETTE["slate_dark"])
    c.vline(4, 0, 16, PALETTE["silver_dark"])
    c.vline(11, 0, 16, PALETTE["silver_dark"])
    c.vline(12, 0, 16, PALETTE["slate_dark"])
    for y in (1, 5, 9, 13):  # 护栏发射柱钉
        emitter_stud(c, 4, y)
        emitter_stud(c, 11, y)
    return c


def belt_turn_edge():
    """弯口带边 16×16（静态层，E5 皮肤）：规范右弯（南入东出）外/内弯护栏
    +对角补角，与带体（u∈[0,20] 条带）零重叠——不透明集与 E3/E4 皮肤逐坐标
    相同（冻结契约：几何不随皮肤变）。左弯=渲染层水平镜像（决策层 FlipX）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    # 外弯（西北侧）：竖段 col3/4 rows6..15 + 对角 (4,5)(4,4) + 横段 row3/4 cols6..15
    c.vline(3, 6, 10, PALETTE["slate_dark"])
    c.vline(4, 6, 10, PALETTE["silver_dark"])
    c.set(4, 5, PALETTE["slate_dark"])
    c.set(4, 4, PALETTE["slate_dark"])
    c.set(5, 4, PALETTE["silver_dark"])
    c.hline(6, 3, 10, PALETTE["slate_dark"])
    c.hline(6, 4, 10, PALETTE["silver_dark"])
    # 内弯（东南侧）：竖段 col11/12 rows11..15 + 横段 row11/12 cols11..15
    c.vline(11, 11, 5, PALETTE["silver_dark"])
    c.vline(12, 11, 5, PALETTE["slate_dark"])
    c.hline(11, 11, 5, PALETTE["silver_dark"])
    c.hline(12, 12, 5, PALETTE["slate_dark"])
    for y in (9, 13):  # 发射柱钉
        emitter_stud(c, 4, y)
    for x in (9, 13):
        emitter_stud(c, x, 4)
    emitter_stud(c, 11, 13)
    emitter_stud(c, 13, 11)
    return c


def belt_turn_frame(phase):
    """转弯单帧 16×16（动画层，E5 皮肤）：条带=竖臂∪弯心∪横臂
    （turn_path_u 定义域），每像素色=PAT16[(u−phase) mod 16]——
    phase=帧号×PX_PER_FRAME（u 律与 E3/E4 皮肤逐式相同）。"""
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
    与 C# Era5ArtMappingTests 双侧断言）。"""
    strip = Canvas(TURN_FRAMES_PER_ROW * BODY_TILE_PX,
                   (TURN_FRAMES // TURN_FRAMES_PER_ROW) * BODY_TILE_PX)
    for k in range(TURN_FRAMES):
        strip.blit(belt_turn_frame(k * PX_PER_FRAME),
                   (k % TURN_FRAMES_PER_ROW) * BODY_TILE_PX,
                   (k // TURN_FRAMES_PER_ROW) * BODY_TILE_PX)
    return strip


# ---------------------------------------------------------------- 驱动

TOWERS = {
    "tower-railgun": tower_railgun,    # tier1/max2 → l1/l2（高能档 48px，E4 激光塔同档）
    "tower-drone-nest": tower_drone_nest,  # tier2/max2 → l1/l2
    "tower-force-field": tower_force_field,  # max_tier1 → l1 单档（支援设施不升级）
}
TOWER_LEVELS = {
    "tower-railgun": (1, 2),
    "tower-drone-nest": (1, 2),
    "tower-force-field": (1,),
}

ENEMIES = {
    "enemy-nano-swarm": enemy_nano_swarm,
    "enemy-combat-frame": enemy_combat_frame,
    "enemy-siege-titan": enemy_siege_titan,
    "enemy-hover-striker": enemy_hover_striker,
    "enemy-lancer-drone": enemy_lancer_drone,
    "enemy-barrier-mech": enemy_barrier_mech,
    "enemy-melt-drone": enemy_melt_drone,
    "enemy-nexus-core": enemy_nexus_core,
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
    boss = os.path.join(out_root, "enemies", "boss-dominator.png")
    boss_dominator().write(boss)
    wrote.append(boss)
    drone = os.path.join(out_root, "units", "drone-right.png")
    drone_right().write(drone)
    wrote.append(drone)
    ui = os.path.join(out_root, "ui", "panel-era5.png")
    panel_palette().write(ui)
    wrote.append(ui)
    tiles = {
        "floor-nano": lambda: tile_floor_nano(),
        "floor-nano-lattice": lambda: tile_floor_nano_lattice(),
        "road-flux": lambda: tile_road_flux(0),
        "road-flux-alt": lambda: tile_road_flux(1),
        "wall-barrier": lambda: tile_wall_barrier(False),
        "wall-barrier-alt": lambda: tile_wall_barrier(True),
        "floor-rubble-nano": lambda: tile_floor_rubble_nano(False),
        "floor-rubble-nano-alt": lambda: tile_floor_rubble_nano(True),
        "water-coolant": lambda: tile_water_coolant(),
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
    tower_anim = os.path.join(out_root, "anim", "tower-force-field-anim.png")
    tower_force_field_strip().write(tower_anim)
    wrote.append(tower_anim)
    return wrote


# ---------------------------------------------------------------- 自检

def _sha256(path):
    with open(path, "rb") as fh:
        return hashlib.sha256(fh.read()).hexdigest()


def check_turn_strip(out_root):
    """转弯 8 帧帧边界对齐断言（E5 皮肤，F-127 验收核心）：
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
        second = os.path.join(tmp, "era5")
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
    """力场塔四相条带断言（T-157 ⑥）：①画布 64×64（2×2 网格 32px 帧）；
    ②第 0 相=canonical 静止底稿（与 tower_force_field(1) 逐像素同——
    决策层静态基准帧契约）；③四相两两不同（循环非退化）。返回 (ok, 说明)。"""
    strip = tower_force_field_strip()
    if (strip.width, strip.height) != (64, 64):
        return False, f"strip size {strip.width}x{strip.height} != 64x64"
    canonical = tower_force_field(1)
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
    return True, "4-phase pulse breathing, phase0=canonical"


def run_self_test(out_root):
    """①asset_validator 全目录；②转弯 8 帧对齐；③力场塔四相条带；
    ④确定性 sha256。"""
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
        description="E5 占位美术程序合成器（T-142）：三塔/八敌+主宰/无人机/"
                    "UI 色板/地形 9 槽/传送带 E5 皮肤（20/s 档双层动画）")
    parser.add_argument("--out", default=None,
                        help="输出根目录（默认=presentation/EraTD/textures/era5）")
    parser.add_argument("--self-test", action="store_true",
                        help="生成后调 asset_validator+8 帧对齐+确定性自校验（任一失败非 0 退出）")
    args = parser.parse_args(argv)

    if args.out:
        out_root = os.path.normpath(args.out)
    else:
        out_root = os.path.normpath(os.path.join(
            os.path.dirname(os.path.abspath(__file__)),
            "..", "..", "game", "presentation", "EraTD", "textures", "era5"))

    for p in build_all(out_root):
        print(f"wrote {p}")

    if args.self_test:
        rc, _report, _msg = run_self_test(out_root)
        return rc
    return 0


if __name__ == "__main__":
    sys.exit(main())
