#!/usr/bin/env python3
"""
era1_placeholders.py — E1 占位美术程序合成器（任务 T-094，Wave 4，E 泳道，组件 C-28）

用途：为 E1（远古时代）确定性合成占位像素图——补齐 T-094 冻结盘点发现的
"有实体无素材"缺口：四塔×两级（木箭塔/落石台/尖刺陷阱/篝火，era1:towers.json）、
八敌+踏群者 Boss（era1:enemies.json）、初火守望者英雄头像（era2:heroes.json
era1 行）、民夫载具（era1:era.json logistics.carrier=porter）与 E1 面板色板
（与 T-078 panel-era2 对偶）。
背景：T-045 只入库了 E1 地形 8 tile；敌/塔/英雄/载具/UI 在冻结盘点时全部
缺失。CC0 源（14 号台账）无成套远古敌种可配，按 T-078 先例（24 号 §3，
任务卡预批准回退路线）走「代码合成像素图」。
红线对齐：**程序绘制**（确定性：固定坐标+固定 23 色色板，无随机数、无 AI
生图），与 21 号 §2.1「AI PNG 不进 presentation/游戏 sprite 管线」红线不
冲突；产物可由本脚本逐字节复现，人工重绘升级时以本脚本为底稿。

输出（--out DIR，默认 presentation/EraTD/textures/era1/）：
  towers/tower-wood-arrow-l1.png / -l2.png       32×32 木柱高台+侧弓
  towers/tower-rockfall-l1.png / -l2.png         32×32 石堆台+落石堆
  towers/tower-spike-trap-l1.png / -l2.png       32×32 木底板+骨尖刺
  towers/tower-campfire-l1.png / -l2.png         32×32 石圈篝火
  enemies/enemy-beastling.png … 8 种             32×32 常规敌
  enemies/boss-stomper.png                       64×64 Boss 踏群者
  heroes/hero-firstfire.png                      32×32 初火守望者头像
  units/porter-right.png                         32×32 民夫（era1 载具）
  ui/panel-era1.png                              32×32 E1 面板色板

全部输出须过 tools/ci/asset_validator.py（尺寸 16/32/48/64 白名单、≤64 色、
kebab 命名）。Boss 画布取 64（14 §3「常规 Boss 64~96」下限而非内容表
canvas_size_px=96：96 不在校验器允许清单，扩清单须 REV，见 28 号冻结清单
§5 白名单纪律）。

依赖：仅 Python 3.8+ 标准库（struct/zlib/argparse）——与 asset_validator
同纪律，不引 Pillow。CLI：python era1_placeholders.py [--out DIR] [--self-test]
--self-test：生成后即调 asset_validator 校验自身输出，任一 REJECT 退出非 0
（run_all Step 1.5 亦覆盖该输出目录）。
"""

from __future__ import annotations

import argparse
import os
import struct
import sys
import zlib

# ---------------------------------------------------------------- E1 远古色板
# 23 色（≤64 master 上限）：土 4 / 木 3 / 兽皮 3 / 骨 2 / 火 3 / 绿 3 / 血 2 / 铁 3
# 与 T-045 入库的 Tiny Dungeon 土色系 tile（tan/brown 泥地砖）同族；
# E1 层级 2 配色标识=骨白+战血涂纹（E2 用金，两代区分）。
PALETTE = {
    "dirt_light": (214, 184, 140),
    "dirt_mid": (176, 144, 100),
    "dirt_dark": (130, 102, 68),
    "dirt_shadow": (86, 66, 44),
    "wood_light": (166, 128, 82),
    "wood_mid": (124, 94, 58),
    "wood_dark": (84, 62, 38),
    "hide_light": (196, 160, 120),
    "hide_mid": (156, 122, 86),
    "hide_dark": (110, 84, 56),
    "bone": (232, 224, 200),
    "bone_shadow": (180, 170, 148),
    "fire_light": (245, 190, 90),
    "fire_mid": (228, 140, 54),
    "fire_dark": (180, 84, 32),
    "green_light": (140, 172, 96),
    "green_mid": (100, 134, 66),
    "green_dark": (66, 96, 46),
    "blood": (168, 52, 44),
    "blood_dark": (116, 32, 30),
    "iron_light": (150, 150, 158),
    "iron_mid": (105, 106, 114),
    "iron_dark": (66, 68, 76),
}


