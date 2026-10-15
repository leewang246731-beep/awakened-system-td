#!/usr/bin/env python3
"""
p4_achievement_icons.py — P4 成就图标程序合成器（任务 T-151 / 31 号 v2.0 §二.4，
2026-10-13；B 档=AI 概念→人工重绘红线的**占位底稿**，业主指令的程序合成路线）

用途：为 P4 氛围扩展批确定性合成 24 枚成就图标（64×64，母题符号+时代色环底）。
依据 31 号 v2.0 §二.4「成就图标 24：AI 概念图→人工像素重绘（B 档，sprite 红线
不放松）」与 T-151 验收「成就图标全过 asset_validator」——本脚本是 era2_placeholders
.py 先例的延续：**程序绘制（固定坐标+固定色板+无随机数）**，产物逐字节可复现
（二次生成 sha256 全等），人工重绘升级时以本脚本为底稿。

成就清单锚点（Progression 域实锚，24 枚分六组）：
  - 时代首通 6：F-003 通关判定 × 六时代（E1~E6）——母题=该时代守望塔形
  - 防线技艺 8：campaign/objectives.json strategy_pool obj-s1~s8（SP-8a/SP-8b）
  - 系统与军师 4：系统绑定（§12.1 觉醒）/关闭系统通关（F-097）/军师满级（B-11）
    /四人格全解锁（B-12~15，T-126）
  - 收集叙事 4：首挖知识残卷（C-20）/集齐 8 段记忆（D 层）/《零的真相》（E-16）
    /首座奇观落成（F-060）
  - 挑战 2：首杀 Boss（C-01 踏群者）/无尽模式里程碑（F-123 无尽三榜、E-04）

色环映射（每时代 4 枚，报告 38 号 §三 记录映射理由）：技艺类按机制首发时代
落位（采集/箭矢/Boss=E1；研究/物流/城墙=E2；经济/效率/仓储=E3）；系统域按
「零源于深空真相」落 E4/E5；收集叙事域收束于 E5/E6。

输出（--out DIR，可多目录 --extra DIR；默认 D 盘入库副本
game/assets/expand-v1/P4/achievements/，E 盘归档经 --extra 指定）：
  achv-era{1..6}-clear.png        时代首通 6
  achv-{no-station-lost,first-research,no-tower-sold,defense-intact,
        ammo-steady,logistics-clean,efficiency-master,key-untouched}.png
                                  防线技艺 8（obj-s1~s8）
  achv-{system-bound,system-offline,advisor-max,persona-all}.png
                                  系统与军师 4
  achv-{scroll-first,memory-all,zero-truth,wonder-first}.png
                                  收集叙事 4
  achv-{boss-first,endless-milestone}.png
                                  挑战 2

全部输出过 tools/ci/asset_validator.py（64×64 白名单内、每枚 ≤8 色 ≤64 上限、
kebab 命名）。依赖：仅 Python 3.8+ 标准库（struct/zlib/argparse/hashlib）——与
asset_validator 同纪律，不引 Pillow、不引随机数。
CLI：python p4_achievement_icons.py [--out DIR] [--extra DIR ...] [--self-test]
  --self-test：生成后即调 asset_validator 校验 + 临时目录二次生成 sha256 对比
  （确定性门禁），任一 REJECT/不等退出非 0。
"""

from __future__ import annotations

import argparse
import hashlib
import math
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib

