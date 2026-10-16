#!/usr/bin/env python3
"""
era3_placeholders.py — E3 占位美术程序合成器（任务 T-119，Wave 6，E 泳道，组件 C-28）

用途：为 E3（工业时代）确定性合成占位像素图——三塔×升级位（加特林/臼炮/铁甲墙，
era3/towers.json）、九敌+堡垒号 Boss（era3/enemies.json）、矿轨载具（era.json
logistics.carrier=mine_rail）与 E4 前置卡车（F-042 各时代运输载具行）、E3 面板
色板（C-22 换肤底稿）、E3 地形子集（工业地砖/铁轨/烟囱装饰）、传送带双层动画
全套素材（F-127：直线段=UV 滚动 shader 的带体/带边纹理；转弯段=8 帧动画条带）。
背景：28 号冻结清单 §6 待做 #8（E3 美术包=T-119）与 §5 Boss 画布裁定（96 需扩
SIZE-ALLOWLIST=REV，本轮取 64 下限）；E3 无 CC0 成套素材源（Kenney 三包均前代
主题），按 T-078 预批准回退路线全量走「代码合成像素图」。
红线对齐：这是**程序绘制**（确定性：固定坐标+固定色板，无随机数、无 AI 生图），
与 21 号 §2.1「AI PNG 不进 presentation/游戏 sprite 管线」红线不冲突；产物可由
本脚本逐字节复现，人工重绘升级时以本脚本为底稿。

视觉身份（31 号 §1.2 E3 行）：主色铆铁赭红 #A8503C / 辅煤黑 #2E2A28、蒸汽灰白
#C9C4BC；母题形状=铆接铁塔（桁架格子铁塔+蒸汽管）；纹理语言=铆钉、齿轮、焊缝、
煤灰；专属母题=桁架三角+齿轮（桁架里嵌一枚转动的齿轮）。Cinematics A-03
（E2→E3 跃迁，game/assets/cinematics/A/a-03.ogv）为 E3 帧参考——铁塔赭红色域。

传送带双层动画（F-127 验收核心，14 号 §3 帧数惯例行）：
  直线段 = shaders/belt-scroll.gdshader UV 滚动——本脚本供其两张 16px 纹理：
    belt/belt-body.png   动画层（带体表面，沿行进方向周期 16px，可无缝平铺）
    belt/belt-edge.png   静态层（带边钢轨+铆钉，不随 UV 滚动）
  转弯段 = anim/belt-turn-anim.png 8 帧 tile 动画（双层拆分：动画带体+静态边框）：
    4×2 网格 16px 帧（画布 64×32——8×16 横条为 128px 宽，超 SIZE-ALLOWLIST
    {16,32,48,64} 清单，故取双行网格；帧序=行优先 row*4+col）
    + belt/belt-turn-edge.png 静态弯口边框（与帧叠加，双层渲染）。
  帧边界对齐契约（与 game/src/EraCore/Presentation/BeltRenderDecisions.cs 常量
  一致，C# 侧 Era3ArtMappingTests 逐像素复验）：
    转弯路径坐标 u（像素沿带心线，南入北行东出）：
      竖臂 x∈[5,10],y∈[11,15]: u=15-y；弯心 x∈[5,10],y∈[5,10]: u=5+(x-5)+(10-y)；
      横臂 x∈[11,15],y∈[5,10]: u=15+(x-10)。全程 u∈[0,20]→转弯步长 21px。
    第 k 帧带体色 = PAT16[(u − 2k) mod 16]（图案向下游流动；每帧前进 2px，
    8 帧×2px=16px=图案周期 → 循环无缝闭合）；直段步长 16px/格。
    PAT16（周期 16）：煤黑胎面×2 档 + 黄铜防滑条×2（idx 2 亮/idx 10 暗）。

输出（--out DIR，默认 presentation/EraTD/textures/era3/）：
  towers/tower-gatling-l1.png / -l2.png            32×32 桁架铁塔+加特林
  towers/tower-mortar-l1.png / -l2.png             48×48 铆接炮座+臼炮
  towers/tower-ironclad-wall-l1.png                32×32 铆接铁甲墙（max_tier 1）
  enemies/enemy-conscript.png … 8 种               32×32 常规敌
  enemies/boss-bastion.png                         64×64 堡垒号 Boss（28 §5 裁定）
  units/mine-rail-right.png                        32×32 矿轨矿车（E3 载具）
  units/truck-right.png                            32×32 卡车（E4 前置，F-042）
  ui/panel-era3.png                                32×32 面板色板
  tiles/floor-steel / -grated / rail-iron{,-alt} /
        wall-riveted{,-alt} / floor-slag{,-alt} /
        water-industrial                           16×16 地形 9 槽
  structures/chimney.png                           32×64 烟囱装饰
  belt/belt-body.png / belt-edge.png /
        belt-turn-edge.png                         16×16 传送带双层纹理
  anim/belt-turn-anim.png                          64×32 转弯 8 帧（4×2 网格）

全部输出须过 tools/ci/asset_validator.py（尺寸 16/32/48/64 白名单、≤64 色、
kebab 命名）。--self-test 另做四类强校验：①asset_validator 全目录；②转弯 8 帧
帧边界对齐（u 律+双臂逐像素平移+循环闭合+八帧两两不同+带边零重叠）；③静态层/
动画层透明区互斥（双层叠加契约）；④确定性（二次生成 sha256 逐字节全等）。

依赖：仅 Python 3.8+ 标准库（struct/zlib/argparse/hashlib/tempfile）——与
asset_validator 同纪律，不引 Pillow。CLI：python era3_placeholders.py
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

# ---------------------------------------------------------------- E3 工业色板
# 18 色（≤64 master 上限，逐文件口径）：铆铁赭红 3（31 号主色 #A8503C 居中）/
# 煤黑 3（辅 #2E2A28）/ 蒸汽灰白 3（辅 #C9C4BC）/ 钢 3 / 黄铜 3（齿轮）/ 炉火 3
PALETTE = {
    "rust_light": (196, 110, 84),
    "rust_main": (168, 80, 60),    # 31 号 E3 主色 铆铁赭红 #A8503C
    "rust_dark": (128, 58, 42),
    "coal_dark": (46, 42, 40),     # 31 号 E3 辅色 煤黑 #2E2A28
    "coal_mid": (66, 60, 57),
    "coal_light": (94, 88, 83),
    "steam_light": (217, 213, 206),
    "steam_main": (201, 196, 188),  # 31 号 E3 辅色 蒸汽灰白 #C9C4BC
    "steam_dark": (160, 154, 146),
    "steel_light": (184, 188, 192),
    "steel_mid": (138, 143, 148),
    "steel_dark": (90, 94, 98),
    "brass_light": (217, 169, 79),
    "brass_mid": (181, 137, 58),
    "brass_dark": (138, 102, 38),
    "fire_light": (242, 166, 90),
    "fire_mid": (221, 122, 51),
    "fire_dark": (180, 85, 34),
}

# ------------------------------------------------ 传送带几何契约（F-127 核心）
# 与 game/src/EraCore/Presentation/BeltRenderDecisions.cs 的冻结常量一一对应：
BODY_TILE_PX = 16        # 带体源 tile 边长（32px 世界格 2× 整数放大，同地形先例）
TURN_FRAMES = 8          # 转弯帧数（14 §3「转弯 8 帧双层 tile 动画」）
PX_PER_FRAME = 2         # 每帧图案前进 px（8 帧×2=16=图案周期 → 无缝循环）
STRAIGHT_PATH_PX = 16    # 直段每格路径长（=源 tile 高，可无缝平铺）
TURN_PATH_PX = 21        # 转弯路径 u∈[0,20] 共 21px（u 律见模块 docstring）
TURN_FRAMES_PER_ROW = 4  # 动画条带 4×2 网格（横条 8 帧为 128px 宽超尺寸白名单）

# 带体表面图案（周期 16）：煤黑胎面两档 + 黄铜防滑条（idx2 亮/idx10 暗）
PAT16 = (
    "coal_dark", "coal_dark", "brass_mid", "coal_dark", "coal_dark",
    "coal_dark", "coal_dark", "coal_dark", "coal_mid", "coal_mid",
    "brass_dark", "coal_mid", "coal_mid", "coal_mid", "coal_mid", "coal_mid",
)


def turn_path_u(x, y):
    """转弯 tile 路径坐标 u（南入北行东出的规范右弯）；带外返回 None。"""
    if 5 <= x <= 10 and 11 <= y <= 15:        # 竖臂（自南缘上行）
        return 15 - y
    if 5 <= x <= 10 and 5 <= y <= 10:         # 弯心方块（对角灯丝）
        return 5 + (x - 5) + (10 - y)
    if 11 <= x <= 15 and 5 <= y <= 10:        # 横臂（东出）
        return 15 + (x - 10)
    return None


class Canvas:
    """最小像素画布：RGBA 网格，初始全透明 (0,0,0,0)。与 era2 合成器同款
    （PNG 写出逐字节确定：zlib level 9、无辅助块）。"""

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


# ---------------------------------------------------------------- 工业部件库

def rivet(c, x, y, key="brass_mid"):
    """铆钉：单像素黄铜点（E3 纹理语言基准件）。"""
    c.set(x, y, PALETTE[key])


def rivet_row(c, x, y, w, step=3, key="brass_mid"):
    """横排铆钉（step 周期）。"""
    for xx in range(x, x + w, step):
        rivet(c, xx, y, key)


def plate(c, x, y, w, h, base="rust_main", light="rust_light", dark="rust_dark",
          rivets=True):
    """铆接铁板：左亮右暗+顶亮底暗+四角铆钉（焊缝=明暗边线）。"""
    c.fill(x, y, w, h, PALETTE[base])
    c.vline(x, y, h, PALETTE[light])
    c.vline(x + w - 1, y, h, PALETTE[dark])
    c.hline(x, y, w, PALETTE[light])
    c.hline(x, y + h - 1, w, PALETTE[dark])
    if rivets and w >= 4 and h >= 4:
        rivet(c, x + 1, y + 1)
        rivet(c, x + w - 2, y + 1)
        rivet(c, x + 1, y + h - 2)
        rivet(c, x + w - 2, y + h - 2)


def truss_legs(c, x, y, w, h, key="steel_mid", light="steel_light", dark="steel_dark"):
    """桁架格子塔身：双立柱+X 交叉腹杆（E3 母题形状基准件）。"""
    c.vline(x, y, h, PALETTE[light])
    c.vline(x + 1, y, h, PALETTE[key])
    c.vline(x + w - 2, y, h, PALETTE[key])
    c.vline(x + w - 1, y, h, PALETTE[dark])
    span = w - 3
    for seg in range(0, h - 1, 5):
        for i in range(1, span):  # 交叉斜杆（每 5px 一节，正反交替）
            c.set(x + 1 + i, y + seg + i, PALETTE[dark])
            c.set(x + w - 2 - i, y + seg + i, PALETTE[key])
        c.hline(x, y + seg + 5, w, PALETTE[key])  # 节点横杆


def gear(c, cx, cy, r=2, key="brass_mid", dark="brass_dark"):
    """小齿轮：外圈齿+暗心（E3 专属母题基准件，桁架内嵌）。"""
    c.set(cx, cy - r, PALETTE[key])
    c.set(cx, cy + r, PALETTE[key])
    c.set(cx - r, cy, PALETTE[key])
    c.set(cx + r, cy, PALETTE[key])
    c.set(cx + r - 1, cy - r + 1, PALETTE[dark])
    c.set(cx - r + 1, cy + r - 1, PALETTE[dark])
    for (dx, dy) in ((0, -1), (0, 1), (-1, 0), (1, 0), (1, -1), (-1, 1)):
        c.set(cx + dx, cy + dy, PALETTE[key])
    c.set(cx, cy, PALETTE[dark])


def steam_puff(c, x, y, w=3):
    """蒸汽白烟（蒸汽管排汽，31 号纹理语言）。"""
    c.set(x, y, PALETTE["steam_light"])
    c.set(x + w - 1, y, PALETTE["steam_main"])
    if w >= 3:
        c.set(x + 1, y - 1, PALETTE["steam_light"])
    c.set(x + 1, y + 1, PALETTE["steam_main"])


def boiler_base(c, x, y, w, h, level=1):
    """铆接锅炉基座：铁板+煤黑炉门+炉火窗+顶部铆钉排；level2 黄铜箍。"""
    plate(c, x, y, w, h)
    door_w = max(4, w // 3)
    dx = x + (w - door_w) // 2
    c.fill(dx, y + 2, door_w, h - 5, PALETTE["coal_dark"])
    c.hline(dx, y + 3, door_w, PALETTE["coal_light"])
    fw = door_w - 2
    if fw >= 2:
        c.fill(dx + 1, y + h - 4, fw, 2, PALETTE["fire_mid"])
        c.set(dx + 1, y + h - 4, PALETTE["fire_light"])
    rivet_row(c, x + 1, y + 1, w - 2, 3)
    if level >= 2:
        c.hline(x, y + 2, w, PALETTE["brass_mid"])
        c.hline(x, y + h - 3, w, PALETTE["brass_mid"])


# ---------------------------------------------------------------- 三塔（era3/towers.json）

def tower_gatling(level):
    """加特林（era3:tower-gatling）32×32：桁架铁塔+锅炉基座+顶置多管加特林；
    L2 黄铜齿轮标+双蒸汽管（31 号 E3 母题完整体）。"""
    c = Canvas(32, 32)
    boiler_base(c, 6, 22, 20, 9, level)
    truss_legs(c, 10, 7, 12, 15)
    gear(c, 16, 13, 2)  # 桁架里嵌一枚转动的齿轮（E3 专属母题）
    plate(c, 9, 4, 14, 4, "steel_mid", "steel_light", "steel_dark")
    c.fill(11, 1, 10, 3, PALETTE["steel_dark"])  # 多管炮组（俯视 4 管）
    for bx in (12, 14, 16, 18):
        c.fill(bx, 0, 1, 3, PALETTE["steel_mid"])
        c.set(bx, 0, PALETTE["steel_light"])
    c.fill(14, 4, 4, 2, PALETTE["coal_dark"])  # 弹链匣
    rivet(c, 15, 5)
    steam_puff(c, 3, 6)
    steam_puff(c, 27, 9)
    if level >= 2:
        gear(c, 11, 24, 1)  # 基座齿轮徽（L2 黄铜标识=代际升级位）
        gear(c, 22, 24, 1)
        c.vline(9, 1, 4, PALETTE["brass_mid"])  # 黄铜蒸汽管
        c.vline(23, 1, 4, PALETTE["brass_mid"])
        c.set(16, 26, PALETTE["brass_light"])
    return c


def tower_mortar(level):
    """臼炮（era3:tower-mortar）48×48：铆接重炮座+短粗臼炮管+煤仓；L2 黄铜箍环。"""
    c = Canvas(48, 48)
    boiler_base(c, 6, 30, 36, 16, level)
    truss_legs(c, 12, 14, 24, 16)
    gear(c, 18, 19, 2)
    gear(c, 30, 19, 2)
    plate(c, 14, 10, 20, 5, "steel_mid", "steel_light", "steel_dark")
    c.fill(20, 2, 8, 9, PALETTE["steel_dark"])  # 臼炮管（短粗重管）
    c.hline(20, 2, 8, PALETTE["steel_mid"])
    c.vline(20, 2, 9, PALETTE["steel_mid"])
    c.hline(20, 4, 8, PALETTE["coal_dark"])  # 炮口
    c.fill(22, 0, 4, 2, PALETTE["coal_dark"])
    c.fill(21, 11, 6, 2, PALETTE["brass_mid"])  # 耳轴
    c.fill(6, 24, 6, 5, PALETTE["coal_dark"])  # 煤仓（右）
    c.fill(36, 24, 6, 5, PALETTE["coal_dark"])
    c.set(8, 24, PALETTE["coal_light"])
    c.set(38, 24, PALETTE["coal_light"])
    steam_puff(c, 4, 26)
    steam_puff(c, 41, 20)
    if level >= 2:
        c.hline(20, 6, 8, PALETTE["brass_mid"])  # 炮管黄铜箍
        c.hline(20, 8, 8, PALETTE["brass_mid"])
        rivet(c, 24, 33)
        rivet(c, 24, 43)
    return c


def tower_ironclad_wall(level):
    """铁甲墙（era3:tower-ironclad-wall）32×32：通高铆接铁甲+桁架斜撑（纯阻挡
    支援塔，max_tier=1 → 单档）。"""
    assert level == 1  # 墙体不升级（内容表 max_tier=1；越级调用=合成器契约违例）
    c = Canvas(32, 32)
    plate(c, 2, 2, 28, 28)
    c.fill(6, 6, 20, 20, PALETTE["rust_dark"])
    c.vline(6, 6, 20, PALETTE["rust_light"])
    c.hline(6, 6, 20, PALETTE["rust_light"])
    for i in range(20):  # 桁架 X 斜撑（母题）
        if 6 + i < 26:
            c.set(6 + i, 6 + i, PALETTE["steel_mid"])
            c.set(25 - i, 6 + i, PALETTE["steel_dark"])
    c.hline(6, 15, 20, PALETTE["steel_mid"])
    rivet(c, 4, 4)
    rivet(c, 27, 4)
    rivet(c, 4, 27)
    rivet(c, 27, 27)
    gear(c, 16, 15, 2)  # 中心齿轮徽
    return c


# ---------------------------------------------------------------- 九敌+Boss（era3/enemies.json）

def _shadow(c, x, y, w):
    """脚下接地阴影（1px 煤灰横带）。"""
    c.hline(x, y, w, PALETTE["coal_mid"])


def _legs(c, xs, y, h=5, key="coal_dark"):
    for lx in xs:
        c.fill(lx, y, 2, h, PALETTE[key])


def enemy_conscript():
    """征召兵（era3:enemy-conscript）32×32：煤灰工装+布帽+肩扛镐（swarm 辨识：
    无甲轻装成群）。"""
    c = Canvas(32, 32)
    _shadow(c, 11, 29, 11)
    c.fill(13, 6, 5, 4, PALETTE["steam_light"])  # 面
    c.set(17, 8, PALETTE["coal_dark"])  # 眼
    c.fill(12, 4, 7, 2, PALETTE["coal_mid"])  # 布帽
    c.hline(12, 4, 7, PALETTE["coal_light"])
    c.fill(12, 10, 8, 8, PALETTE["coal_light"])  # 工装躯干
    c.hline(12, 10, 8, PALETTE["steam_dark"])
    c.vline(16, 11, 6, PALETTE["coal_mid"])  # 背带
    c.fill(12, 16, 8, 3, PALETTE["coal_mid"])
    _legs(c, (13, 17), 19)
    c.vline(24, 2, 14, PALETTE["coal_light"])  # 镐柄
    c.fill(23, 0, 3, 3, PALETTE["steel_mid"])  # 镐头
    c.set(24, 0, PALETTE["steel_light"])
    return c


def enemy_line_infantry():
    """线列兵（era3:enemy-line-infantry）32×32：钢盔+前装线列枪（armor=6 辨识：
    钢盔+胸甲板）。"""
    c = Canvas(32, 32)
    _shadow(c, 9, 29, 15)
    c.fill(13, 6, 5, 4, PALETTE["steam_light"])
    c.set(17, 8, PALETTE["coal_dark"])
    c.fill(12, 3, 7, 3, PALETTE["steel_mid"])  # 钢盔
    c.hline(12, 3, 7, PALETTE["steel_light"])
    c.fill(12, 5, 7, 1, PALETTE["steel_dark"])  # 盔沿
    plate(c, 12, 10, 8, 8, "steel_dark", "steel_mid", "coal_dark")  # 胸甲板
    c.fill(12, 16, 8, 3, PALETTE["coal_mid"])
    _legs(c, (13, 17), 19, key="coal_dark")
    c.fill(22, 12, 1, 8, PALETTE["coal_light"])  # 枪托
    c.fill(22, 6, 1, 7, PALETTE["steel_dark"])  # 枪管
    c.set(22, 5, PALETTE["steel_mid"])
    c.fill(21, 13, 3, 1, PALETTE["brass_mid"])  # 火门
    return c


def enemy_ram():
    """蒸汽冲车（era3:enemy-ram）32×32：钢壳冲角+锅炉+履轮（对建筑 ×3/armor=16
    辨识：钢犁撞头+排汽管）。"""
    c = Canvas(32, 32)
    _shadow(c, 3, 29, 26)
    plate(c, 4, 10, 22, 10, "rust_main", "rust_light", "rust_dark")
    c.fill(26, 8, 5, 12, PALETTE["steel_mid"])  # 钢犁撞头
    c.hline(26, 8, 5, PALETTE["steel_light"])
    c.vline(30, 8, 12, PALETTE["steel_dark"])
    c.fill(29, 13, 2, 3, PALETTE["steel_dark"])
    c.fill(7, 6, 4, 4, PALETTE["coal_dark"])  # 锅炉烟囱
    c.hline(7, 6, 4, PALETTE["coal_light"])
    steam_puff(c, 7, 4)
    c.fill(6, 20, 18, 4, PALETTE["coal_dark"])  # 车底
    for wx in (8, 16, 22):  # 履轮组
        c.fill(wx, 22, 3, 4, PALETTE["steel_dark"])
        c.set(wx + 1, 23, PALETTE["coal_light"])
    rivet_row(c, 6, 11, 16, 4)
    return c


def enemy_scout_rider():
    """铁骑斥候（era3:enemy-scout-rider）32×32：蒸汽摩托+前倾骑手（opening_charge
    辨识：疾驰前倾+排汽白线）。"""
    c = Canvas(32, 32)
    _shadow(c, 3, 29, 26)
    c.fill(6, 16, 16, 5, PALETTE["coal_dark"])  # 车架
    c.hline(6, 16, 16, PALETTE["coal_light"])
    c.fill(21, 13, 5, 5, PALETTE["steel_mid"])  # 前叉+头灯
    c.hline(21, 13, 5, PALETTE["steel_light"])
    c.set(25, 12, PALETTE["fire_light"])  # 头灯
    for wx in (8, 24):  # 轮（辐条）
        c.fill(wx - 2, 21, 5, 5, PALETTE["coal_dark"])
        c.set(wx, 23, PALETTE["steel_mid"])
        c.set(wx - 2, 23, PALETTE["steel_mid"])
        c.set(wx + 2, 23, PALETTE["steel_mid"])
    c.fill(6, 12, 5, 4, PALETTE["rust_main"])  # 锅炉筒
    c.hline(6, 12, 5, PALETTE["rust_light"])
    steam_puff(c, 2, 10)
    c.set(0, 9, PALETTE["steam_main"])
    c.fill(12, 8, 4, 4, PALETTE["steam_light"])  # 骑手（前倾）
    c.set(15, 9, PALETTE["coal_dark"])
    c.fill(13, 4, 5, 2, PALETTE["steel_mid"])  # 风镜盔
    c.fill(11, 12, 6, 5, PALETTE["coal_mid"])  # 骑手皮衣
    c.fill(10, 14, 4, 2, PALETTE["coal_dark"])  # 前倾把手臂
    return c


def enemy_gunner():
    """排枪手（era3:enemy-gunner）32×32：架设排枪+弹药箱（stationary_ranged_attack
    辨识：三脚枪架+固定姿）。"""
    c = Canvas(32, 32)
    _shadow(c, 8, 29, 17)
    c.fill(12, 6, 5, 4, PALETTE["steam_light"])  # 面
    c.set(16, 8, PALETTE["coal_dark"])
    c.fill(11, 4, 7, 2, PALETTE["coal_dark"])  # 宽檐帽
    c.hline(10, 5, 9, PALETTE["coal_mid"])
    c.fill(12, 10, 7, 7, PALETTE["coal_light"])  # 工装
    c.fill(12, 15, 7, 3, PALETTE["coal_mid"])
    c.fill(13, 18, 2, 6, PALETTE["coal_dark"])  # 跪姿腿
    c.fill(17, 18, 2, 6, PALETTE["coal_dark"])
    c.fill(6, 10, 20, 2, PALETTE["steel_dark"])  # 排枪身（横架）
    c.hline(6, 10, 20, PALETTE["steel_mid"])
    c.fill(9, 8, 2, 2, PALETTE["steel_mid"])  # 双管口
    c.fill(21, 8, 2, 2, PALETTE["steel_mid"])
    c.fill(13, 12, 2, 2, PALETTE["brass_mid"])  # 拉栓
    c.vline(15, 12, 2, PALETTE["coal_dark"])  # 架脚
    c.fill(3, 21, 7, 5, PALETTE["coal_dark"])  # 弹药箱
    c.hline(3, 22, 7, PALETTE["coal_light"])
    c.set(6, 24, PALETTE["brass_mid"])
    return c


def enemy_ironclad_guard():
    """铁甲卫兵（era3:enemy-ironclad-guard）32×32：通高铁盾+铆接重甲（shield_wall
    辨识：塔盾覆身+全盔）。"""
    c = Canvas(32, 32)
    _shadow(c, 8, 29, 17)
    c.fill(21, 4, 5, 4, PALETTE["steel_light"])  # 全盔
    c.set(25, 6, PALETTE["coal_dark"])
    c.fill(20, 2, 7, 2, PALETTE["steel_mid"])  # 盔顶
    c.hline(20, 2, 7, PALETTE["steel_light"])
    plate(c, 21, 8, 5, 9, "steel_dark", "steel_mid", "coal_dark")
    c.fill(21, 15, 5, 3, PALETTE["coal_mid"])
    _legs(c, (21, 24), 18, key="steel_dark")
    plate(c, 5, 3, 13, 23, "rust_main", "rust_light", "rust_dark")  # 通高铁盾
    rivet_row(c, 6, 5, 11, 3)
    rivet_row(c, 6, 23, 11, 3)
    c.fill(8, 10, 7, 7, PALETTE["rust_dark"])  # 盾面桁架十字
    c.vline(11, 10, 7, PALETTE["steel_mid"])
    c.hline(8, 13, 7, PALETTE["steel_mid"])
    gear(c, 11, 13, 1)  # 盾徽齿轮
    return c


def enemy_sapper():
    """爆破工兵（era3:enemy-sapper）32×32：背负炸药+手持雷管（death_explode
    辨识：红色炸药箱+引线）。"""
    c = Canvas(32, 32)
    _shadow(c, 10, 29, 13)
    c.fill(13, 6, 5, 4, PALETTE["steam_light"])
    c.set(17, 8, PALETTE["coal_dark"])
    c.fill(12, 4, 7, 2, PALETTE["coal_dark"])  # 便帽
    c.fill(12, 10, 7, 8, PALETTE["coal_light"])
    c.hline(12, 10, 7, PALETTE["steam_dark"])
    c.fill(12, 16, 7, 3, PALETTE["coal_mid"])
    _legs(c, (13, 17), 19)
    c.fill(21, 6, 6, 8, PALETTE["rust_main"])  # 背负炸药箱
    c.hline(21, 6, 6, PALETTE["rust_light"])
    c.set(23, 8, PALETTE["fire_light"])  # 火药桶盖
    c.set(24, 8, PALETTE["fire_mid"])
    rivet(c, 22, 12)
    rivet(c, 25, 12)
    c.vline(26, 3, 4, PALETTE["coal_light"])  # 引线杆
    c.set(26, 2, PALETTE["fire_light"])  # 雷管火花
    c.set(27, 3, PALETTE["fire_mid"])
    c.fill(7, 11, 3, 3, PALETTE["steel_mid"])  # 手持起爆器
    c.set(7, 10, PALETTE["brass_mid"])
    return c


def enemy_overseer():
    """工头军士（era3:enemy-overseer）32×32：礼帽+怀表链+警棍（war_drums 辨识：
    高帽工头+铜哨）。"""
    c = Canvas(32, 32)
    _shadow(c, 8, 29, 17)
    c.fill(13, 6, 5, 4, PALETTE["steam_light"])
    c.set(17, 8, PALETTE["coal_dark"])
    c.fill(11, 4, 9, 2, PALETTE["coal_dark"])  # 高礼帽
    c.fill(12, 3, 7, 1, PALETTE["coal_light"])
    c.hline(11, 5, 9, PALETTE["coal_mid"])
    c.fill(12, 10, 8, 8, PALETTE["coal_mid"])  # 呢子大衣
    c.hline(12, 10, 8, PALETTE["coal_light"])
    c.vline(16, 11, 6, PALETTE["coal_dark"])
    c.fill(12, 16, 8, 3, PALETTE["coal_dark"])
    _legs(c, (13, 17), 19, key="coal_dark")
    c.hline(13, 12, 6, PALETTE["brass_mid"])  # 怀表链
    c.set(17, 13, PALETTE["brass_light"])
    c.fill(23, 10, 2, 9, PALETTE["coal_dark"])  # 警棍
    c.set(23, 9, PALETTE["steel_mid"])
    c.set(24, 9, PALETTE["steel_light"])
    c.set(10, 6, PALETTE["brass_light"])  # 铜哨
    c.set(9, 7, PALETTE["brass_mid"])
    return c


def boss_bastion():
    """堡垒号（era3:enemy-boss）64×64：钢铁堡垒陆舰——铆接装甲船体+双烟囱+前犁
    撞角+侧舷炮廓（trample/war_cry 辨识：履带车体+汽笛喇叭）。画布 64（28 §5
    裁定：内容表 canvas_size_px=96，96 不在校验器清单，取「常规 Boss 64~96」
    下限 64 合规；升 96 须随 SIZE-ALLOWLIST 扩展走 REV）。"""
    c = Canvas(64, 64)
    _shadow(c, 4, 60, 56)
    plate(c, 8, 30, 46, 18, "rust_main", "rust_light", "rust_dark")  # 主装甲带
    plate(c, 14, 18, 30, 12, "steel_mid", "steel_light", "steel_dark")  # 上层舰桥
    c.fill(18, 20, 10, 4, PALETTE["coal_dark"])  # 舰桥窗
    c.hline(18, 20, 10, PALETTE["coal_light"])
    gear(c, 36, 24, 2)  # 舰桥齿轮徽（E3 母题）
    rivet_row(c, 9, 32, 44, 4)
    rivet_row(c, 9, 45, 44, 4)
    for cx in (20, 40):  # 双烟囱
        c.fill(cx, 6, 6, 12, PALETTE["coal_dark"])
        c.hline(cx, 6, 6, PALETTE["coal_light"])
        c.hline(cx, 7, 6, PALETTE["steel_dark"])
        steam_puff(c, cx - 1, 4, 4)
        c.set(cx + 7, 3, PALETTE["steam_main"])
        c.set(cx - 2, 2, PALETTE["steam_light"])
    c.fill(54, 34, 8, 10, PALETTE["steel_mid"])  # 前犁撞角
    c.hline(54, 34, 8, PALETTE["steel_light"])
    c.fill(59, 38, 3, 3, PALETTE["steel_dark"])
    for gx in (12, 30):  # 侧舷炮廓
        c.fill(gx, 48, 8, 4, PALETTE["coal_dark"])
        c.set(gx + 7, 50, PALETTE["steel_mid"])
    c.fill(4, 50, 56, 6, PALETTE["coal_dark"])  # 履带车底盘
    for tx in range(6, 58, 6):  # 履齿
        c.set(tx, 51, PALETTE["steel_mid"])
        c.set(tx, 55, PALETTE["coal_light"])
    c.fill(46, 24, 6, 3, PALETTE["brass_mid"])  # 汽笛喇叭（war_cry）
    c.set(52, 24, PALETTE["brass_light"])
    c.set(45, 22, PALETTE["steam_light"])
    c.set(53, 22, PALETTE["steam_main"])
    return c


# ---------------------------------------------------------------- 载具（F-042）

def mine_rail_right():
    """矿轨矿车（E3 载具 carrier=mine_rail）32×32（朝右）：钢制矿车+煤斗+双轨轮。"""
    c = Canvas(32, 32)
    _shadow(c, 4, 29, 25)
    c.hline(0, 26, 32, PALETTE["steel_dark"])  # 轨面
    c.hline(0, 27, 32, PALETTE["coal_mid"])
    for tx in (2, 8, 14, 20, 26):  # 轨枕
        c.set(tx, 28, PALETTE["coal_dark"])
        c.set(tx, 28, PALETTE["coal_dark"])
    plate(c, 6, 12, 18, 10, "steel_mid", "steel_light", "steel_dark")
    c.fill(8, 9, 14, 4, PALETTE["coal_dark"])  # 煤斗堆
    c.set(10, 8, PALETTE["coal_light"])
    c.set(15, 8, PALETTE["coal_light"])
    c.set(13, 7, PALETTE["coal_light"])
    c.fill(6, 22, 18, 2, PALETTE["steel_dark"])  # 车底架
    for wx in (10, 20):  # 轨轮
        c.fill(wx - 1, 24, 3, 3, PALETTE["coal_dark"])
        c.set(wx, 25, PALETTE["steel_mid"])
    c.vline(24, 15, 4, PALETTE["brass_mid"])  # 车钩（朝右）
    c.set(24, 14, PALETTE["brass_light"])
    rivet(c, 8, 14)
    rivet(c, 21, 14)
    return c


def truck_right():
    """卡车（E4 前置载具，F-042 阶梯第 3 档=传送带/卡车）32×32（朝右）：
    钢舱驾驶室+敞斗货箱。"""
    c = Canvas(32, 32)
    _shadow(c, 2, 29, 28)
    c.fill(3, 12, 16, 8, PALETTE["coal_dark"])  # 货箱底盘
    c.hline(3, 12, 16, PALETTE["coal_light"])
    c.fill(4, 8, 13, 5, PALETTE["coal_mid"])  # 敞斗
    c.hline(4, 8, 13, PALETTE["coal_light"])
    c.fill(6, 7, 5, 2, PALETTE["coal_light"])  # 斗内散煤
    c.fill(19, 6, 9, 9, PALETTE["rust_main"])  # 驾驶室
    c.hline(19, 6, 9, PALETTE["rust_light"])
    c.vline(19, 6, 9, PALETTE["rust_light"])
    c.fill(21, 8, 5, 3, PALETTE["steam_light"])  # 挡风玻璃
    c.hline(21, 8, 5, PALETTE["steel_light"])
    c.fill(28, 12, 2, 3, PALETTE["coal_dark"])  # 前保险杠
    c.fill(2, 20, 28, 3, PALETTE["steel_dark"])  # 车架
    for wx in (8, 23):  # 轮对
        c.fill(wx - 2, 23, 5, 5, PALETTE["coal_dark"])
        c.set(wx, 25, PALETTE["steel_mid"])
        c.set(wx - 2, 25, PALETTE["steel_mid"])
        c.set(wx + 2, 25, PALETTE["steel_mid"])
    c.set(1, 11, PALETTE["fire_mid"])  # 尾灯
    return c


# ---------------------------------------------------------------- UI 色板（F-132 系）

def panel_palette():
    """E3 面板色板 32×32：铆铁赭红描边+煤黑底+4×4 色块矩阵（赭/钢/煤/铜四行），
    供 C-22 面板 E3 换肤取色（色板底稿，非成品 UI 图）。"""
    c = Canvas(32, 32)
    c.fill(0, 0, 32, 32, PALETTE["coal_dark"])
    c.rect(0, 0, 32, 32, None, PALETTE["rust_main"])
    c.rect(2, 2, 28, 28, None, PALETTE["rust_dark"])
    c.fill(4, 4, 24, 24, PALETTE["coal_mid"])
    rows = (
        ("rust_light", "rust_main", "rust_dark", "fire_mid"),
        ("steel_light", "steel_mid", "steel_dark", "steam_main"),
        ("coal_light", "coal_mid", "coal_dark", "steam_dark"),
        ("brass_light", "brass_mid", "brass_dark", "fire_dark"),
    )
    for ry, row in enumerate(rows):
        for rx, key in enumerate(row):
            c.fill(5 + rx * 6, 5 + ry * 6, 5, 5, PALETTE[key])
    return c


# ---------------------------------------------------------------- 地形 9 槽（16×16）

def tile_floor_steel():
    """钢地砖：钢板网格+四角铆钉（Plain）。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["steel_mid"])
    c.hline(0, 0, 16, PALETTE["steel_light"])
    c.vline(0, 0, 16, PALETTE["steel_light"])
    c.hline(0, 15, 16, PALETTE["steel_dark"])
    c.vline(15, 0, 16, PALETTE["steel_dark"])
    c.hline(0, 7, 16, PALETTE["steel_dark"])  # 板缝十字
    c.vline(8, 0, 16, PALETTE["steel_dark"])
    rivet(c, 2, 2)
    rivet(c, 13, 2)
    rivet(c, 2, 13)
    rivet(c, 13, 13)
    rivet(c, 6, 6, "steel_light")
    rivet(c, 10, 10, "steel_light")
    return c