class Canvas:
    """最小像素画布：RGBA 网格，初始全透明 (0,0,0,0)。"""

    def __init__(self, width, height):
        self.width = width
        self.height = height
        self.px = [[(0, 0, 0, 0)] * width for _ in range(height)]

    def set(self, x, y, color):
        if 0 <= x < self.width and 0 <= y < self.height:
            self.px[y][x] = (color[0], color[1], color[2], 255)

    def fill(self, x, y, w, h, color):
        for yy in range(y, y + h):
            for xx in range(x, x + w):
                self.set(xx, yy, color)

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

    def write(self, path):
        raw = bytearray()
        for row in self.px:
            raw.append(0)  # PNG 行滤波 type 0
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


# ---------------------------------------------------------------- 通用部件

def ground_shadow(c, x, y, w):
    """脚下接地阴影（1px 暗土横带）。"""
    c.hline(x, y, w, PALETTE["dirt_shadow"])


def wood_legs(c, xs, y, h):
    """木结构支腿（左右成对）。"""
    for lx in xs:
        c.fill(lx, y, 2, h, PALETTE["wood_mid"])
        c.vline(lx, y, h, PALETTE["wood_light"])
        c.vline(lx + 1, y, h, PALETTE["wood_dark"])


def hide_skirt(c, x, y, w, h):
    """兽皮围裙（亮上暗下+垂条）。"""
    c.fill(x, y, w, h, PALETTE["hide_mid"])
    c.hline(x, y, w, PALETTE["hide_light"])
    for i in range(x + 1, x + w, 3):
        c.vline(i, y + 1, h - 1, PALETTE["hide_dark"])


def bone_helm(c, x, y):
    """骨盔（4×3 骨帽+护鼻条）。"""
    c.fill(x, y, 4, 3, PALETTE["bone"])
    c.hline(x, y + 3, 4, PALETTE["bone_shadow"])
    c.vline(x + 2, y + 3, 3, PALETTE["bone_shadow"])


def spear(c, x, y, h, tip=PALETTE["bone"]):
    """竖持矛（木杆+骨/铁矛头）。"""
    c.vline(x, y, h, PALETTE["wood_dark"])
    c.fill(x - 1, y, 3, 2, tip)


def club(c, x, y):
    """木棒（粗头短柄）。"""
    c.fill(x, y, 2, 6, PALETTE["wood_mid"])
    c.fill(x - 1, y, 4, 3, PALETTE["wood_dark"])
    c.set(x - 1, y, PALETTE["wood_light"])