# ---------------------------------------------------------------- 六时代色板
# 31 号 v2.0 §1.2 六时代视觉身份卡（主色/辅助色）+ 每时代补一档暗底与亮符
# （暗底/亮符为主辅色系内自定的深浅档，保证环内符号可读）。
ERA_PALETTES = {
    "e1": {  # 晨曦赭金 / 灰岩绿 / 篝火橙
        "main": (0xC9, 0x91, 0x5C),
        "accent": (0x6B, 0x7A, 0x5A),
        "light": (0xE0, 0x7B, 0x39),
        "dark": (0x6A, 0x4A, 0x2E),
        "pale": (0xEF, 0xD9, 0xBC),
    },
    "e2": {  # 青铜苍 / 陶红 / 大理石白
        "main": (0x4E, 0x8A, 0x6E),
        "accent": (0xB3, 0x59, 0x3A),
        "light": (0xE8, 0xE2, 0xD0),
        "dark": (0x2E, 0x4A, 0x3C),
        "pale": (0xCF, 0xE0, 0xD2),
    },
    "e3": {  # 铆铁赭红 / 煤黑 / 蒸汽灰白
        "main": (0xA8, 0x50, 0x3C),
        "accent": (0x2E, 0x2A, 0x28),
        "light": (0xC9, 0xC4, 0xBC),
        "dark": (0x57, 0x29, 0x1F),
        "pale": (0xE8, 0xC9, 0xB8),
    },
    "e4": {  # 电网蓝 / 混凝土灰 / 警示黄
        "main": (0x3D, 0x7E, 0xA6),
        "accent": (0x9A, 0x9A, 0x94),
        "light": (0xE3, 0xC1, 0x4F),
        "dark": (0x24, 0x48, 0x5F),
        "pale": (0xCF, 0xE4, 0xF0),
    },
    "e5": {  # 聚变青 / 聚变紫 / 纳米银白
        "main": (0x3F, 0xC1, 0xC9),
        "accent": (0x8A, 0x5F, 0xC9),
        "light": (0xDC, 0xE4, 0xE8),
        "dark": (0x1F, 0x6A, 0x6F),
        "pale": (0xE8, 0xFB, 0xFB),
    },
    "e6": {  # 星门靛蓝 / 暗物质紫黑 / 零点白青
        "main": (0x3E, 0x55, 0xC9),
        "accent": (0x1D, 0x1B, 0x33),
        "light": (0xBF, 0xE8, 0xF2),
        "dark": (0x23, 0x2C, 0x6E),
        "pale": (0xD8, 0xE4, 0xFF),
    },
}


# ---------------------------------------------------------------- 最小画布
# 与 era2_placeholders.Canvas 同构（RGBA 网格，初始全透明）。

class Canvas:
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
        if w > 0:
            for xx in range(x, x + w):
                self.set(xx, y, color)

    def vline(self, x, y, h, color):
        if h > 0:
            for yy in range(y, y + h):
                self.set(x, yy, color)

    def line(self, x0, y0, x1, y1, color, thick=1):
        """Bresenham 直线（thick>1 时向左上扩 thick×thick 笔宽）。"""
        dx = abs(x1 - x0)
        dy = -abs(y1 - y0)
        sx = 1 if x0 < x1 else -1
        sy = 1 if y0 < y1 else -1
        err = dx + dy
        x, y = x0, y0
        while True:
            if thick <= 1:
                self.set(x, y, color)
            else:
                o = thick // 2
                self.fill(x - o, y - o, thick, thick, color)
            if x == x1 and y == y1:
                break
            e2 = 2 * err
            if e2 >= dy:
                err += dy
                x += sx
            if e2 <= dx:
                err += dx
                y += sy

    def disc(self, cx, cy, r, color):
        for yy in range(cy - r, cy + r + 1):
            for xx in range(cx - r, cx + r + 1):
                if (xx - cx) ** 2 + (yy - cy) ** 2 <= r * r:
                    self.set(xx, yy, color)

    def ring(self, cx, cy, r_outer, r_inner, color):
        for yy in range(cy - r_outer, cy + r_outer + 1):
            for xx in range(cx - r_outer, cx + r_outer + 1):
                d2 = (xx - cx) ** 2 + (yy - cy) ** 2
                if r_inner * r_inner < d2 <= r_outer * r_outer:
                    self.set(xx, yy, color)

    def arc(self, cx, cy, r, a0_deg, a1_deg, color, thick=1):
        """参数角步进 1° 的圆弧（a0→a1，度，0=右，顺时针为正 y 向下）。"""
        a0, a1 = min(a0_deg, a1_deg), max(a0_deg, a1_deg)
        for a in range(int(a0), int(a1) + 1):
            rad = math.radians(a)
            x = cx + int(round(r * math.cos(rad)))
            y = cy + int(round(r * math.sin(rad)))
            if thick <= 1:
                self.set(x, y, color)
            else:
                o = thick // 2
                self.fill(x - o, y - o, thick, thick, color)

    def tri_fill(self, p0, p1, p2, color):
        """扫描三角形填充（顶点 (x,y) 元组，整型网格）。"""
        ys = [p0[1], p1[1], p2[1]]
        for yy in range(min(ys), max(ys) + 1):
            xs = []
            for (ax, ay), (bx, by) in ((p0, p1), (p1, p2), (p2, p0)):
                if (ay <= yy <= by) or (by <= yy <= ay):
                    if ay == by:
                        xs.extend((ax, bx))
                    else:
                        t = (yy - ay) / (by - ay)
                        xs.append(int(round(ax + t * (bx - ax))))
            if xs:
                for xx in range(min(xs), max(xs) + 1):
                    self.set(xx, yy, color)

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