def tile_floor_grated():
    """格栅地砖：漏缝格栅（Forest 代位——工业代无林地，密格栅代杂木堆）。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["coal_dark"])
    for y in range(0, 16, 2):
        c.hline(0, y, 16, PALETTE["steel_dark"])
        c.hline(0, y + 1, 16, PALETTE["coal_dark"])
    for x in range(0, 16, 4):
        c.vline(x, 0, 16, PALETTE["steel_mid"])
    c.hline(0, 0, 16, PALETTE["steel_light"])
    return c


def tile_rail_alt(sleepers_offset):
    """铁轨地砖（Road=工业道路）：双钢轨横铺+轨枕（offset 区分正/副槽）。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["coal_mid"])
    for y in range(0, 16, 2):  # 碎砟底纹
        c.hline(0, y + 1, 16, PALETTE["coal_dark"])
    for tx in range((2 + sleepers_offset) % 4, 16, 4):  # 轨枕
        c.fill(tx, 2, 1, 12, PALETTE["coal_dark"])
        c.set(tx, 3, PALETTE["coal_light"])
    c.hline(0, 3, 16, PALETTE["steel_light"])  # 双轨（轨顶亮线）
    c.hline(0, 4, 16, PALETTE["steel_mid"])
    c.hline(0, 11, 16, PALETTE["steel_light"])
    c.hline(0, 12, 16, PALETTE["steel_mid"])
    return c


