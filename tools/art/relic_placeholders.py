#!/usr/bin/env python3
"""
relic_placeholders.py — 跨代遗迹标记占位合成器（任务 T-125，Wave 7，C 泳道，
组件 C-20/C-28；28 号冻结清单 v1.1 §2.4）

用途：为遗迹映射正式版（F-057/F-168 产品化）的新时代地图标记面确定性合成
占位像素图——立碑遗迹（standing relic）/考古挖掘位（dig site，降级废墟的
地图实体）/纪念馆修复位（memorial repair slot）/核心保留区勾勒 tile（A5
reserve outline，RelicMarkerBoard.ReserveOutline 消费）。纹理 stem 与
EraCore.Presentation/RelicMarkerBoard.cs 常量逐一对应（跨层契约，
RelicArtMappingTests 实锚）。

背景：跨代遗迹标记是 era 无关域（遗迹本体=上一代建筑，标记=新一代地图 UI
面），不属于任何单代美术包（28 号 §2.1/§2.2 时代子域、41 号 era3/ 树）——
E4 美术包（T-128）未产，按 28 号 §6 待做 #8 口径与本任务卡「美术未产占位
合成」指示先行占位合成；人工重绘升级时以本脚本为底稿（28 号 §6 声明同律）。

红线对齐：**程序绘制**（确定性：固定坐标+固定 12 色色板，无随机数、无 AI
生图），与 21 号 §2.1「AI PNG 不进 presentation/游戏 sprite 管线」红线不
冲突；产物可由本脚本逐字节复现。

视觉身份（31 号 §1.2 时代行之外的跨代域裁定）：风化石灰岩三阶（浅/中/影）
+ 青铜铭刻两阶 + 苔藓两点缀 + 勘测旗红——遗迹=「上一代的残骸」故走石/铜
而非任何单代主题色；alpha：保留区勾勒 tile 用 192 半透明（叠加在地形上的
UI 覆盖层），三个实体标记全不透明。

输出（--out DIR，默认 presentation/EraTD/textures/relic/）：
  relic-standing.png    32×32 断柱纪念碑（立碑遗迹：回放原位/移位）
  relic-dig-site.png    32×32 碎石堆+勘测旗（考古挖掘位：降级废墟锚定）
  relic-memorial.png    32×32 纪念碑修复位（未消费的修复槽）
  relic-reserve.png     16×16 核心保留区勾勒 tile（空心边框+角饰，半透明）

全部输出须过 tools/ci/asset_validator.py（尺寸 16/32/48/64 白名单、≤64 色、
kebab 命名）。依赖：仅 Python 3.8+ 标准库（struct/zlib/argparse）。
CLI：python relic_placeholders.py [--out DIR] [--self-test]
"""

from __future__ import annotations

import argparse
import os
import struct
import sys
import zlib

# ------------------------------------------------------- 跨代遗迹色板（12 色）
# 风化石灰岩 3 + 青铜铭刻 2 + 苔藓 2 + 勘测旗 2 + 基座阴影 2 + 勾勒金 1
PALETTE = {
    "limestone_light": (212, 204, 186),
    "limestone_mid": (178, 168, 148),
    "limestone_shadow": (128, 118, 100),
    "bronze_light": (196, 156, 84),
    "bronze_dark": (140, 104, 48),
    "moss_light": (110, 140, 74),
    "moss_dark": (72, 100, 48),
    "flag_red": (168, 60, 48),
    "flag_shadow": (120, 38, 30),
    "base_shadow": (86, 78, 64),
    "ground_dark": (58, 52, 42),
    "outline_gold": (222, 184, 92),
}


class Canvas:
    """最小像素画布：RGBA 网格，初始全透明 (0,0,0,0)。"""

    def __init__(self, width, height):
        self.width = width
        self.height = height
        self.px = [[(0, 0, 0, 0)] * width for _ in range(height)]

    def set(self, x, y, color, alpha=255):
        if 0 <= x < self.width and 0 <= y < self.height:
            self.px[y][x] = (color[0], color[1], color[2], alpha)

    def fill(self, x, y, w, h, color, alpha=255):
        for yy in range(y, y + h):
            for xx in range(x, x + w):
                self.set(xx, yy, color, alpha)

    def hline(self, x, y, w, color, alpha=255):
        for xx in range(x, x + w):
            self.set(xx, y, color, alpha)

    def vline(self, x, y, h, color, alpha=255):
        for yy in range(y, y + h):
            self.set(x, yy, color, alpha)

    def write(self, path):
        raw = bytearray()
        for row in self.px:
            raw.append(0)  # PNG 行滤波 type 0（合成器写出契约）
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