# ---------------------------------------------------------------- 底座部件

def icon_base(c, pal):
    """时代色环底：主色环 r24~28 + 辅助色高光弧（右上 90°）+ 暗底圆盘 r22。"""
    cx = cy = 32
    c.ring(cx, cy, 28, 24, pal["main"])
    c.arc(cx, cy, 26, -80, 10, pal["accent"], thick=2)  # 右上强调弧
    c.disc(cx, cy, 22, pal["dark"])
    c.ring(cx, cy, 22, 21, pal["main"])                 # 盘缘细描


# ---------------------------------------------------------------- 母题符号
# 每枚图标中心 40×40 区域（约 x12~52 / y12~52）内绘制母题符号，亮色为主。

def sym_era1_tower(c, pal):
    """E1 木望楼：三层收分木塔+茅檐（矛尖母题由环外斜线补）。"""
    fg = pal["light"]
    c.tri_fill((32, 14), (20, 34), (44, 34), fg)          # 顶茅檐三角
    c.fill(26, 34, 12, 5, fg)                              # 平台
    c.tri_fill((32, 38), (22, 46), (42, 46), fg)           # 底层收分
    c.fill(24, 46, 16, 3, fg)
    c.line(32, 18, 32, 44, pal["dark"], thick=1)            # 中缝木纹


def sym_era2_keep(c, pal):
    """E2 石堡箭楼：山墙三角+塔身+垛口；山墙即古典母题。"""
    fg = pal["light"]
    c.tri_fill((32, 12), (21, 24), (43, 24), fg)           # 山墙
    c.fill(23, 24, 18, 20, fg)                             # 塔身
    for i, xx in enumerate((26, 32, 38)):                 # 垛口齿
        c.fill(xx - 1, 21, 2, 3, fg)
    c.fill(30, 30, 4, 6, pal["dark"])                       # 箭窗
    c.fill(30, 40, 4, 4, pal["dark"])


def sym_era3_truss(c, pal):
    """E3 铆铁桁架：三角桁架+内腹杆+嵌齿轮。"""
    fg = pal["light"]
    c.tri_fill((32, 14), (18, 44), (46, 44), fg)
    c.tri_fill((32, 22), (24, 40), (40, 40), pal["dark"])   # 挖空腹
    for yy in range(44, 48):                              # 底梁
        c.hline(18, yy, 29, fg)
    c.disc(32, 44, 4, pal["light"])                         # 桁架嵌齿轮
    c.ring(32, 44, 2, 1, pal["dark"])


