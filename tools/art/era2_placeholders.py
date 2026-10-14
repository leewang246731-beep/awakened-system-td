#!/usr/bin/env python3
"""
era2_placeholders.py — E2 占位美术程序合成器（任务 T-078，Wave 3，E 泳道，组件 C-28）

用途：为 E2（古典时代）确定性合成占位像素图——五塔×两级（F-014）、运输马车
（F-042）、E2 面板色板（F-132）与水域 tile（补 Kenney Tiny Town 无水体的空缺；
Medieval RTS 自带水 tile 41~48 实测 78~87 色超 64 上限被校验器拒收）。
背景：CC0 素材源配不齐 E2 五塔×两级的成套规格（14 号台账 A3 Foozle Spire
Tower Pack 为 itch 页面交互下载，脚本管线不可达；Medieval RTS 仅 1 座瞭望塔），
按 08-task-plan T-078 卡片预批准的回退路线走「代码合成像素图」。
红线对齐：这是**程序绘制**（确定性：固定坐标+固定色板，无随机数、无 AI 生图），
与 21 号 §2.1「AI PNG 不进 presentation/游戏 sprite 管线」红线不冲突；产物可由
本脚本逐字节复现，人工重绘升级时以本脚本为底稿。

输出（--out DIR，默认 presentation/EraTD/textures/era2/）：
  towers/tower-arrow-l1.png / -l2.png           32×32 石基+木弓楼
  towers/tower-crossbow-l1.png / -l2.png        32×32 石台+弩机
  towers/tower-repeating-crossbow-l1/l2.png     48×48 双层塔+双联弩+弹匣
  towers/tower-onager-l1.png / -l2.png          48×64 低重台+绞盘弩炮
  towers/tower-fire-oil-l1.png / -l2.png        32×48 石柱+火油釜
  units/carriage-right.png                      32×32 运输马车（F-042）
  ui/panel-era2.png                             32×32 面板色板（古典金/石蓝）
  tiles/water.png                               16×16 水域
  anim/water-anim.png                           32×16 两帧横条（--expect-frames 2 自测）

全部输出须过 tools/ci/asset_validator.py（尺寸 16/32/48/64 白名单、≤64 色、
kebab 命名）；water-anim.png 以 --expect-frames 2 验帧。

依赖：仅 Python 3.8+ 标准库（struct/zlib/argparse）——与 asset_validator 同纪律，
不引 Pillow。CLI：python era2_placeholders.py [--out DIR] [--self-test]
--self-test：生成后即调 asset_validator 校验自身输出（含帧数门禁），任一
REJECT 退出非 0（run_all Step 1.5 亦覆盖该输出目录）。
"""

from __future__ import annotations

import argparse
import os
import struct
import sys
import zlib