def ground_shadow(c, x, y, w):
    """石件落影一行（贴地暗色）。"""
    c.hline(x, y, w, PALETTE["ground_dark"])


# ---------------------------------------------------------------- 立碑遗迹 32×32

def build_relic_standing():
    """断柱纪念碑：三段基座+柱身+残断柱口+青铜铭带+苔藓点缀。"""
    c = Canvas(32, 32)
    ls, lm, lk = (PALETTE["limestone_light"], PALETTE["limestone_mid"],
                  PALETTE["limestone_shadow"])
    bl, bd = PALETTE["bronze_light"], PALETTE["bronze_dark"]
    mo = PALETTE["moss_light"]

    # 三段基座（下宽上窄）
    c.fill(6, 27, 20, 3, lm)                     # 底层基座
    c.hline(6, 27, 20, lk)
    c.fill(8, 24, 16, 2, ls)                     # 中层基座
    c.hline(8, 25, 16, lm)
    c.fill(10, 22, 12, 2, lm)                    # 顶层基座
    ground_shadow(c, 5, 30, 22)

    # 柱身（竖槽纹理：light 主体 + shadow 竖线两道）
    c.fill(12, 7, 8, 15, ls)
    c.vline(12, 7, 15, lk)
    c.vline(19, 7, 15, lk)
    c.vline(14, 8, 13, lm)
    c.vline(17, 8, 13, lm)

    # 残断柱口（锯齿：奇偶列高低差）
    for i, x in enumerate(range(12, 20)):
        c.hline(x, 5 if i % 2 == 0 else 6, 1, ls if i % 2 == 0 else lm)
    c.hline(12, 4, 8, lm)                        # 断口顶沿

    # 青铜铭带（柱身中部两行 + 阴影沿）
    c.fill(12, 14, 8, 2, bl)
    c.hline(12, 16, 8, bd)

    # 苔藓点缀（基座两簇）
    c.set(9, 24, mo)
    c.set(10, 23, mo)
    c.set(23, 25, mo)
    c.set(22, 26, PALETTE["moss_dark"])
    return c


# ---------------------------------------------------------------- 挖掘位 32×32

def build_relic_dig_site():
    """碎石堆+勘测旗：降级废墟的考古现场（堆体石灰岩碎块+旗杆红旗）。"""
    c = Canvas(32, 32)
    ls, lm, lk = (PALETTE["limestone_light"], PALETTE["limestone_mid"],
                  PALETTE["limestone_shadow"])

    # 碎石堆（三层穹顶：下宽上窄，块间阴影缝）
    c.fill(8, 22, 16, 4, lm)
    c.fill(10, 18, 12, 4, ls)
    c.fill(13, 15, 6, 3, lm)
    c.hline(8, 22, 16, lk)                       # 堆底阴影沿
    c.vline(8, 22, 4, lk)
    c.vline(23, 22, 4, lk)
    # 碎块缝（阴影短划）
    for (x, y) in ((11, 20), (15, 19), (18, 21), (13, 24), (19, 24), (10, 25)):
        c.set(x, y, lk)
    # 散落碎石（堆外三颗）
    c.fill(5, 26, 2, 2, lm)
    c.fill(25, 27, 2, 2, lk)
    c.set(3, 24, lm)
    ground_shadow(c, 7, 30, 20)

    # 勘测旗：旗杆（堆右）+三角旗
    c.vline(25, 8, 14, PALETTE["base_shadow"])
    c.fill(26, 8, 4, 3, PALETTE["flag_red"])
    c.set(30, 10, PALETTE["flag_shadow"])
    c.set(30, 11, PALETTE["flag_shadow"])
    c.set(25, 7, PALETTE["flag_shadow"])          # 杆顶帽
    return c


# ---------------------------------------------------------------- 修复位 32×32