def sym_era4_radar(c, pal):
    """E4 混凝土雷达塔：塔身+抛物面天线+播放键三角。"""
    fg = pal["light"]
    c.tri_fill((26, 12), (20, 24), (32, 24), pal["light"])  # 播放键（左上小）
    c.tri_fill((31, 13), (27, 20), (35, 20), pal["dark"])
    c.fill(24, 26, 16, 20, fg)                             # 浇筑塔身
    c.hline(24, 32, 16, pal["dark"]); c.hline(24, 38, 16, pal["dark"])
    c.arc(32, 20, 8, 180, 360, fg, thick=2)                # 抛物面（上半弧）
    c.line(32, 20, 32, 12, fg, thick=1)                    # 馈源杆
    c.set(32, 11, pal["light"])


def sym_era5_spire(c, pal):
    """E5 能量尖塔：尖塔+悬浮环+六边晶格点。"""
    fg = pal["light"]
    c.tri_fill((32, 10), (27, 44), (37, 44), fg)
    c.tri_fill((32, 20), (30, 44), (34, 44), pal["dark"])
    c.ring(32, 20, 8, 7, pal["light"])                      # 悬浮环
    for (ax, ay) in ((16, 16), (48, 16), (14, 34), (50, 34)):  # 六边晶格角点
        c.disc(ax, ay, 2, pal["light"])


def sym_era6_gate(c, pal):
    """E6 星门：塔顶展开成环+裂缝。"""
    fg = pal["light"]
    c.ring(32, 24, 10, 7, fg)                              # 星门环
    c.line(32, 34, 32, 46, fg, thick=2)                    # 塔身收束
    c.hline(26, 44, 6, fg)
    c.line(29, 17, 35, 29, pal["light"], thick=1)           # 环上裂缝（斜）
    c.line(35, 17, 31, 27, pal["light"], thick=1)


def sym_shield(c, pal):
    """防线完整（obj-s2）：盾形。"""
    fg = pal["light"]
    c.fill(24, 16, 16, 14, fg)
    c.tri_fill((24, 30), (40, 30), (32, 44), fg)
    c.hline(27, 20, 10, pal["dark"]); c.vline(31, 20, 12, pal["dark"])


def sym_gem(c, pal):
    """采集站零损（obj-s1）：资源菱晶。"""
    fg = pal["light"]
    c.tri_fill((32, 14), (22, 26), (42, 26), fg)
    c.tri_fill((22, 26), (42, 26), (32, 44), fg)
    c.line(32, 14, 27, 26, pal["dark"]); c.line(27, 26, 32, 44, pal["dark"])
    c.set(28, 22, pal["light"])


def sym_coin_slash(c, pal):
    """不出售任何塔（obj-s3）：币面+禁售斜杠。"""
    fg = pal["light"]
    c.disc(32, 30, 11, fg)
    c.ring(32, 30, 7, 6, pal["dark"])
    c.line(22, 40, 42, 20, pal["light"], thick=2)           # 禁售斜杠


def sym_arrow(c, pal):
    """弹药不断供（obj-s4）：满弦箭。"""
    fg = pal["light"]
    c.line(16, 36, 44, 36, fg, thick=2)                    # 箭杆
    c.tri_fill((44, 36), (36, 31), (36, 41), fg)           # 箭头
    c.line(16, 36, 24, 28, pal["dark"]); c.line(16, 36, 24, 44, pal["dark"])  # 弓弦梢
    c.arc(24, 36, 9, -90, 90, pal["light"], thick=1)        # 弓身


def sym_gauge(c, pal):
    """塔效率（obj-s5）：仪表盘+满针。"""
    fg = pal["light"]
    c.arc(32, 36, 12, 180, 360, fg, thick=2)
    c.hline(20, 36, 25, fg)
    c.line(32, 36, 41, 25, pal["light"], thick=2)           # 满档针
    c.disc(32, 36, 2, pal["dark"])


def sym_keep(c, pal):
    """主城零受损（obj-s6）：堡楼+垛口。"""
    fg = pal["light"]
    c.fill(23, 22, 18, 22, fg)
    for xx in (23, 29, 35, 41):
        c.fill(xx, 18, 3, 4, fg)                           # 垛口
    c.fill(29, 28, 6, 8, pal["dark"])                       # 门洞
    c.hline(23, 30, 18, pal["light"])