def tile_wall_riveted(banded):
    """铆接铁墙（Wall）：赭红铁板+铆钉排；banded=True 副槽加煤黑横带。"""
    c = Canvas(16, 16)
    plate(c, 0, 0, 16, 16)
    rivet_row(c, 1, 2, 14, 4)
    rivet_row(c, 1, 13, 14, 4)
    c.hline(0, 8, 16, PALETTE["rust_dark"])  # 焊缝
    rivet(c, 3, 8)
    rivet(c, 12, 8)
    if banded:
        c.hline(0, 6, 16, PALETTE["coal_dark"])
        c.hline(0, 10, 16, PALETTE["coal_dark"])
    return c


def tile_floor_slag(dark_variant):
    """矿渣地砖（Rock 代位）：煤灰底+钢/赭渣粒。"""
    c = Canvas(16, 16)
    c.fill(0, 0, 16, 16, PALETTE["coal_dark"])
    specks = ((2, 3, "steel_dark"), (6, 2, "rust_dark"), (11, 4, "coal_light"),
              (4, 8, "rust_main"), (9, 9, "steel_mid"), (13, 12, "rust_dark"),
              (2, 13, "coal_light"), (7, 13, "steel_dark"))
    if dark_variant:
        specks = ((3, 2, "coal_light"), (8, 3, "steel_dark"), (12, 7, "rust_dark"),
                  (5, 6, "coal_light"), (10, 11, "rust_main"), (2, 11, "steel_dark"),
                  (14, 3, "coal_light"), (6, 14, "steel_dark"))
    for (x, y, key) in specks:
        c.set(x, y, PALETTE[key])
        c.set(x + 1, y, PALETTE[key])
    return c