def build_relic_memorial():
    """纪念碑修复位：完整尖顶碑+凹铭牌（与断柱对照=修复后的形态）。"""
    c = Canvas(32, 32)
    ls, lm, lk = (PALETTE["limestone_light"], PALETTE["limestone_mid"],
                  PALETTE["limestone_shadow"])
    bl, bd = PALETTE["bronze_light"], PALETTE["bronze_dark"]

    # 两段基座
    c.fill(7, 27, 18, 3, lm)
    c.hline(7, 27, 18, lk)
    c.fill(9, 25, 14, 2, ls)
    ground_shadow(c, 6, 30, 20)

    # 碑身（上尖下方：两段梯形近似）
    c.fill(11, 8, 10, 17, ls)
    c.fill(13, 5, 6, 3, lm)
    c.fill(14, 3, 4, 2, ls)                       # 尖顶
    c.vline(11, 8, 17, lk)
    c.vline(20, 8, 17, lk)

    # 凹铭牌（青铜框+阴影内衬）
    c.fill(13, 12, 6, 7, lk)
    c.fill(14, 13, 4, 5, bd)
    c.fill(14, 13, 4, 1, bl)                      # 铭牌顶光沿

    # 基座铭带
    c.hline(9, 26, 14, bl)
    c.hline(9, 24, 14, lm)

    # 苔藓一点（右下基座）
    c.set(22, 27, PALETTE["moss_light"])
    return c


# ------------------------------------------------------- 保留区勾勒 tile 16×16

def build_relic_reserve():
    """核心保留区勾勒 tile：空心边框+四角饰（alpha 192=叠加覆盖层）。"""
    c = Canvas(16, 16)
    a = 192
    gold = PALETTE["outline_gold"]
    # 空心边框（上下左右各 1px）
    c.hline(1, 1, 14, gold, a)
    c.hline(1, 14, 14, gold, a)
    c.vline(1, 1, 14, gold, a)
    c.vline(14, 1, 14, gold, a)
    # 四角饰（2×2 实心加重）
    c.fill(1, 1, 2, 2, gold, 255)
    c.fill(13, 1, 2, 2, gold, 255)
    c.fill(1, 13, 2, 2, gold, 255)
    c.fill(13, 13, 2, 2, gold, 255)
    # 边中点饰（四向短刺）
    c.hline(7, 2, 2, gold, a)
    c.hline(7, 12, 2, gold, a)
    c.vline(2, 7, 2, gold, a)
    c.vline(12, 7, 2, gold, a)
    return c


# ---------------------------------------------------------------- 装载与自测

def build_all(out_root):
    files = [
        ("relic-standing", build_relic_standing, 32, 32),
        ("relic-dig-site", build_relic_dig_site, 32, 32),
        ("relic-memorial", build_relic_memorial, 32, 32),
        ("relic-reserve", build_relic_reserve, 16, 16),
    ]
    paths = []
    for stem, build, w, h in files:
        canvas = build()
        assert canvas.width == w and canvas.height == h, stem
        path = os.path.join(out_root, stem + ".png")
        canvas.write(path)
        paths.append(path)
    return paths


def run_self_test(out_root):
    here = os.path.dirname(os.path.abspath(__file__))
    ci_dir = os.path.normpath(os.path.join(here, "..", "ci"))
    sys.path.insert(0, ci_dir)
    import asset_validator  # noqa: E402（同仓库零依赖校验器）
    report = asset_validator.run(
        [out_root], asset_validator.DEFAULT_ALLOWED_SIZES,
        asset_validator.DEFAULT_MAX_PALETTE, False)
    fails = [r for r in report["results"]
             if any(f["severity"] == "REJECT" for f in r["findings"])]
    return fails, report


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="跨代遗迹标记占位合成器（T-125）：立碑遗迹/挖掘位/修复位/保留区勾勒")
    parser.add_argument("--out", default=None,
                        help="输出目录（默认=presentation/EraTD/textures/relic）")
    parser.add_argument("--self-test", action="store_true",
                        help="生成后调 asset_validator 自校验（REJECT 即非 0 退出）")
    args = parser.parse_args(argv)

    if args.out:
        out_root = os.path.normpath(args.out)
    else:
        out_root = os.path.normpath(os.path.join(
            os.path.dirname(os.path.abspath(__file__)),
            "..", "..", "game", "presentation", "EraTD", "textures", "relic"))

    for p in build_all(out_root):
        print(f"wrote {p}")

    if args.self_test:
        fails, report = run_self_test(out_root)
        for r in report["results"]:
            for f in r["findings"]:
                if f["severity"] != "INFO":
                    print(f"  {f['severity']} {r['path']}: {f['message']}")
        if fails:
            print(f"self-test FAIL: rejects={len(fails)}")
            return 1
        print(f"self-test ok: static_files={report['files']} rejected=0")
    return 0


if __name__ == "__main__":
    sys.exit(main())