def sym_scroll(c, pal):
    """首项研究（obj-s7）/首挖残卷：卷轴+微光。"""
    fg = pal["light"]
    c.fill(22, 20, 20, 22, fg)
    c.fill(19, 18, 26, 3, pal["light"])                     # 上卷轴杆
    c.fill(19, 41, 26, 3, pal["light"])                     # 下卷轴杆
    for yy in (26, 31, 36):
        c.hline(25, yy, 14, pal["dark"])                    # 文字行（纯线条）
    c.set(44, 16, pal["light"]); c.set(47, 13, pal["light"])  # 微光星点


def sym_network(c, pal):
    """物流零死锁（obj-s8）：三节点全连通。"""
    fg = pal["light"]
    pts = ((32, 16), (20, 40), (44, 40))
    for (ax, ay) in pts:
        for (bx, by) in pts:
            if (ax, ay) < (bx, by):
                c.line(ax, ay, bx, by, pal["dark"], thick=1)
    for (ax, ay) in pts:
        c.disc(ax, ay, 4, fg)
        c.disc(ax, ay, 1, pal["light"])


def sym_eye(c, pal):
    """系统绑定（觉醒）：目纹（零在注视）。"""
    fg = pal["light"]
    c.line(14, 32, 24, 22, fg, thick=2)
    c.line(24, 22, 40, 22, fg, thick=2)
    c.line(40, 22, 50, 32, fg, thick=2)
    c.line(50, 32, 40, 42, fg, thick=2)
    c.line(40, 42, 24, 42, fg, thick=2)
    c.line(24, 42, 14, 32, fg, thick=2)
    c.disc(32, 32, 6, pal["light"])
    c.disc(32, 32, 2, pal["dark"])


def sym_power_off(c, pal):
    """关闭系统通关（F-097）：断电环+竖杠。"""
    fg = pal["light"]
    c.arc(32, 32, 12, -240, 60, fg, thick=2)               # 缺口圆
    c.line(32, 18, 32, 32, fg, thick=2)
    c.disc(32, 32, 2, pal["light"])


def sym_star_badge(c, pal):
    """军师满级（B-11）：五星徽记。"""
    fg = pal["light"]
    pts_out, pts_in = [], []
    for i in range(5):
        a_out = -90 + i * 72
        a_in = a_out + 36
        pts_out.append((32 + int(round(14 * math.cos(math.radians(a_out)))),
                        32 + int(round(14 * math.sin(math.radians(a_out))))))
        pts_in.append((32 + int(round(6 * math.cos(math.radians(a_in)))),
                       32 + int(round(6 * math.sin(math.radians(a_in))))))
    for i in range(5):
        c.tri_fill(pts_out[i], pts_in[i], pts_in[i - 1], fg)
        c.tri_fill(pts_out[i], pts_out[(i + 1) % 5], pts_in[i], fg)
    c.disc(32, 32, 2, pal["dark"])


def sym_four_marks(c, pal):
    """四人格全解锁（B-12~15）：四象限小面具点。"""
    fg = pal["light"]
    for qx, qy in ((24, 24), (40, 24), (24, 40), (40, 40)):
        c.disc(qx, qy, 5, fg)
        c.fill(qx - 2, qy - 1, 1, 1, pal["dark"])
        c.fill(qx + 2, qy - 1, 1, 1, pal["dark"])           # 双目
        c.hline(qx - 2, qy + 2, 5, pal["dark"])             # 口线
    c.ring(32, 32, 15, 14, pal["light"])


def sym_fragments8(c, pal):
    """集齐 8 段记忆（D 层）：八碎片环。"""
    fg = pal["light"]
    for i in range(8):
        a = i * 45
        x = 32 + int(round(13 * math.cos(math.radians(a))))
        y = 32 + int(round(13 * math.sin(math.radians(a))))
        c.disc(x, y, 2, fg)
    c.ring(32, 32, 6, 4, pal["light"])


