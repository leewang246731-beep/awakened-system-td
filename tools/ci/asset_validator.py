#!/usr/bin/env python3
"""
asset_validator.py — 资产导入校验器（任务 T-046，Wave 1，组件 C-28，泳道 E）

单文件、零三方依赖（Python 3.8+，仅标准库 struct/zlib/json）。扫描目录中的
PNG 资产并按《14-art-license-audit.md》§3 像素规格落地参数执行入库校验；
Kenney Tiny Dungeon 为 16×16 CC0 tile 包（14 §1.1 A1），进 32px 网格须整数
2× 放大——首版允许清单因此取 16 的整数倍尺寸 16/32/48/64。

规则（severity: REJECT=拒绝入库 / WARN=放行但报告 / INFO=仅报告）：
  PNG-STRUCTURE  REJECT  PNG 签名/IHDR/分块 CRC 损坏（含截断文件）
  SIZE-ALLOWLIST REJECT  宽或高不在允许清单 {16,32,48,64}（首版=Kenney
                        Tiny Dungeon 16×16 及其整数放大；"非 2 的幂或不在
                        允许清单"的尺寸拒绝入库——48 为清单内显式允许的
                        非 2 幂例外，清单本身即判据）
  FILENAME-KEBAB REJECT  文件名非 kebab-case 小写（^[a-z0-9]+(-[a-z0-9]+)*\\.png$）
  PALETTE-COUNT  REJECT  实际使用颜色数 > master 调色板上限 64 色
                        （14 §3「master ≤64 色」；Wave 1 round 1 review #6
                        起默认拒绝入库；`--allow-oversized-palette` 逃生阀
                        供美术特批后降为 WARN 放行——审批留痕走审查记录）
  FRAME-STRIP    INFO    横条 sheet（宽=高×整数）自动报告帧数=w/h 供观察，
                        首版不下判定（自动帧门禁随 sheet 结构规格在 T-045
                        接入）；显式给定 `--expect-frames N` 时按声明值判定：
                        探测帧数≠N（含无法给出帧数的非横条图）→ REJECT

实现说明：
  - 像素尺寸与颜色统计直接解析 PNG 二进制：IHDR 头（宽/高/位深/颜色类型/
    隔行标志）+ 分块 CRC 校验（zlib.crc32）+ IDAT zlib 解压 + 逐行反滤波
    （Filter 0~4，含 Paeth 预测器）——不依赖 Pillow；
  - 支持位深 1/2/4/8/16 与颜色类型 0（灰）/2（RGB）/3（索引）/4（灰+α）/6（RGBA），
    16 位取高字节计数；tRNS 透明度计入 distinct 颜色；Adam7 隔行图跳过
    颜色统计（INFO 注明），尺寸校验不受影响；
  - 调色板统计口径=图内实际使用的 distinct (R,G,B,A) 颜色数（14 §3「master
    ≤64 色」的入库口径）。

CLI 语义（自测见 test_asset_validator.py）：
  python asset_validator.py [--json REPORT] [--allow-oversized-palette]
                            [--expect-frames N] DIR [DIR ...]
  退出码：0=无 REJECT（WARN 放行）；1=存在 REJECT；2=参数/IO 错误。
  --json 同时写机器报告（files/rejected/warned/results/exit_code）。

run_all.sh 接入点（T-045 起生效，G2.5 门禁）：tools/ci/run_all.sh Step 1.5 调
  python tools/ci/asset_validator.py --json <log>/assets.json \
      game/assets game/presentation/EraTD/textures
（REJECT=run_all 红；含 game/assets 资产库根——新包入库即被门禁覆盖。）
规范依据：docs/engineering/14-art-license-audit.md §3；08-task-plan.md T-045/T-046。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import struct
import sys
import zlib

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

# 14 §3 画布/Tile 规格的首版允许清单：16×16 源 tile 的 1×/2×/3×/4× 整数放大
DEFAULT_ALLOWED_SIZES = (16, 32, 48, 64)

# 14 §3「调色板 master ≤64 色」
DEFAULT_MAX_PALETTE = 64

KEBAB_RE_OK = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*\.png$")

# PNG 颜色类型 → 每像素通道数（位深按通道计）
COLOR_TYPE_CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}


# ---------------------------------------------------------------- PNG 解析


class PngError(ValueError):
    """PNG 结构损坏（REJECT 级）。"""


def parse_png(data: bytes) -> dict:
    """解析 PNG 字节流：宽/高/位深/颜色类型/隔行/PLTE/tRNS/像素颜色集合。

    返回 dict：width,height,bit_depth,color_type,interlaced,palette_entries,
    colors（set[(r,g,b,a)]；隔行图为 None）、frames_hint（宽=高×整数时 w//h）。
    任何结构损坏抛 PngError。
    """
    if len(data) < 8 or data[:8] != PNG_SIGNATURE:
        raise PngError("PNG 签名缺失（非 PNG 或已截断）")

    pos = 8
    ihdr = None
    plte = None  # bytes，每 3 字节一色
    trns = None  # bytes，索引透明度
    idat = bytearray()
    seen_iend = False
    while pos < len(data):
        if pos + 8 > len(data):
            raise PngError("分块头截断")
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        ctype = data[pos + 4:pos + 8]
        cstart = pos + 8
        cend = cstart + length
        if cend + 4 > len(data):
            raise PngError(f"分块 {ctype.decode('latin-1')} 数据截断")
        chunk = data[cstart:cend]
        (crc_stored,) = struct.unpack(">I", data[cend:cend + 4])
        if zlib.crc32(ctype + chunk) & 0xFFFFFFFF != crc_stored:
            raise PngError(f"分块 {ctype.decode('latin-1')} CRC 校验失败")
        if ctype == b"IHDR":
            if length != 13:
                raise PngError("IHDR 长度非法")
            w, h, depth, color, _comp, _filt, interlace = struct.unpack(">IIBBBBB", chunk)
            ihdr = (w, h, depth, color, interlace)
        elif ctype == b"PLTE":
            plte = chunk
        elif ctype == b"tRNS":
            trns = chunk
        elif ctype == b"IDAT":
            idat.extend(chunk)
        elif ctype == b"IEND":
            seen_iend = True
            pos = cend + 4
            break
        pos = cend + 4

    if ihdr is None:
        raise PngError("缺 IHDR 分块")
    if not seen_iend:
        raise PngError("缺 IEND 分块（文件截断）")
    width, height, bit_depth, color_type, interlaced = ihdr
    if width == 0 or height == 0:
        raise PngError("宽或高为 0")
    if color_type not in COLOR_TYPE_CHANNELS:
        raise PngError(f"非法颜色类型 {color_type}")
    if color_type == 3 and bit_depth not in (1, 2, 4, 8):
        raise PngError(f"索引色不支持位深 {bit_depth}")
    if color_type != 3 and bit_depth not in (1, 2, 4, 8, 16):
        raise PngError(f"颜色类型 {color_type} 不支持位深 {bit_depth}")
    if not idat:
        raise PngError("缺 IDAT 分块")

    colors = None
    if interlaced == 1:
        colors = None  # Adam7：跳过颜色统计（INFO），尺寸校验照常
    elif interlaced != 0:
        raise PngError(f"非法隔行标志 {interlaced}")
    else:
        try:
            raw = zlib.decompress(bytes(idat))
        except zlib.error as exc:
            raise PngError(f"IDAT 解压失败：{exc}") from exc
        colors = count_colors(raw, width, height, bit_depth, color_type, plte, trns)

    frames_hint = width // height if height and width >= height and width % height == 0 else None
    return {
        "width": width,
        "height": height,
        "bit_depth": bit_depth,
        "color_type": color_type,
        "interlaced": interlaced == 1,
        "palette_entries": (len(plte) // 3) if plte else 0,
        "colors": colors,
        "frames_hint": frames_hint,
    }


def _paeth(a: int, b: int, c: int) -> int:
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def _unfilter(raw: bytes, width: int, height: int, bpp: int, stride: int) -> bytearray:
    """逐行反滤波（Filter 0~4）。bpp=滤波用每像素字节数（≥1）；stride=每行字节数。"""
    expected = (stride + 1) * height
    if len(raw) < expected:
        raise PngError(f"像素数据不足：{len(raw)} < {expected} 字节")
    out = bytearray(stride * height)
    pos = 0
    prev_start = -stride  # 上一行在 out 中的起点（<0 表示无上行）
    for _row in range(height):
        ftype = raw[pos]
        pos += 1
        line = raw[pos:pos + stride]
        pos += stride
        row_start = _row * stride
        if ftype == 0:
            out[row_start:row_start + stride] = line
        elif ftype == 1:  # Sub
            for i in range(stride):
                left = out[row_start + i - bpp] if i >= bpp else 0
                out[row_start + i] = (line[i] + left) & 0xFF
        elif ftype == 2:  # Up
            if prev_start >= 0:
                for i in range(stride):
                    out[row_start + i] = (line[i] + out[prev_start + i]) & 0xFF
            else:
                out[row_start:row_start + stride] = line
        elif ftype == 3:  # Average
            for i in range(stride):
                left = out[row_start + i - bpp] if i >= bpp else 0
                up = out[prev_start + i] if prev_start >= 0 else 0
                out[row_start + i] = (line[i] + ((left + up) >> 1)) & 0xFF
        elif ftype == 4:  # Paeth
            for i in range(stride):
                left = out[row_start + i - bpp] if i >= bpp else 0
                up = out[prev_start + i] if prev_start >= 0 else 0
                ul = out[prev_start + i - bpp] if (prev_start >= 0 and i >= bpp) else 0
                out[row_start + i] = (line[i] + _paeth(left, up, ul)) & 0xFF
        else:
            raise PngError(f"非法行滤波类型 {ftype}")
        prev_start = row_start
    return out


def _unpack_bits(row: bytes, start: int, count: int, depth: int) -> list:
    """从行字节中取 count 个 depth 位样本（1/2/4 位索引）。"""
    vals = []
    for i in range(count):
        bit_index = (start + i) * depth
        byte = row[bit_index >> 3]
        shift = 8 - depth - (bit_index & 7)
        mask = (1 << depth) - 1
        vals.append((byte >> shift) & mask)
    return vals


def count_colors(raw, width, height, depth, color_type, plte, trns):
    """反滤波并统计 distinct (r,g,b,a)。位深 16 取高字节。"""
    channels = COLOR_TYPE_CHANNELS[color_type]
    bits_per_px = channels * depth
    stride = (width * bits_per_px + 7) >> 3
    bpp = max(1, bits_per_px >> 3)
    pixels = _unfilter(raw, width, height, bpp, stride)

    scale = {1: 0xFF, 2: 0x55, 4: 0x11, 8: 0x00, 16: None}[depth]  # 16 位特殊处理
    colors = set()
    for y in range(height):
        row_start = y * stride
        row = pixels[row_start:row_start + stride]
        for x in range(width):
            if color_type == 3:  # 索引色：PLTE 查表（无 PLTE=结构错）
                if plte is None:
                    raise PngError("索引色缺 PLTE 分块")
                if depth == 8:
                    idx = row[x]
                else:
                    idx = _unpack_bits(row, x, 1, depth)[0]
                if idx * 3 + 3 > len(plte):
                    raise PngError(f"调色板索引 {idx} 越界")
                r, g, b = plte[idx * 3], plte[idx * 3 + 1], plte[idx * 3 + 2]
                a = trns[idx] if (trns and idx < len(trns)) else 255
                colors.add((r, g, b, a))
                continue
            if depth == 16:  # 每通道 2 字节，取高字节
                off = x * channels * 2
                vals = [row[off + c * 2] for c in range(channels)]
            elif depth == 8:
                off = x * channels
                vals = [row[off + c] for c in range(channels)]
            else:  # 灰/灰α 低位于 1/2/4 位深
                vals = _unpack_bits(row, x, channels, depth)
                vals = [v * scale for v in vals]
            if color_type == 0:
                colors.add((vals[0], vals[0], vals[0], 255))
            elif color_type == 2:
                colors.add((vals[0], vals[1], vals[2], 255))
            elif color_type == 4:
                colors.add((vals[0], vals[0], vals[0], vals[1]))
            else:  # 6
                colors.add((vals[0], vals[1], vals[2], vals[3]))
    return colors


# ---------------------------------------------------------------- 校验规则


def validate_file(path: str, allowed_sizes, max_palette: int,
                  allow_oversized_palette: bool, expect_frames) -> dict:
    """校验单个 PNG，返回 result dict（path/findings[]/尺寸/帧数/颜色数）。

    allow_oversized_palette：调色板超限降为 WARN（美术特批逃生阀，
    审批留痕须走审查记录）。expect_frames：None=自动帧探测仅 INFO；
    给定 N=探测帧数≠N（含非横条图无法给出帧数）→ REJECT。
    """
    rel = path.replace(os.sep, "/")
    findings = []
    result = {
        "path": rel,
        "width": None,
        "height": None,
        "frames": None,
        "palette_colors": None,
        "findings": findings,
    }

    base = os.path.basename(path)
    if not KEBAB_RE_OK.match(base):
        findings.append({
            "rule": "FILENAME-KEBAB", "severity": "REJECT",
            "message": f"文件名 {base} 非 kebab-case 小写（^[a-z0-9]+(-[a-z0-9]+)*.png$）",
        })

    try:
        with open(path, "rb") as fh:
            data = fh.read()
    except OSError as exc:
        findings.append({"rule": "IO", "severity": "REJECT", "message": f"读取失败：{exc}"})
        return result

    try:
        info = parse_png(data)
    except PngError as exc:
        findings.append({"rule": "PNG-STRUCTURE", "severity": "REJECT", "message": str(exc)})
        return result

    result["width"], result["height"] = info["width"], info["height"]
    result["frames"] = info["frames_hint"]

    bad = [d for d in (info["width"], info["height"]) if d not in allowed_sizes]
    if bad:
        findings.append({
            "rule": "SIZE-ALLOWLIST", "severity": "REJECT",
            "message": (f"尺寸 {info['width']}x{info['height']} 不在允许清单 "
                        f"{sorted(allowed_sizes)}（14 §3 首版=Kenney Tiny Dungeon "
                        f"16×16 整数放大；非清单尺寸拒绝入库）"),
        })

    if info["colors"] is None:
        findings.append({
            "rule": "PALETTE-COUNT", "severity": "INFO",
            "message": "Adam7 隔行图：颜色统计跳过（尺寸校验不受影响）",
        })
        result["palette_colors"] = None
    else:
        ncolors = len(info["colors"])
        result["palette_colors"] = ncolors
        if ncolors > max_palette:
            findings.append({
                "rule": "PALETTE-COUNT",
                "severity": "WARN" if allow_oversized_palette else "REJECT",
                "message": (f"实际使用颜色 {ncolors} > master 上限 {max_palette}"
                            f"（14 §3「master ≤64 色」"
                            + ("；--allow-oversized-palette 逃生阀生效（美术特批，审批留痕见审查记录）"
                               if allow_oversized_palette else "；默认拒绝入库") + "）"),
            })

    if expect_frames is None:
        if info["frames_hint"] and info["frames_hint"] > 1:
            findings.append({
                "rule": "FRAME-STRIP", "severity": "INFO",
                "message": (f"横条 sheet：{info['frames_hint']} 帧×{info['height']}px"
                            f"（自动帧探测仅报告；自动帧门禁随 sheet 规格在 T-045 接入，"
                            f"声明式门禁用 --expect-frames N）"),
            })
    else:
        detected = info["frames_hint"]
        if detected is None:
            findings.append({
                "rule": "FRAME-STRIP", "severity": "REJECT",
                "message": (f"非横条 sheet（{info['width']}x{info['height']}，宽≠高×整数），"
                            f"无法给出帧数，与 --expect-frames {expect_frames} 不符"),
            })
        elif detected != expect_frames:
            findings.append({
                "rule": "FRAME-STRIP", "severity": "REJECT",
                "message": (f"探测帧数 {detected} ≠ 声明 --expect-frames {expect_frames}"
                            f"（帧数门禁以声明值为准；自动门禁 T-045 接入）"),
            })
    return result


def scan_tree(root: str) -> list:
    """递归收集目录下全部 .png（大小写不敏感扩展名；其余文件类型忽略——
    game/content/ 等纯 JSON 目录扫描结果为 0 文件即通过）。"""
    files = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if d != ".git")
        for fn in sorted(filenames):
            if fn.lower().endswith(".png"):
                files.append(os.path.join(dirpath, fn))
    return files


def run(roots, allowed_sizes, max_palette, allow_oversized_palette, expect_frames=None) -> dict:
    results = []
    for root in roots:
        if not os.path.isdir(root):
            results.append({
                "path": root.replace(os.sep, "/"), "width": None, "height": None,
                "frames": None, "palette_colors": None,
                "findings": [{"rule": "IO", "severity": "REJECT",
                              "message": f"目录不存在：{root}"}],
            })
            continue
        results.extend(
            validate_file(p, allowed_sizes, max_palette, allow_oversized_palette, expect_frames)
            for p in scan_tree(root))

    rejected = sum(1 for r in results
                   if any(f["severity"] == "REJECT" for f in r["findings"]))
    warned = sum(1 for r in results
                 if any(f["severity"] == "WARN" for f in r["findings"])
                 and not any(f["severity"] == "REJECT" for f in r["findings"]))
    exit_code = 1 if rejected else 0
    return {
        "files": len(results),
        "rejected": rejected,
        "warned": warned,
        "allowed_sizes": sorted(allowed_sizes),
        "max_palette": max_palette,
        "allow_oversized_palette": allow_oversized_palette,
        "expect_frames": expect_frames,
        "results": results,
        "exit_code": exit_code,
    }


# ---------------------------------------------------------------- CLI


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="资产导入校验器（T-046）：PNG 尺寸/调色板/文件名/帧数入库校验")
    parser.add_argument("dirs", nargs="+", help="待扫描目录（递归；非 PNG 忽略）")
    parser.add_argument("--json", metavar="REPORT", help="机器报告输出路径")
    parser.add_argument("--sizes", default=",".join(str(s) for s in DEFAULT_ALLOWED_SIZES),
                        help="允许尺寸清单（逗号分隔，默认 16,32,48,64）")
    parser.add_argument("--max-palette", type=int, default=DEFAULT_MAX_PALETTE,
                        help="master 调色板颜色上限（默认 64，14 §3）")
    parser.add_argument("--allow-oversized-palette", action="store_true",
                        help="调色板超限（>64 色）由 REJECT 降为 WARN 放行——"
                             "仅供美术特批后使用，审批留痕须走审查记录")
    parser.add_argument("--expect-frames", type=int, metavar="N", default=None,
                        help="声明式帧数门禁：给定 N 时探测帧数≠N（含非横条图"
                             "无法给出帧数）→ REJECT；缺省=自动帧探测仅 INFO"
                             "（自动门禁随 T-045 接入）")
    args = parser.parse_args(argv)

    try:
        allowed = tuple(sorted({int(s) for s in args.sizes.split(",") if s.strip()}))
    except ValueError:
        print("asset_validator: --sizes 须为逗号分隔整数", file=sys.stderr)
        return 2

    if args.expect_frames is not None and args.expect_frames < 1:
        print("asset_validator: --expect-frames 须为正整数", file=sys.stderr)
        return 2

    try:
        report = run(args.dirs, allowed, args.max_palette,
                     args.allow_oversized_palette, args.expect_frames)
    except OSError as exc:
        print(f"asset_validator: IO 错误：{exc}", file=sys.stderr)
        return 2

    # stdout：人读摘要（每文件一行 + 汇总）
    for r in report["results"]:
        worst = next((f["severity"] for f in r["findings"]
                      if f["severity"] in ("REJECT", "WARN")), None)
        tag = {"REJECT": "REJECT", "WARN": "WARN"}.get(worst, "ok")
        size = f"{r['width']}x{r['height']}" if r["width"] else "-"
        colors = str(r["palette_colors"]) if r["palette_colors"] is not None else "-"
        frames = str(r["frames"]) if r["frames"] is not None else "-"
        print(f"  [{tag:>6}] {r['path']}  size={size}  colors={colors}  frames={frames}")
        for f in r["findings"]:
            if f["severity"] != "INFO":
                print(f"          {f['severity']:<6} {f['rule']}: {f['message']}")
    print(f"summary: files={report['files']} rejected={report['rejected']} "
          f"warned={report['warned']} allowed_sizes={report['allowed_sizes']} "
          f"max_palette={report['max_palette']} "
          f"allow_oversized_palette={report['allow_oversized_palette']} "
          f"expect_frames={report['expect_frames']}")
    print(f"exit_code: {report['exit_code']}")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(report, fh, ensure_ascii=False, indent=1)
    return report["exit_code"]


if __name__ == "__main__":
    sys.exit(main())