def tile_water_industrial():
    """工业废水（Water）：煤黑油纹水面（周期 8 斜带，era2 water 同构异色）。"""
    c = Canvas(16, 16)
    for y in range(16):
        for x in range(16):
            band = (x + y) % 8
            key = "coal_mid" if band in (0, 1) else ("steam_dark" if band == 4 else "coal_dark")
            c.set(x, y, PALETTE[key])
    for (x, y) in ((3, 2), (11, 6), (7, 12)):
        c.set(x, y, PALETTE["steam_main"])  # 油面高光
    return c


# ---------------------------------------------------------------- 烟囱装饰

def structure_chimney():
    """烟囱（E3 装饰，structures/）32×64：铆接钢烟囱+箍环+顶口煤黑+蒸汽白烟。"""
    c = Canvas(32, 64)
    plate(c, 4, 14, 24, 46, "steel_mid", "steel_light", "steel_dark")
    for y in (22, 34, 46):  # 箍环
        c.hline(4, y, 24, PALETTE["brass_mid"])
        c.set(4, y, PALETTE["brass_light"])
    rivet(c, 8, 18)
    rivet(c, 23, 18)
    rivet(c, 8, 52)
    rivet(c, 23, 52)
    plate(c, 2, 8, 28, 6, "rust_main", "rust_light", "rust_dark")  # 顶口收沿
    c.fill(8, 3, 16, 5, PALETTE["coal_dark"])  # 顶口烟道
    c.hline(8, 3, 16, PALETTE["coal_light"])
    for (x, y, key) in ((10, 0, "steam_light"), (14, 1, "steam_main"),
                        (18, 0, "steam_light"), (12, 2, "steam_main"),
                        (20, 2, "steam_main"), (22, 1, "steam_light")):
        c.set(x, y, PALETTE[key])  # 蒸汽白烟
    plate(c, 0, 58, 32, 6, "coal_dark", "coal_light", "coal_dark")  # 基座
    rivet(c, 3, 60)
    rivet(c, 28, 60)
    return c