def sym_zero(c, pal):
    """《零的真相》（E-16）：零之环+中心点。"""
    fg = pal["light"]
    c.ring(32, 30, 11, 7, fg)
    c.line(26, 44, 38, 44, fg, thick=2)
    c.disc(32, 30, 2, pal["light"])
    for ax, ay in ((18, 18), (46, 18), (18, 42), (46, 42)):
        c.disc(ax, ay, 1, pal["light"])


def sym_wonder(c, pal):
    """首座奇观（F-060）：阶梯金字塔。"""
    fg = pal["light"]
    c.hline(18, 46, 5, fg)
    c.fill(22, 40, 20, 5, fg)
    c.fill(26, 34, 12, 5, fg)
    c.fill(30, 28, 4, 5, fg)
    c.set(32, 24, pal["light"]); c.set(32, 21, pal["light"])  # 尖顶光柱


def sym_beast(c, pal):
    """首杀 Boss（C-01 踏群者）：巨兽头剪影+双角。"""
    fg = pal["light"]
    c.tri_fill((32, 22), (18, 44), (46, 44), fg)
    c.line(22, 26, 16, 14, fg, thick=2)                    # 左角
    c.line(42, 26, 48, 14, fg, thick=2)                    # 右角
    c.set(27, 32, pal["dark"]); c.set(37, 32, pal["dark"])    # 双目
    c.hline(27, 40, 5, pal["dark"])