# ---------------------------------------------------------------- E2 古典色板
# 22 色（≤64 master 上限）：石 4 / 木 3 / 铁 3 / 金 3 / 蓝石 3 / 火 3 / 水 3
PALETTE = {
    "stone_light": (201, 196, 185),
    "stone_mid": (163, 155, 142),
    "stone_dark": (119, 111, 99),
    "stone_shadow": (82, 76, 66),
    "wood_light": (166, 124, 82),
    "wood_mid": (124, 88, 56),
    "wood_dark": (86, 60, 38),
    "iron_light": (151, 157, 165),
    "iron_mid": (107, 113, 123),
    "iron_dark": (70, 74, 84),
    "gold_light": (233, 194, 105),
    "gold_mid": (197, 153, 67),
    "gold_dark": (141, 105, 41),
    "blue_light": (129, 153, 173),
    "blue_mid": (93, 115, 139),
    "blue_dark": (62, 80, 102),
    "fire_light": (241, 161, 71),
    "fire_mid": (213, 109, 45),
    "fire_dark": (161, 61, 29),
    "water_light": (91, 141, 191),
    "water_mid": (59, 105, 161),
    "water_dark": (39, 75, 125),
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

def stone_base(c, x, y, w, h, level=1):
    """石砌基座：亮左缘/暗右缘 + 每 4 行砖缝（错缝竖点）+ 底影；level2 金箍。"""
    c.fill(x, y, w, h, PALETTE["stone_mid"])
    c.vline(x, y, h, PALETTE["stone_light"])
    c.vline(x + w - 1, y, h, PALETTE["stone_dark"])
    for i in range(4, h, 4):
        c.hline(x, y + i, w, PALETTE["stone_dark"])
        if (i // 4) % 2:
            c.set(x + 2, y + i - 1, PALETTE["stone_shadow"])
    c.hline(x, y + h - 1, w, PALETTE["stone_shadow"])
    if level >= 2:
        c.hline(x, y + 2, w, PALETTE["gold_mid"])


def wood_platform(c, x, y, w, level=1):
    """木射击平台：亮顶/暗底；level2 金压条。"""
    c.fill(x, y, w, 3, PALETTE["wood_mid"])
    c.hline(x, y, w, PALETTE["wood_light"])
    c.hline(x, y + 2, w, PALETTE["wood_dark"])
    if level >= 2:
        c.hline(x, y - 1, w, PALETTE["gold_light"])


def crenellation(c, x, y, w, color):
    """垛口：4px 周期齿块。"""
    xx = x
    while xx < x + w - 1:
        c.fill(xx, y, 2, 2, color)
        xx += 4


def arrow_slit(c, x, y):
    """箭窗：1×4 竖缝。"""
    c.fill(x, y, 1, 4, PALETTE["stone_shadow"])


def bow_sidearm(c, cx, cy, level=1):
    """侧挂弓（箭楼辨识标记）：竖弓身+弦梢。"""
    c.fill(cx, cy - 2, 1, 5, PALETTE["wood_light"])
    c.set(cx - 1, cy - 3, PALETTE["wood_mid"])
    c.set(cx - 1, cy + 3, PALETTE["wood_mid"])
    tip = PALETTE["gold_mid"] if level >= 2 else PALETTE["wood_dark"]
    c.set(cx + 1, cy - 3, tip)
    c.set(cx + 1, cy + 3, tip)


def crossbow_top(c, cx, cy, level=1):
    """顶置弩机：铁弩身+木弓片+箭槽；level2 金准星。"""
    c.fill(cx - 3, cy, 7, 2, PALETTE["iron_mid"])
    c.fill(cx - 1, cy - 3, 1, 3, PALETTE["iron_mid"])
    c.set(cx - 1, cy - 4, PALETTE["iron_dark"])
    c.set(cx - 5, cy - 1, PALETTE["wood_light"])
    c.set(cx - 4, cy - 2, PALETTE["wood_light"])
    c.set(cx + 5, cy - 1, PALETTE["wood_light"])
    c.set(cx + 4, cy - 2, PALETTE["wood_light"])
    c.hline(cx - 3, cy - 2, 7, PALETTE["wood_mid"])
    if level >= 2:
        c.set(cx, cy - 5, PALETTE["gold_light"])
        c.set(cx + 2, cy + 2, PALETTE["gold_mid"])


# ---------------------------------------------------------------- 五塔（F-014）

def tower_arrow(level):
    """箭楼（era2:tower-arrow）32×32：石基+木塔身+射击平台+垛口+侧弓。"""
    c = Canvas(32, 32)
    stone_base(c, 8, 18, 16, 14, level)
    arrow_slit(c, 12, 22)
    arrow_slit(c, 19, 22)
    c.fill(10, 6, 12, 12, PALETTE["wood_mid"])
    c.vline(10, 6, 12, PALETTE["wood_light"])
    c.vline(21, 6, 12, PALETTE["wood_dark"])
    c.hline(11, 10, 10, PALETTE["wood_dark"])
    c.hline(11, 14, 10, PALETTE["wood_dark"])
    arrow_slit(c, 15, 8)
    wood_platform(c, 9, 4, 14, level)
    crenellation(c, 10, 1, 12, PALETTE["wood_light"])
    bow_sidearm(c, 25, 24, level)
    if level >= 2:
        c.fill(15, 0, 1, 4, PALETTE["gold_dark"])  # 旗杆
        c.fill(16, 0, 4, 3, PALETTE["gold_light"])  # 金旗
    return c


def tower_crossbow(level):
    """弩塔（era2:tower-crossbow）32×32：宽石台+顶置弩机。"""
    c = Canvas(32, 32)
    stone_base(c, 5, 20, 22, 12, level)
    arrow_slit(c, 10, 24)
    arrow_slit(c, 21, 24)
    wood_platform(c, 4, 17, 24, level)
    crenellation(c, 5, 14, 22, PALETTE["stone_light"])
    crossbow_top(c, 16, 10, level)
    return c


def tower_repeating_crossbow(level):
    """连弩塔（era2:tower-repeating-crossbow）48×48：双层石塔+双联弩+木弹匣。"""
    c = Canvas(48, 48)
    stone_base(c, 10, 26, 28, 22, level)
    stone_base(c, 14, 12, 20, 14, level)
    arrow_slit(c, 18, 31)
    arrow_slit(c, 29, 31)
    arrow_slit(c, 22, 16)
    wood_platform(c, 13, 9, 22, level)
    crenellation(c, 14, 6, 20, PALETTE["stone_light"])
    crossbow_top(c, 18, 2, level)
    crossbow_top(c, 30, 2, level)
    c.rect(21, 24, 6, 4, PALETTE["wood_mid"],
           PALETTE["gold_dark"] if level >= 2 else PALETTE["wood_dark"])
    if level >= 2:
        c.fill(23, 0, 2, 3, PALETTE["gold_mid"])
    return c


def tower_onager(level):
    """弩炮（era2:tower-onager）48×64：低重石台+绞盘+粗发射臂+环形索。"""
    c = Canvas(48, 64)
    stone_base(c, 6, 40, 36, 24, level)
    wood_platform(c, 4, 36, 40, level)
    c.fill(18, 20, 12, 16, PALETTE["wood_mid"])  # 炮架
    c.vline(18, 20, 16, PALETTE["wood_light"])
    c.vline(29, 20, 16, PALETTE["wood_dark"])
    c.fill(22, 8, 4, 12, PALETTE["wood_light"])  # 发射臂
    c.fill(22, 8, 4, 2, PALETTE["iron_mid"])  # 臂顶铁箍
    for (xx, yy) in ((14, 30), (15, 29), (15, 31), (33, 30), (32, 29), (32, 31)):
        c.set(xx, yy, PALETTE["iron_dark"])  # 环形索
    c.fill(8, 44, 4, 4, PALETTE["iron_mid"])  # 绞盘
    c.fill(36, 44, 4, 4, PALETTE["iron_mid"])
    if level >= 2:
        c.hline(6, 38, 36, PALETTE["gold_mid"])
        c.set(24, 6, PALETTE["gold_light"])
    return c


def tower_fire_oil(level):
    """火油塔（era2:tower-fire-oil）32×48：石柱+铁支架+火油釜+火焰。"""
    c = Canvas(32, 48)
    stone_base(c, 10, 22, 12, 26, level)
    arrow_slit(c, 14, 28)
    arrow_slit(c, 17, 34)
    c.fill(8, 18, 16, 4, PALETTE["iron_dark"])  # 支架
    c.fill(9, 10, 14, 8, PALETTE["iron_mid"])  # 釜
    c.hline(9, 10, 14, PALETTE["iron_light"])
    c.hline(9, 17, 14, PALETTE["iron_dark"])
    c.hline(11, 11, 10, PALETTE["fire_dark"])  # 油面
    for (xx, yy, key) in ((13, 8, "fire_light"), (14, 7, "fire_light"),
                          (15, 8, "fire_mid"), (16, 7, "fire_light"),
                          (17, 8, "fire_mid"), (15, 5, "fire_light"),
                          (14, 9, "fire_mid"), (16, 9, "fire_dark")):
        c.set(xx, yy, PALETTE[key])
    if level >= 2:
        c.hline(9, 9, 14, PALETTE["gold_mid"])
        c.set(12, 4, PALETTE["fire_light"])
        c.set(18, 4, PALETTE["fire_light"])
    return c


# ---------------------------------------------------------------- 载具（F-042）

def carriage_right():
    """运输马车 32×32（朝右）：挽马+双轮栏板车+货物。"""
    c = Canvas(32, 32)
    c.fill(2, 12, 12, 7, PALETTE["wood_mid"])  # 马体
    c.fill(13, 10, 4, 5, PALETTE["wood_mid"])  # 颈
    c.fill(15, 9, 3, 3, PALETTE["wood_dark"])  # 头
    c.set(17, 10, PALETTE["stone_shadow"])  # 眼
    c.hline(13, 9, 4, PALETTE["stone_shadow"])  # 鬃
    for lx in (3, 6, 9, 11):
        c.fill(lx, 19, 2, 5, PALETTE["wood_dark"])  # 腿
    c.fill(3, 23, 3, 2, PALETTE["stone_shadow"])  # 蹄
    c.fill(11, 23, 3, 2, PALETTE["stone_shadow"])
    c.fill(1, 13, 1, 5, PALETTE["stone_shadow"])  # 尾
    c.fill(18, 12, 12, 8, PALETTE["wood_mid"])  # 车厢
    c.hline(18, 12, 12, PALETTE["wood_light"])
    c.hline(18, 19, 12, PALETTE["wood_dark"])
    c.vline(18, 12, 8, PALETTE["wood_light"])
    c.vline(29, 12, 8, PALETTE["wood_dark"])
    c.fill(20, 9, 8, 3, PALETTE["stone_mid"])  # 货物
    c.set(21, 8, PALETTE["gold_mid"])
    c.set(25, 8, PALETTE["gold_mid"])
    for wx in (21, 27):  # 车轮+辐条
        c.fill(wx - 2, 21, 5, 5, PALETTE["wood_dark"])
        c.set(wx, 23, PALETTE["stone_dark"])
        c.set(wx - 2, 23, PALETTE["stone_dark"])
        c.set(wx + 2, 23, PALETTE["stone_dark"])
    c.hline(14, 15, 5, PALETTE["wood_dark"])  # 辕杆
    return c


# ---------------------------------------------------------------- UI 色板（F-132）

def panel_palette():
    """E2 面板色板 32×32：金描边+石蓝底+4×4 色块矩阵（金/蓝/石/木四行），
    供 C-22 面板 E2 换肤取色（色板底稿，非成品 UI 图）。"""
    c = Canvas(32, 32)
    c.fill(0, 0, 32, 32, PALETTE["blue_dark"])
    c.rect(0, 0, 32, 32, None, PALETTE["gold_mid"])
    c.rect(2, 2, 28, 28, None, PALETTE["gold_dark"])
    c.fill(4, 4, 24, 24, PALETTE["blue_mid"])
    rows = (
        ("gold_light", "gold_mid", "gold_dark", "fire_mid"),
        ("blue_light", "blue_mid", "blue_dark", "iron_dark"),
        ("stone_light", "stone_mid", "stone_dark", "iron_mid"),
        ("wood_light", "wood_mid", "wood_dark", "stone_shadow"),
    )
    for ry, row in enumerate(rows):
        for rx, key in enumerate(row):
            c.fill(5 + rx * 6, 5 + ry * 6, 5, 5, PALETTE[key])
    return c


# ---------------------------------------------------------------- 水域 tile

def water_tile(phase=0):
    """16×16 水面：三蓝斜波纹（周期 8）+固定高光点；phase 供两帧动画错相。"""
    c = Canvas(16, 16)
    for y in range(16):
        for x in range(16):
            band = (x + y + phase) % 8
            key = "water_dark" if band in (0, 1) else ("water_light" if band == 4 else "water_mid")
            c.set(x, y, PALETTE[key])
    for (x, y) in ((2, 3), (10, 7), (6, 12), (13, 1)):
        c.set(x, y, PALETTE["water_light"])
    return c


def water_anim_strip():
    """32×16 两帧横条（validator frames_hint=2，帧数门禁对象）。"""
    strip = Canvas(32, 16)
    strip.blit(water_tile(0), 0, 0)
    strip.blit(water_tile(4), 16, 0)
    return strip


# ---------------------------------------------------------------- 驱动

TOWERS = {
    "tower-arrow": tower_arrow,
    "tower-crossbow": tower_crossbow,
    "tower-repeating-crossbow": tower_repeating_crossbow,
    "tower-onager": tower_onager,
    "tower-fire-oil": tower_fire_oil,
}


def build_all(out_root):
    wrote = []
    for name, fn in sorted(TOWERS.items()):
        for level in (1, 2):
            path = os.path.join(out_root, "towers", f"{name}-l{level}.png")
            fn(level).write(path)
            wrote.append(path)
    units = os.path.join(out_root, "units", "carriage-right.png")
    carriage_right().write(units)
    wrote.append(units)
    ui = os.path.join(out_root, "ui", "panel-era2.png")
    panel_palette().write(ui)
    wrote.append(ui)
    water = os.path.join(out_root, "tiles", "water.png")
    water_tile(0).write(water)
    wrote.append(water)
    anim = os.path.join(out_root, "anim", "water-anim.png")
    water_anim_strip().write(anim)
    wrote.append(anim)
    return wrote


def run_self_test(out_root):
    """调 asset_validator 校验输出：towers/units/ui/tiles 常规门禁；
    anim/ 目录以 expect_frames=2 过帧数门禁（帧数门禁为目录级，横条独立成目录）。
    返回 (static_fails, frames_ok, 静态报告, 帧报告)。"""
    here = os.path.dirname(os.path.abspath(__file__))
    ci_dir = os.path.normpath(os.path.join(here, "..", "ci"))
    sys.path.insert(0, ci_dir)
    import asset_validator  # noqa: E402（同仓库零依赖校验器）

    static_dirs = [os.path.join(out_root, d) for d in ("towers", "units", "ui", "tiles")]
    static_report = asset_validator.run(
        static_dirs, asset_validator.DEFAULT_ALLOWED_SIZES,
        asset_validator.DEFAULT_MAX_PALETTE, False)
    fails = [r for r in static_report["results"]
             if any(f["severity"] == "REJECT" for f in r["findings"])]

    frames_report = asset_validator.run(
        [os.path.join(out_root, "anim")], asset_validator.DEFAULT_ALLOWED_SIZES,
        asset_validator.DEFAULT_MAX_PALETTE, False, expect_frames=2)
    norm = lambda p: p.replace(os.sep, "/")  # noqa: E731
    anim = next((r for r in frames_report["results"]
                 if norm(r["path"]).endswith("water-anim.png")), None)
    frames_ok = (anim is not None and anim["frames"] == 2
                 and not any(f["severity"] == "REJECT" for f in anim["findings"]))
    for r in frames_report["results"]:  # anim 目录内任何 REJECT 都判失败
        if any(f["severity"] == "REJECT" for f in r["findings"]):
            frames_ok = False
    return fails, frames_ok, static_report, frames_report


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="E2 占位美术程序合成器（T-078）：五塔/马车/UI 色板/水域")
    parser.add_argument("--out", default=None,
                        help="输出根目录（默认=presentation/EraTD/textures/era2）")
    parser.add_argument("--self-test", action="store_true",
                        help="生成后调 asset_validator 自校验（REJECT 即非 0 退出）")
    args = parser.parse_args(argv)

    if args.out:
        out_root = os.path.normpath(args.out)
    else:
        out_root = os.path.normpath(os.path.join(
            os.path.dirname(os.path.abspath(__file__)),
            "..", "..", "game", "presentation", "EraTD", "textures", "era2"))

    for p in build_all(out_root):
        print(f"wrote {p}")

    if args.self_test:
        fails, frames_ok, static_report, frames_report = run_self_test(out_root)
        for r in static_report["results"] + frames_report["results"]:
            for f in r["findings"]:
                if f["severity"] != "INFO":
                    print(f"  {f['severity']} {r['path']}: {f['message']}")
        if fails or not frames_ok:
            print(f"self-test FAIL: static_rejects={len(fails)} frames_ok={frames_ok}")
            return 1
        print(f"self-test ok: static_files={static_report['files']} rejected=0 "
              f"anim_files={frames_report['files']} water-anim frames=2/expect-2")
    return 0


if __name__ == "__main__":
    sys.exit(main())