def hide_shield(c, x, y, round_=True):
    """兽皮盾（圆盾 6×6 / 体盾 6×9）。"""
    h = 6 if round_ else 9
    c.fill(x, y, 6, h, PALETTE["hide_mid"])
    c.hline(x, y, 6, PALETTE["hide_light"])
    c.hline(x, y + h - 1, 6, PALETTE["hide_dark"])
    c.set(x + 3, y + h // 2, PALETTE["bone"])  # 骨钉


def walking_legs(c, x, y, stride=2):
    """行进双腿（前后错位）。"""
    c.fill(x, y, 2, 5, PALETTE["hide_dark"])
    c.fill(x + 3 + stride, y, 2, 5, PALETTE["hide_dark"])


def quadruped_body(c, x, y, w, h, body, body_light, body_dark, legs_y=None):
    """四足躯干（亮背线/暗腹线+四腿）。"""
    c.fill(x, y, w, h, body)
    c.hline(x, y, w, body_light)
    c.hline(x, y + h - 1, w, body_dark)
    legs_y = legs_y if legs_y is not None else y + h
    for lx in (x + 1, x + 4, x + w - 5, x + w - 2):
        c.fill(lx, legs_y, 2, 4, body_dark)


def quadruped_head(c, x, y, body, body_dark, horn=None):
    """右向兽首（4×3 头+耳+眼）。"""
    c.fill(x, y, 4, 3, body)
    c.set(x + 3, y + 1, PALETTE["blood_dark"])  # 眼
    c.set(x + 1, y - 1, body_dark)  # 耳
    if horn:
        c.set(x + 4, y + 2, PALETTE["bone"])  # 獠牙/角
        c.set(x + 4, y, horn)


# ---------------------------------------------------------------- 四塔×两级

def tower_wood_arrow(level):
    """木箭塔（era1:tower-wood-arrow）32×32：四木腿高台+木平台+侧挂弓；
    l2 骨白压条+战血小旗。"""
    c = Canvas(32, 32)
    wood_legs(c, (6, 24), 12, 14)
    c.fill(6, 12, 20, 3, PALETTE["wood_mid"])  # 台面
    c.hline(6, 12, 20, PALETTE["wood_light"])
    c.hline(6, 14, 20, PALETTE["wood_dark"])
    c.fill(8, 4, 14, 8, PALETTE["wood_mid"])  # 护栏塔屋
    c.vline(8, 4, 8, PALETTE["wood_light"])
    c.vline(21, 4, 8, PALETTE["wood_dark"])
    c.hline(9, 6, 12, PALETTE["wood_dark"])  # 瞭望缝
    c.fill(4, 2, 22, 2, PALETTE["wood_light"])  # 顶檐
    c.hline(4, 3, 22, PALETTE["wood_dark"])
    # 侧挂弓（辨识标记）
    c.fill(27, 16, 1, 6, PALETTE["wood_light"])
    c.set(26, 15, PALETTE["wood_dark"])
    c.set(26, 22, PALETTE["wood_dark"])
    if level >= 2:
        c.hline(4, 1, 22, PALETTE["bone"])
        c.hline(6, 11, 20, PALETTE["bone_shadow"])
        c.fill(14, 0, 1, 3, PALETTE["wood_dark"])  # 旗杆
        c.fill(15, 0, 4, 2, PALETTE["blood"])  # 战血小旗
    return c


def tower_rockfall(level):
    """落石台（era1:tower-rockfall）32×32：干石垒台+悬臂+顶上落石堆；
    l2 骨撑+双倍石堆。"""
    c = Canvas(32, 32)
    c.fill(6, 14, 20, 12, PALETTE["dirt_dark"])  # 干石垒台
    c.vline(6, 14, 12, PALETTE["dirt_mid"])
    c.vline(25, 14, 12, PALETTE["dirt_shadow"])
    for i in range(4):  # 错缝石纹
        c.hline(6, 17 + i * 3, 20, PALETTE["dirt_shadow"])
        c.set(10 + (i % 2) * 8, 16 + i * 3, PALETTE["dirt_mid"])
    c.fill(4, 12, 24, 2, PALETTE["wood_mid"])  # 木顶台
    c.hline(4, 12, 24, PALETTE["wood_light"])
    rocks = ((8, 6), (13, 5), (19, 6), (23, 7), (11, 10), (16, 11), (21, 11))
    for (rx, ry) in rocks[: (5 if level < 2 else 7)]:  # 落石堆
        c.fill(rx, ry, 4, 3, PALETTE["dirt_mid"])
        c.set(rx, ry, PALETTE["dirt_light"])
    c.fill(15, 2, 2, 4, PALETTE["wood_mid"])  # 悬臂吊索
    c.vline(14, 2, 4, PALETTE["wood_dark"])
    if level >= 2:
        c.vline(8, 13, 12, PALETTE["bone_shadow"])  # 骨撑
        c.vline(23, 13, 12, PALETTE["bone_shadow"])
        c.fill(15, 1, 2, 1, PALETTE["bone"])
    return c


def tower_spike_trap(level):
    """尖刺陷阱（era1:tower-spike-trap）32×32：贴地木底板+骨尖刺排；
    l1 四刺 / l2 六刺+血色预警纹。"""
    c = Canvas(32, 32)
    c.fill(3, 22, 26, 6, PALETTE["wood_mid"])  # 底板
    c.hline(3, 22, 26, PALETTE["wood_light"])
    c.hline(3, 27, 26, PALETTE["wood_dark"])
    for i in range(3, 29, 6):  # 板钉
        c.set(i, 24, PALETTE["wood_dark"])
        c.set(i + 2, 26, PALETTE["wood_dark"])
    spikes = (6, 12, 19, 25) if level < 2 else (5, 10, 14, 19, 23, 27)
    for sx in spikes:  # 骨尖刺（1px 茎+2px 骨尖）
        c.vline(sx, 10, 12, PALETTE["bone_shadow"])
        c.fill(sx - 1, 8, 3, 2, PALETTE["bone"])
        c.set(sx, 7, PALETTE["bone"])
    if level >= 2:
        c.hline(4, 21, 24, PALETTE["blood"])  # 血色预警纹
    return c


def tower_campfire(level):
    """篝火（era1:tower-campfire）32×32：石圈+交叉木柴+火苗（光环塔）；
    l2 双图腾柱+高焰。"""
    c = Canvas(32, 32)
    for sx in range(6, 27, 5):  # 石圈
        c.fill(sx, 24, 3, 2, PALETTE["dirt_dark"])
        c.set(sx, 24, PALETTE["dirt_mid"])
    c.fill(10, 22, 12, 2, PALETTE["wood_mid"])  # 交叉柴
    c.hline(8, 21, 16, PALETTE["wood_dark"])
    flame_h = 10 if level < 2 else 14
    for fy in range(flame_h):  # 三色火苗（锥形，固定相位）
        half = max(1, (flame_h - fy) // 3)
        key = "fire_light" if fy > flame_h - 4 else ("fire_mid" if fy > 2 else "fire_dark")
        c.hline(16 - half, 22 - fy, half * 2, PALETTE[key])
    c.set(16, 22 - flame_h, PALETTE["fire_light"])
    if level >= 2:
        for tx in (6, 26):  # 图腾柱
            c.fill(tx, 12, 2, 12, PALETTE["wood_dark"])
            c.set(tx, 11, PALETTE["bone"])
            c.set(tx + 1, 14, PALETTE["green_mid"])
    return c


# ---------------------------------------------------------------- 八敌+Boss

def enemy_beastling():
    """兽崽（era1:enemy-beastling）32×32：小型拱背四足，土色皮毛。"""
    c = Canvas(32, 32)
    ground_shadow(c, 8, 29, 16)
    quadruped_body(c, 9, 16, 14, 7, PALETTE["hide_mid"],
                   PALETTE["hide_light"], PALETTE["hide_dark"])
    c.hline(11, 15, 10, PALETTE["hide_dark"])  # 拱背鬃
    quadruped_head(c, 23, 17, PALETTE["hide_mid"], PALETTE["hide_dark"])
    return c


def enemy_hunter():
    """猎狼（era1:enemy-hunter）32×32：灰狼奔跑姿态，铁灰色系。"""
    c = Canvas(32, 32)
    ground_shadow(c, 5, 29, 22)
    quadruped_body(c, 7, 14, 17, 6, PALETTE["iron_mid"],
                   PALETTE["iron_light"], PALETTE["iron_dark"])
    c.fill(22, 12, 5, 3, PALETTE["iron_mid"])  # 前伸颈
    c.fill(26, 10, 4, 3, PALETTE["iron_mid"])  # 头
    c.set(29, 11, PALETTE["blood_dark"])  # 眼
    c.set(27, 9, PALETTE["iron_dark"])  # 耳
    c.set(30, 12, PALETTE["bone"])  # 獠牙
    c.fill(3, 16, 4, 2, PALETTE["iron_dark"])  # 后掠尾
    c.fill(6, 20, 2, 2, PALETTE["iron_dark"])  # 后蹬腿（奔跑姿态）
    c.fill(21, 20, 2, 2, PALETTE["iron_dark"])
    return c


def enemy_boar():
    """铠猪（era1:enemy-boar）32×32：厚皮野猪+背负骨甲板。"""
    c = Canvas(32, 32)
    ground_shadow(c, 5, 29, 22)
    quadruped_body(c, 6, 15, 18, 8, PALETTE["hide_mid"],
                   PALETTE["hide_light"], PALETTE["hide_dark"])
    quadruped_head(c, 24, 15, PALETTE["hide_dark"], PALETTE["hide_dark"],
                   horn=PALETTE["bone"])
    c.set(25, 18, PALETTE["bone"])  # 第二獠牙
    for (bx, by) in ((9, 11), (14, 10), (19, 11)):  # 骨甲板
        c.fill(bx, by, 4, 4, PALETTE["bone"])
        c.hline(bx, by + 3, 4, PALETTE["bone_shadow"])
    return c


def enemy_tribesman():
    """蛮徒（era1:enemy-tribesman）32×32：皮衣原始兵，持木棒。"""
    c = Canvas(32, 32)
    ground_shadow(c, 11, 29, 12)
    c.fill(13, 6, 5, 4, PALETTE["hide_light"])  # 头
    c.set(17, 8, PALETTE["blood_dark"])  # 眼
    c.fill(12, 5, 7, 2, PALETTE["hide_dark"])  # 发
    c.fill(12, 10, 7, 8, PALETTE["hide_mid"])  # 躯干
    c.hline(12, 10, 7, PALETTE["hide_light"])
    hide_skirt(c, 12, 15, 7, 3)
    walking_legs(c, 13, 18)
    club(c, 22, 8)  # 木棒
    return c


def enemy_warband():
    """蛮兵（era1:enemy-warband）32×32：皮甲中坚，矛+圆盾。"""
    c = Canvas(32, 32)
    ground_shadow(c, 9, 29, 16)
    c.fill(12, 5, 5, 4, PALETTE["hide_light"])  # 头
    c.set(16, 7, PALETTE["blood_dark"])
    c.fill(12, 4, 5, 2, PALETTE["hide_dark"])
    c.fill(11, 9, 7, 9, PALETTE["hide_mid"])  # 躯干
    c.hline(11, 9, 7, PALETTE["hide_light"])
    c.vline(14, 11, 5, PALETTE["hide_dark"])  # 皮带
    hide_skirt(c, 11, 15, 7, 3)
    walking_legs(c, 12, 18, stride=3)
    spear(c, 7, 4, 18)
    hide_shield(c, 20, 10)  # 圆盾
    return c


def enemy_champion():
    """蛮勇士（era1:enemy-champion）32×32：骨盔重甲+战血涂纹+双手斧。"""
    c = Canvas(32, 32)
    ground_shadow(c, 9, 29, 16)
    c.fill(12, 5, 6, 4, PALETTE["hide_light"])  # 头
    c.set(17, 7, PALETTE["blood_dark"])
    bone_helm(c, 12, 4)
    c.fill(11, 9, 8, 10, PALETTE["hide_mid"])  # 宽躯干
    c.hline(11, 9, 8, PALETTE["hide_light"])
    c.vline(15, 10, 8, PALETTE["blood"])  # 战血涂纹
    c.set(12, 12, PALETTE["blood"])
    hide_skirt(c, 11, 16, 8, 3)
    c.fill(12, 19, 3, 5, PALETTE["hide_dark"])  # 粗腿
    c.fill(16, 19, 3, 5, PALETTE["hide_dark"])
    c.vline(24, 4, 15, PALETTE["wood_dark"])  # 斧柄
    c.fill(22, 3, 6, 4, PALETTE["iron_mid"])  # 斧刃
    c.hline(22, 3, 6, PALETTE["iron_light"])
    c.fill(23, 7, 4, 1, PALETTE["bone"])  # 骨饰
    return c


def enemy_ram():
    """撞木（era1:enemy-ram）32×32：双人抬木攻城槌（对建筑 ×3 辨识：骨撞头）。"""
    c = Canvas(32, 32)
    ground_shadow(c, 4, 29, 24)
    c.fill(4, 12, 24, 4, PALETTE["wood_mid"])  # 横木
    c.hline(4, 12, 24, PALETTE["wood_light"])
    c.hline(4, 15, 24, PALETTE["wood_dark"])
    c.fill(27, 11, 4, 6, PALETTE["bone"])  # 骨撞头
    c.vline(27, 11, 6, PALETTE["bone_shadow"])
    for (hx, hy) in ((7, 7), (19, 7)):  # 两名抬手
        c.fill(hx, hy, 4, 4, PALETTE["hide_light"])  # 头
        c.fill(hx - 1, hy + 4, 6, 5, PALETTE["hide_mid"])  # 躯干
        c.vline(hx + 1, hy + 9, 3, PALETTE["hide_dark"])  # 腿
        c.vline(hx + 4, hy + 9, 3, PALETTE["hide_dark"])
    return c


def enemy_boomshrew():
    """自爆鼩（era1:enemy-boomshrew）32×32：疾行小鼠+背负火药陶罐。"""
    c = Canvas(32, 32)
    ground_shadow(c, 10, 29, 14)
    c.fill(12, 18, 12, 6, PALETTE["hide_mid"])  # 小躯
    c.hline(12, 18, 12, PALETTE["hide_light"])
    c.fill(23, 19, 3, 3, PALETTE["hide_mid"])  # 头
    c.set(25, 20, PALETTE["blood_dark"])  # 眼
    c.vline(27, 18, 2, PALETTE["hide_dark"])  # 耳
    c.hline(23, 22, 3, PALETTE["bone"])  # 牙
    for lx in (13, 16, 19, 22):  # 疾行腿
        c.fill(lx, 24, 1, 4, PALETTE["hide_dark"])
    c.fill(14, 11, 7, 7, PALETTE["dirt_dark"])  # 陶罐
    c.hline(14, 11, 7, PALETTE["dirt_mid"])
    c.vline(17, 10, 1, PALETTE["wood_dark"])  # 引线
    c.set(17, 9, PALETTE["fire_light"])  # 火星
    c.set(13, 13, PALETTE["fire_mid"])
    return c


def boss_stomper():
    """踏群者（era1:enemy-boss）64×64：巨型披甲巨兽，践踏姿态（trample 辨识）。
    画布 64（14 §3 常规 Boss 64~96 下限；96 需扩校验器清单=REV，见 28 号 §5）。"""
    c = Canvas(64, 64)
    ground_shadow(c, 8, 60, 48)
    quadruped_body(c, 10, 26, 40, 18, PALETTE["hide_mid"],
                   PALETTE["hide_light"], PALETTE["hide_dark"], legs_y=44)
    c.fill(48, 22, 10, 8, PALETTE["hide_mid"])  # 巨首
    c.set(56, 25, PALETTE["blood"])  # 赤目
    c.set(58, 25, PALETTE["blood"])
    c.fill(57, 29, 3, 2, PALETTE["bone"])  # 巨獠牙
    c.fill(53, 19, 2, 4, PALETTE["bone"])  # 双角
    c.fill(60, 20, 2, 4, PALETTE["bone"])
    for (bx, by, bw) in ((14, 18, 8), (24, 15, 9), (35, 16, 8)):  # 骨披甲
        c.fill(bx, by, bw, 8, PALETTE["bone"])
        c.hline(bx, by + 7, bw, PALETTE["bone_shadow"])
        c.set(bx + bw // 2, by + 3, PALETTE["bone_shadow"])
    c.hline(12, 36, 36, PALETTE["blood"])  # 战血纹带
    for dx in range(10, 58, 6):  # 践踏扬尘
        c.set(dx, 58, PALETTE["dirt_mid"])
        c.set(dx + 3, 57, PALETTE["dirt_mid"])
    c.fill(24, 12, 1, 6, PALETTE["wood_dark"])  # 背上战旗
    c.fill(25, 12, 5, 3, PALETTE["blood"])
    return c


# ---------------------------------------------------------------- 英雄/载具/UI

def hero_firstfire():
    """初火守望者（era1:hero-firstfire）32×32 半身头像：羽冠+火把+骨项圈。"""
    c = Canvas(32, 32)
    c.fill(0, 0, 32, 32, PALETTE["dirt_dark"])  # 底
    c.fill(10, 12, 12, 20, PALETTE["hide_mid"])  # 肩
    c.hline(10, 12, 12, PALETTE["hide_light"])
    c.fill(11, 6, 10, 8, PALETTE["hide_light"])  # 面
    c.set(14, 9, PALETTE["blood_dark"])  # 双目
    c.set(17, 9, PALETTE["blood_dark"])
    c.fill(13, 12, 6, 1, PALETTE["hide_dark"])  # 口
    c.fill(11, 4, 10, 3, PALETTE["hide_dark"])  # 发带
    for (fx, fy, key) in ((11, 1, "green_mid"), (13, 0, "green_light"),
                          (16, 0, "green_light"), (18, 0, "green_mid"),
                          (20, 1, "bone"), (10, 2, "bone")):  # 羽冠
        c.set(fx, fy, PALETTE[key])
        c.set(fx, fy + 1, PALETTE["green_dark"] if key.startswith("green") else PALETTE["bone_shadow"])
    c.hline(9, 14, 14, PALETTE["bone"])  # 骨项圈
    c.set(12, 15, PALETTE["bone_shadow"])
    c.set(19, 15, PALETTE["bone_shadow"])
    c.fill(24, 16, 4, 3, PALETTE["wood_dark"])  # 火把柄
    c.fill(23, 12, 6, 4, PALETTE["fire_mid"])  # 焰
    c.fill(24, 11, 4, 1, PALETTE["fire_light"])
    c.set(26, 10, PALETTE["fire_light"])
    c.vline(15, 16, 2, PALETTE["blood"])  # 战纹
    return c


def porter_right():
    """民夫（era1 载具 carrier=porter）32×32：背货弯腰步行（朝右）。"""
    c = Canvas(32, 32)
    ground_shadow(c, 8, 29, 15)
    c.fill(12, 6, 5, 4, PALETTE["hide_light"])  # 头
    c.set(16, 8, PALETTE["blood_dark"])
    c.fill(13, 10, 6, 7, PALETTE["hide_mid"])  # 躯干（前倾）
    c.hline(13, 10, 6, PALETTE["hide_light"])
    hide_skirt(c, 13, 14, 6, 3)
    c.fill(14, 17, 2, 5, PALETTE["hide_dark"])  # 腿
    c.fill(17, 17, 2, 5, PALETTE["hide_dark"])
    c.fill(6, 4, 10, 7, PALETTE["wood_mid"])  # 大背篓
    c.hline(6, 4, 10, PALETTE["wood_light"])
    c.hline(6, 10, 10, PALETTE["wood_dark"])
    c.vline(6, 4, 7, PALETTE["wood_light"])
    c.vline(15, 4, 7, PALETTE["wood_dark"])
    for bx in range(8, 15, 3):  # 篓内货物
        c.set(bx, 3, PALETTE["green_mid"])
        c.set(bx + 1, 3, PALETTE["dirt_mid"])
    c.vline(19, 9, 4, PALETTE["wood_dark"])  # 挂杖
    return c


def panel_palette():
    """E1 面板色板 32×32：骨白描边+土色底+4×4 色块矩阵（土/木/皮/骨四行），
    与 panel-era2 对偶（C-22 换肤取色底稿，非成品 UI 图）。"""
    c = Canvas(32, 32)
    c.fill(0, 0, 32, 32, PALETTE["dirt_shadow"])
    c.fill(1, 1, 30, 30, PALETTE["dirt_dark"])
    c.fill(2, 2, 28, 28, PALETTE["dirt_mid"])
    rows = (
        ("dirt_light", "dirt_mid", "dirt_dark", "dirt_shadow"),
        ("wood_light", "wood_mid", "wood_dark", "green_mid"),
        ("hide_light", "hide_mid", "hide_dark", "blood"),
        ("bone", "bone_shadow", "fire_mid", "fire_dark"),
    )
    for ry, row in enumerate(rows):
        for rx, key in enumerate(row):
            c.fill(4 + rx * 7, 4 + ry * 7, 6, 6, PALETTE[key])
    c.hline(2, 1, 28, PALETTE["bone"])
    c.hline(2, 30, 28, PALETTE["bone"])
    c.vline(1, 2, 28, PALETTE["bone"])
    c.vline(30, 2, 28, PALETTE["bone"])
    return c


# ---------------------------------------------------------------- 驱动

TOWERS = {
    "tower-wood-arrow": tower_wood_arrow,
    "tower-rockfall": tower_rockfall,
    "tower-spike-trap": tower_spike_trap,
    "tower-campfire": tower_campfire,
}

ENEMIES = {
    "enemy-beastling": enemy_beastling,
    "enemy-hunter": enemy_hunter,
    "enemy-boar": enemy_boar,
    "enemy-tribesman": enemy_tribesman,
    "enemy-warband": enemy_warband,
    "enemy-champion": enemy_champion,
    "enemy-ram": enemy_ram,
    "enemy-boomshrew": enemy_boomshrew,
}


def build_all(out_root):
    wrote = []
    for name, fn in sorted(TOWERS.items()):
        for level in (1, 2):
            path = os.path.join(out_root, "towers", f"{name}-l{level}.png")
            fn(level).write(path)
            wrote.append(path)
    for name, fn in sorted(ENEMIES.items()):
        path = os.path.join(out_root, "enemies", f"{name}.png")
        fn().write(path)
        wrote.append(path)
    boss = os.path.join(out_root, "enemies", "boss-stomper.png")
    boss_stomper().write(boss)
    wrote.append(boss)
    hero = os.path.join(out_root, "heroes", "hero-firstfire.png")
    hero_firstfire().write(hero)
    wrote.append(hero)
    units = os.path.join(out_root, "units", "porter-right.png")
    porter_right().write(units)
    wrote.append(units)
    ui = os.path.join(out_root, "ui", "panel-era1.png")
    panel_palette().write(ui)
    wrote.append(ui)
    return wrote


def run_self_test(out_root):
    """调 asset_validator 校验输出（towers/enemies/units/ui 常规门禁）。
    返回 (失败列表, 报告)。"""
    here = os.path.dirname(os.path.abspath(__file__))
    ci_dir = os.path.normpath(os.path.join(here, "..", "ci"))
    sys.path.insert(0, ci_dir)
    import asset_validator  # noqa: E402（同仓库零依赖校验器）

    static_dirs = [os.path.join(out_root, d)
                   for d in ("towers", "enemies", "units", "ui")]
    static_dirs.append(os.path.join(out_root, "heroes"))
    report = asset_validator.run(
        static_dirs, asset_validator.DEFAULT_ALLOWED_SIZES,
        asset_validator.DEFAULT_MAX_PALETTE, False)
    fails = [r for r in report["results"]
             if any(f["severity"] == "REJECT" for f in r["findings"])]
    return fails, report


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="E1 占位美术程序合成器（T-094）：四塔/八敌+Boss/英雄/民夫/UI 色板")
    parser.add_argument("--out", default=None,
                        help="输出根目录（默认=presentation/EraTD/textures/era1）")
    parser.add_argument("--self-test", action="store_true",
                        help="生成后调 asset_validator 自校验（REJECT 即非 0 退出）")
    args = parser.parse_args(argv)

    if args.out:
        out_root = os.path.normpath(args.out)
    else:
        out_root = os.path.normpath(os.path.join(
            os.path.dirname(os.path.abspath(__file__)),
            "..", "..", "game", "presentation", "EraTD", "textures", "era1"))

    for p in build_all(out_root):
        print(f"wrote {p}")

    if args.self_test:
        fails, report = run_self_test(out_root)
        for r in report["results"]:
            for f in r["findings"]:
                if f["severity"] != "INFO":
                    print(f"  {f['severity']} {r['path']}: {f['message']}")
        if fails:
            print(f"self-test FAIL: static_rejects={len(fails)}")
            return 1
        print(f"self-test ok: static_files={report['files']} rejected=0")
    return 0


if __name__ == "__main__":
    sys.exit(main())