def sym_endless(c, pal):
    """无尽里程碑（F-123/E-04）：无穷双环+波线。"""
    fg = pal["light"]
    c.ring(25, 28, 6, 4, fg)
    c.ring(39, 28, 6, 4, fg)
    for yy in (40, 43, 46):
        for xx in range(18 - (yy - 40), 46 + (yy - 40)):
            if ((xx // 3) + yy) % 2 == 0:
                c.set(xx, yy, pal["light"])                 # 确定性格点波纹


# ---------------------------------------------------------------- 成就清单
# (文件名, 色环时代, 符号绘制函数, 中文标题, 锚点)
ACHIEVEMENTS = [
    ("achv-era1-clear", "e1", sym_era1_tower, "E1 首通·守望初成", "F-003 通关判定×E1"),
    ("achv-era2-clear", "e2", sym_era2_keep, "E2 首通·青铜回响", "F-003 通关判定×E2"),
    ("achv-era3-clear", "e3", sym_era3_truss, "E3 首通·铆铁之心", "F-003 通关判定×E3"),
    ("achv-era4-clear", "e4", sym_era4_radar, "E4 首通·电网黎明", "F-003 通关判定×E4"),
    ("achv-era5-clear", "e5", sym_era5_spire, "E5 首通·聚变之冠", "F-003 通关判定×E5"),
    ("achv-era6-clear", "e6", sym_era6_gate, "终章首通·星门彼岸", "F-003 通关判定×终章"),
    ("achv-no-station-lost", "e1", sym_gem, "技艺·寸土不失", "obj-s1 StationsLost=0"),
    ("achv-defense-intact", "e2", sym_shield, "技艺·铜墙铁壁", "obj-s2 DefenseIntegrityMin≥40%"),
    ("achv-no-tower-sold", "e3", sym_coin_slash, "技艺·分毫不卖", "obj-s3 TowersSoldCount=0"),
    ("achv-ammo-steady", "e1", sym_arrow, "技艺·箭不绝弦", "obj-s4 AmmoStarvedSeconds≤30"),
    ("achv-efficiency-master", "e3", sym_gauge, "技艺·满效运转", "obj-s5 WaveEndEfficiencyMin≥70%"),
    ("achv-key-untouched", "e3", sym_keep, "技艺·固若金汤", "obj-s6 KeyBuildingsDamagedCount=0"),
    ("achv-first-research", "e2", sym_scroll, "技艺·格物致知", "obj-s7 ResearchCompletedCount≥1"),
    ("achv-logistics-clean", "e2", sym_network, "技艺·脉络畅通", "obj-s8 LogisticsDeadlockCount=0"),
    ("achv-system-bound", "e4", sym_eye, "系统·觉醒之始", "§12.1 系统绑定「叫我零」"),
    ("achv-system-offline", "e4", sym_power_off, "系统·独行守望者", "F-097 关闭系统可全成就"),
    ("achv-advisor-max", "e5", sym_star_badge, "军师·零的荣光", "B-11 军师满级节点"),
    ("achv-persona-all", "e5", sym_four_marks, "军师·四面零声", "B-12~15 四人格解锁"),
    ("achv-scroll-first", "e6", sym_scroll, "收集·拾遗启程", "C-20 首挖知识残卷"),
    ("achv-memory-all", "e5", sym_fragments8, "收集·记忆归源", "D 层 8 段记忆集齐"),
    ("achv-zero-truth", "e6", sym_zero, "收集·零的真相", "E-16 隐藏合成篇解锁"),
    ("achv-wonder-first", "e6", sym_wonder, "收集·奇观时刻", "F-060 首座奇观落成"),
    ("achv-boss-first", "e1", sym_beast, "挑战·巨兽殒落", "C-01/C-05 首杀 Boss"),
    ("achv-endless-milestone", "e4", sym_endless, "挑战·无尽长歌", "F-123 无尽三榜里程碑"),
]


def render(out_dir):
    """渲染 24 枚图标到 out_dir；返回 [(文件名, sha256), ...]（升序）。"""
    manifest = []
    for fname, era, sym, _t, _a in ACHIEVEMENTS:
        c = Canvas(64, 64)
        pal = ERA_PALETTES[era]
        icon_base(c, pal)
        sym(c, pal)
        path = os.path.join(out_dir, fname + ".png")
        c.write(path)
        with open(path, "rb") as fh:
            digest = hashlib.sha256(fh.read()).hexdigest()
        manifest.append((fname + ".png", digest))
    return manifest


# ---------------------------------------------------------------- 自测

def self_test(out_dir):
    """asset_validator 校验 + 临时目录二次生成 sha256 对比（确定性门禁）。"""
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    validator = os.path.join(repo_root, "tools", "ci", "asset_validator.py")
    rc = subprocess.call([sys.executable, validator, out_dir])
    if rc != 0:
        print("[self-test] asset_validator REJECT (exit %d)" % rc)
        return False
    first = dict(render(out_dir))
    with tempfile.TemporaryDirectory() as tmp:
        second = dict(render(os.path.join(tmp, "achievements")))
    mismatches = [k for k in first if first[k] != second.get(k)]
    if mismatches:
        print("[self-test] sha256 mismatch on regeneration: %s" % mismatches)
        return False
    print("[self-test] OK: 24 icons validator-clean + sha256 reproducible")
    return True


def main():
    ap = argparse.ArgumentParser(description="P4 成就图标程序合成器（24 枚 64×64）")
    default_out = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__)))), "game", "assets", "expand-v1", "P4", "achievements")
    ap.add_argument("--out", default=default_out, help="主输出目录（默认 D 盘入库副本）")
    ap.add_argument("--extra", action="append", default=[],
                    help="附加输出目录（如 E 盘归档），可重复")
    ap.add_argument("--self-test", action="store_true", help="校验+确定性自测")
    args = ap.parse_args()

    dirs = [args.out] + list(args.extra)
    listing = None
    for d in dirs:
        listing = render(d)
        print("[render] %d icons -> %s" % (len(listing), d))
    for fname, digest in listing:
        print("  %-34s %s" % (fname, digest[:16]))
    if args.self_test:
        if not self_test(args.out):
            sys.exit(1)


if __name__ == "__main__":
    main()