# ---------------------------------------------------------------- 传送带双层（F-127）

def belt_body():
    """直段带体 16×16（动画层）：竖向条带 cols 5..10，行色=PAT16[y%16]——
    沿 V 方向周期 16=源 tile 高 → shader UV 滚动可无缝平铺（滚动量取
    mod 16，EraCore BeltRenderDecisions.ShaderOffsetPx 同式）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    for y in range(BODY_TILE_PX):
        for x in range(5, 11):
            c.set(x, y, PALETTE[PAT16[y % 16]])
    return c


def belt_edge():
    """直段带边 16×16（静态层）：双侧钢轨+铆钉，带外全透明（与带体零重叠）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    c.vline(3, 0, 16, PALETTE["steel_dark"])
    c.vline(4, 0, 16, PALETTE["steel_mid"])
    c.vline(11, 0, 16, PALETTE["steel_mid"])
    c.vline(12, 0, 16, PALETTE["steel_dark"])
    for y in (1, 5, 9, 13):  # 轨面铆钉
        rivet(c, 4, y)
        rivet(c, 11, y)
    return c


def belt_turn_edge():
    """弯口带边 16×16（静态层）：规范右弯（南入东出）外/内弯钢轨+对角补角，
    与带体（u∈[0,20] 条带）零重叠。左弯=渲染层水平镜像（决策层 FlipX）。"""
    c = Canvas(BODY_TILE_PX, BODY_TILE_PX)
    # 外弯（西北侧）：竖段 col3/4 rows6..15 + 对角 (4,5)(5,4) + 横段 row3/4 cols6..15
    c.vline(3, 6, 10, PALETTE["steel_dark"])
    c.vline(4, 6, 10, PALETTE["steel_mid"])
    c.set(4, 5, PALETTE["steel_dark"])
    c.set(4, 4, PALETTE["steel_dark"])
    c.set(5, 4, PALETTE["steel_mid"])
    c.hline(6, 3, 10, PALETTE["steel_dark"])
    c.hline(6, 4, 10, PALETTE["steel_mid"])
    # 内弯（东南侧）：竖段 col11/12 rows11..15 + 横段 row11/12 cols11..15
    c.vline(11, 11, 5, PALETTE["steel_mid"])
    c.vline(12, 11, 5, PALETTE["steel_dark"])
    c.hline(11, 11, 5, PALETTE["steel_mid"])
    c.hline(12, 12, 5, PALETTE["steel_dark"])
    for y in (9, 13):  # 铆钉
        rivet(c, 4, y)
    for x in (9, 13):
        rivet(c, x, 4)
    rivet(c, 11, 13)
    rivet(c, 13, 11)
    return c


def belt_turn_frame(phase):
    """转弯单帧 16×16（动画层）：条带=竖臂∪弯心∪横臂（turn_path_u 定义域），
    每像素色=PAT16[(u−phase) mod 16]——phase=帧号×PX_PER_FRAME。"""
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
    与 C# Era3ArtMappingTests 双侧断言）。"""
    strip = Canvas(TURN_FRAMES_PER_ROW * BODY_TILE_PX,
                   (TURN_FRAMES // TURN_FRAMES_PER_ROW) * BODY_TILE_PX)
    for k in range(TURN_FRAMES):
        strip.blit(belt_turn_frame(k * PX_PER_FRAME),
                   (k % TURN_FRAMES_PER_ROW) * BODY_TILE_PX,
                   (k // TURN_FRAMES_PER_ROW) * BODY_TILE_PX)
    return strip


# ---------------------------------------------------------------- 驱动

TOWERS = {
    "tower-gatling": tower_gatling,          # tier1/max2 → l1/l2
    "tower-mortar": tower_mortar,            # tier2/max2 → l1/l2（E2 弩塔同款两级先例）
    "tower-ironclad-wall": tower_ironclad_wall,  # max_tier1 → l1 单档
}
TOWER_LEVELS = {
    "tower-gatling": (1, 2),
    "tower-mortar": (1, 2),
    "tower-ironclad-wall": (1,),
}

ENEMIES = {
    "enemy-conscript": enemy_conscript,
    "enemy-line-infantry": enemy_line_infantry,
    "enemy-ram": enemy_ram,
    "enemy-scout-rider": enemy_scout_rider,
    "enemy-gunner": enemy_gunner,
    "enemy-ironclad-guard": enemy_ironclad_guard,
    "enemy-sapper": enemy_sapper,
    "enemy-overseer": enemy_overseer,
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
    boss = os.path.join(out_root, "enemies", "boss-bastion.png")
    boss_bastion().write(boss)
    wrote.append(boss)
    for unit in ("mine-rail", "truck"):
        path = os.path.join(out_root, "units", f"{unit}-right.png")
        (mine_rail_right if unit == "mine-rail" else truck_right)().write(path)
        wrote.append(path)
    ui = os.path.join(out_root, "ui", "panel-era3.png")
    panel_palette().write(ui)
    wrote.append(ui)
    tiles = {
        "floor-steel": lambda: tile_floor_steel(),
        "floor-steel-grated": lambda: tile_floor_grated(),
        "rail-iron": lambda: tile_rail_alt(0),
        "rail-iron-alt": lambda: tile_rail_alt(2),
        "wall-riveted": lambda: tile_wall_riveted(False),
        "wall-riveted-alt": lambda: tile_wall_riveted(True),
        "floor-slag": lambda: tile_floor_slag(False),
        "floor-slag-alt": lambda: tile_floor_slag(True),
        "water-industrial": lambda: tile_water_industrial(),
    }
    for name, fn in tiles.items():
        path = os.path.join(out_root, "tiles", f"{name}.png")
        fn().write(path)
        wrote.append(path)
    chimney = os.path.join(out_root, "structures", "chimney.png")
    structure_chimney().write(chimney)
    wrote.append(chimney)
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
    """转弯 8 帧帧边界对齐断言（F-127 验收核心）：
    ①帧数/网格几何（4×2×16px=64×32）；②u 律——每帧每带体像素色=PAT16[(u−2k)%16]；
    ③双臂逐像素平移——竖臂 (x,y)@k+1 == (x,y+2)@k、横臂 (x,y)@k+1 == (x−2,y)@k
    （帧边界对齐的可直读形式）；④循环闭合——k+8 相位≡k（律内含）且八帧两两
    不同（循环非退化）；⑤带体/带边不透明集零重叠（双层叠加契约）。
    返回 (ok, 说明)。"""
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
        second = os.path.join(tmp, "era3")
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
        description="E3 占位美术程序合成器（T-119）：三塔/九敌+堡垒号/矿轨+卡车/"
                    "UI 色板/地形 9 槽/烟囱/传送带双层动画全套")
    parser.add_argument("--out", default=None,
                        help="输出根目录（默认=presentation/EraTD/textures/era3）")
    parser.add_argument("--self-test", action="store_true",
                        help="生成后调 asset_validator+8 帧对齐+确定性自校验（任一失败非 0 退出）")
    args = parser.parse_args(argv)

    if args.out:
        out_root = os.path.normpath(args.out)
    else:
        out_root = os.path.normpath(os.path.join(
            os.path.dirname(os.path.abspath(__file__)),
            "..", "..", "game", "presentation", "EraTD", "textures", "era3"))

    for p in build_all(out_root):
        print(f"wrote {p}")

    if args.self_test:
        rc, _report, _msg = run_self_test(out_root)
        return rc
    return 0


if __name__ == "__main__":
    sys.exit(main())
