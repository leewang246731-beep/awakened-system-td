#!/usr/bin/env python3
"""
test_asset_validator.py — asset_validator.py 自测（任务 T-046；零三方依赖，
`python test_asset_validator.py` 直跑）。

全部 PNG 样例由手写字节构造（最小 IHDR/IDAT/IEND + zlib.compress + CRC），
不依赖任何外部图片文件：
  1. png_bytes_builder    构造器：任意宽高/位深/颜色类型/像素→合法 PNG 字节
  2. size_rules           尺寸允许清单正反例（16/32/48/64 过；1/20/24/128 拒）
  3. palette_rules        颜色计数：单色过；>64 色默认 REJECT（Wave 1 round 1
                          review #6 起默认拒绝）、--allow-oversized-palette
                          逃生阀降 WARN 放行；索引色（PLTE+tRNS）计数正确
  4. frames_rules         帧数门禁：缺省自动探测仅 INFO；--expect-frames
                          匹配过、不匹配拒、非横条图拒
  5. filename_rules       kebab-case 过；大写/下划线拒
  6. structure_rules      截断/CRC 损坏/非 PNG 拒；隔行图跳过计数不崩
  7. cinematics_exemption T-152 显式豁免（31 号 §三 C 层序列帧）：
                          game/assets/cinematics/ 下 PNG 尺寸/色板降 INFO
                          （消息带豁免注记）；结构/命名仍拒；豁免区外同规格
                          照常拒；仓内 8 张真实 1440x810 Atlas 零拒
  7b. expand_exemption   T-096-v02 显式豁免②（Q16A 分层红线）：expand-v1
                          插画聚合件（P1/P3/P4 非图标）尺寸/色板降 INFO；
                          P4/achievements/ B 档严管子树不豁免照常拒；
                          结构/命名仍拒；仓内 73 件聚合树零拒
  7c. store_exemption    T-108 显式豁免区③（商店页物料）：store/ 下 PNG
                          尺寸/色板降 INFO；版本号带点/结构损坏仍拒；
                          豁免区外同规格照常拒；仓内真实物料零拒
  8. cli_semantics        CLI 端到端：退出码 0/1、--json 报告结构、
                          非 PNG 目录（game/content/ 纯 JSON）0 文件通过
样例均在系统临时目录创建、用后删除。
"""

from __future__ import annotations

import importlib.util
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib

sys.dont_write_bytecode = True  # 不在 tools/ci 留下 __pycache__

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(HERE))

_spec = importlib.util.spec_from_file_location(
    "asset_validator", os.path.join(HERE, "asset_validator.py"))
av = importlib.util.module_from_spec(_spec)
sys.modules["asset_validator"] = av
_spec.loader.exec_module(av)

PASS = 0
FAIL = 0


def check(cond: bool, label: str) -> None:
    global PASS, FAIL
    if cond:
        PASS += 1
        print(f"  ok   {label}")
    else:
        FAIL += 1
        print(f"  FAIL {label}")


# ---------------------------------------------------------------- PNG 构造器


def chunk(ctype: bytes, data: bytes) -> bytes:
    return (struct.pack(">I", len(data)) + ctype + data
            + struct.pack(">I", zlib.crc32(ctype + data) & 0xFFFFFFFF))


def png_bytes(width: int, height: int, depth: int, color_type: int,
              pixels: bytes = b"", plte: bytes = b"", trns: bytes = b"",
              interlace: int = 0, corrupt_crc: bool = False) -> bytes:
    """最小合法 PNG。pixels=已含每行滤波字节的原始扫描流（Filter 0）。"""
    if not pixels:  # 缺省：全 0 像素
        stride = (width * depth * av.COLOR_TYPE_CHANNELS[color_type] + 7) >> 3
        pixels = b"".join(b"\x00" + b"\x00" * stride for _ in range(height))
    ihdr = struct.pack(">IIBBBBB", width, height, depth, color_type, 0, 0, interlace)
    out = av.PNG_SIGNATURE + chunk(b"IHDR", ihdr)
    if plte:
        out += chunk(b"PLTE", plte)
    if trns:
        out += chunk(b"tRNS", trns)
    out += chunk(b"IDAT", zlib.compress(bytes(pixels)))
    if corrupt_crc:  # 破坏 IHDR 分块 CRC（字节 29:33：签名 8+长度 4+类型 4+数据 13）
        out = out[:29] + b"\x00\x00\x00\x00" + out[33:]
    out += chunk(b"IEND", b"")
    return out


def rgba_rows(width: int, height: int, color_fn) -> bytes:
    """8 位 RGBA 像素 → Filter 0 行流。color_fn(x, y) -> (r,g,b,a)。"""
    rows = []
    for y in range(height):
        row = bytearray()
        for x in range(width):
            row.extend(color_fn(x, y))
        rows.append(b"\x00" + bytes(row))
    return b"".join(rows)


def write(root: str, name: str, data: bytes) -> str:
    path = os.path.join(root, name)
    with open(path, "wb") as fh:
        fh.write(data)
    return path


def rules_of(result: dict) -> set:
    return {f["rule"] for f in result["findings"]}


def severity_of(result: dict, rule: str):
    for f in result["findings"]:
        if f["rule"] == rule:
            return f["severity"]
    return None


# ---------------------------------------------------------------- 用例


def test_png_builder_and_parser() -> None:
    """构造器自证：手写字节能被 parse_png 正确读回（1x1 与 32x32）。"""
    one = png_bytes(1, 1, 8, 6, pixels=b"\x00\xff\x00\x00\xff")
    info = av.parse_png(one)
    check(info["width"] == 1 and info["height"] == 1, "1x1 IHDR 宽高解析")
    check(info["colors"] == {(255, 0, 0, 255)}, "1x1 RGBA 像素颜色")

    rows = rgba_rows(32, 32, lambda x, y: (x * 8 % 256, y * 8 % 256, 0, 255))
    big = png_bytes(32, 32, 8, 6, pixels=rows)
    info = av.parse_png(big)
    check(info["width"] == 32 and info["height"] == 32, "32x32 IHDR 宽高解析")
    check(len(info["colors"]) == 32 * 32, "32x32 全异色计数=1024（反滤波链路正确）")

    # RGB（颜色类型 2）单色
    rgb = png_bytes(16, 16, 8, 2,
                    pixels=b"".join(b"\x00" + b"\x10\x20\x30" * 16 for _ in range(16)))
    check(av.parse_png(rgb)["colors"] == {(16, 32, 48, 255)}, "16x16 RGB 单色计数")


def test_size_rules() -> None:
    root = tempfile.mkdtemp(prefix="av_size_")
    try:
        write(root, "ok-16.png", png_bytes(16, 16, 8, 2))
        write(root, "ok-32.png", png_bytes(32, 32, 8, 2))
        write(root, "ok-48.png", png_bytes(48, 48, 8, 2))  # 清单内非 2 幂例外
        write(root, "ok-64.png", png_bytes(64, 64, 8, 2))
        write(root, "bad-1.png", png_bytes(1, 1, 8, 6, pixels=b"\x00\xff\x00\x00\xff"))
        write(root, "bad-20.png", png_bytes(20, 20, 8, 2))   # 16 的 1.25 倍
        write(root, "bad-24.png", png_bytes(24, 24, 8, 2))   # 非 2 幂且不在清单
        write(root, "bad-128.png", png_bytes(128, 16, 8, 2))  # 2 幂但不在清单
        report = av.run([root], av.DEFAULT_ALLOWED_SIZES, 64, False)
        by_name = {os.path.basename(r["path"]): r for r in report["results"]}
        for name in ("ok-16.png", "ok-32.png", "ok-48.png", "ok-64.png"):
            check(not rules_of(by_name[name]) & {"SIZE-ALLOWLIST"},
                  f"{name} 过尺寸清单")
        for name in ("bad-1.png", "bad-20.png", "bad-24.png", "bad-128.png"):
            check(severity_of(by_name[name], "SIZE-ALLOWLIST") == "REJECT",
                  f"{name} 拒（不在允许清单）")
        check(report["rejected"] == 4 and report["exit_code"] == 1, "尺寸汇总 4 拒/exit 1")
    finally:
        shutil.rmtree(root, ignore_errors=True)


def test_palette_rules() -> None:
    root = tempfile.mkdtemp(prefix="av_pal_")
    many = os.path.join(root, "many")
    idx = os.path.join(root, "idx")
    os.makedirs(many)
    os.makedirs(idx)
    try:
        # 65+ 色 RGBA（>64 → 默认 REJECT；--allow-oversized-palette → WARN 放行）
        rows = rgba_rows(16, 16, lambda x, y: ((y * 16 + x) % 256, 0, 0, 255))
        write(many, "many-colors.png", png_bytes(16, 16, 8, 6, pixels=rows))
        report = av.run([many], av.DEFAULT_ALLOWED_SIZES, 64, False)
        r = report["results"][0]
        check(r["palette_colors"] == 256, "颜色计数=256")
        check(severity_of(r, "PALETTE-COUNT") == "REJECT",
              "65+ 色默认 REJECT（拒绝入库）")
        check(report["exit_code"] == 1, "默认超限 exit 1")
        escape = av.run([many], av.DEFAULT_ALLOWED_SIZES, 64, True)
        check(severity_of(escape["results"][0], "PALETTE-COUNT") == "WARN",
              "--allow-oversized-palette 逃生阀降 WARN（美术特批）")
        check(escape["exit_code"] == 0 and escape["warned"] == 1,
              "逃生阀下 WARN 放行 exit 0 且计入 warned")

        # 索引色：PLTE 4 色 + tRNS 1 半透明 → distinct (rgb,a) 计数
        plte = bytes([255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9])
        trns = bytes([128])  # 索引 0 半透明
        idx_rows = b"".join(b"\x00" + bytes([(x + y) % 4 for x in range(16)])
                            for y in range(16))
        write(idx, "indexed.png", png_bytes(16, 16, 8, 3, pixels=idx_rows,
                                            plte=plte, trns=trns))
        rep = av.run([idx], av.DEFAULT_ALLOWED_SIZES, 64, False)
        check(rep["results"][0]["palette_colors"] == 4,
              "索引色 distinct 颜色计数（tRNS 计入）")
        check(rep["results"][0]["palette_colors"] <= 64, "索引色 ≤64 通过")
    finally:
        shutil.rmtree(root, ignore_errors=True)


def test_frames_rules() -> None:
    root = tempfile.mkdtemp(prefix="av_frames_")
    strip = os.path.join(root, "strip")
    single = os.path.join(root, "single")
    os.makedirs(strip)
    os.makedirs(single)
    try:
        # 32×16 横条 sheet：帧数=32/16=2（两帧各 16×16，单色）
        rows = rgba_rows(32, 16, lambda x, y: (10, 20, 30, 255))
        write(strip, "walk-sheet.png", png_bytes(32, 16, 8, 6, pixels=rows))
        # 16×16 方图：frames_hint=1（非多帧横条）
        write(single, "icon.png", png_bytes(16, 16, 8, 6,
                                            pixels=rgba_rows(16, 16, lambda x, y: (1, 2, 3, 255))))

        base = av.run([strip], av.DEFAULT_ALLOWED_SIZES, 64, False)
        check(severity_of(base["results"][0], "FRAME-STRIP") == "INFO",
              "缺省：自动帧探测仅 INFO（自动门禁 T-045 接入）")
        check(base["results"][0]["frames"] == 2, "横条帧数探测=宽/高=2")
        check(base["exit_code"] == 0, "缺省帧探测不改退出码")

        match = av.run([strip], av.DEFAULT_ALLOWED_SIZES, 64, False, expect_frames=2)
        check("FRAME-STRIP" not in rules_of(match["results"][0]),
              "--expect-frames 2 与探测一致：无帧数违规")
        check(match["exit_code"] == 0, "--expect-frames 匹配 exit 0")

        mismatch = av.run([strip], av.DEFAULT_ALLOWED_SIZES, 64, False, expect_frames=4)
        check(severity_of(mismatch["results"][0], "FRAME-STRIP") == "REJECT",
              "--expect-frames 4 ≠ 探测 2 → REJECT")
        check(mismatch["exit_code"] == 1, "帧数不符 exit 1")

        icon = av.run([single], av.DEFAULT_ALLOWED_SIZES, 64, False, expect_frames=2)
        check(severity_of(icon["results"][0], "FRAME-STRIP") == "REJECT",
              "非横条图（frames=1≠2）在声明门禁下拒")

        # 16×32 竖条（w<h，frames_hint=None）+ expect-frames → 无法给出帧数即拒
        tall = os.path.join(root, "tall")
        os.makedirs(tall)
        write(tall, "tall-strip.png", png_bytes(16, 32, 8, 6,
                                                pixels=rgba_rows(16, 32, lambda x, y: (5, 6, 7, 255))))
        tall_rep = av.run([tall], av.DEFAULT_ALLOWED_SIZES, 64, False, expect_frames=2)
        check(severity_of(tall_rep["results"][0], "FRAME-STRIP") == "REJECT",
              "非横条图（无帧数）在声明门禁下拒")
        check(tall_rep["results"][0]["frames"] is None, "竖条图 frames_hint=None")
    finally:
        shutil.rmtree(root, ignore_errors=True)


def test_filename_rules() -> None:
    root = tempfile.mkdtemp(prefix="av_name_")
    try:
        good = png_bytes(16, 16, 8, 2)
        write(root, "tower-wood-arrow.png", good)
        write(root, "tile-0001.png", good)
        write(root, "Upper.png", good)          # 大写
        write(root, "snake_case.png", good)     # 下划线
        write(root, "sp ace.png", good)         # 空格
        report = av.run([root], av.DEFAULT_ALLOWED_SIZES, 64, False)
        by_name = {os.path.basename(r["path"]): r for r in report["results"]}
        for name in ("tower-wood-arrow.png", "tile-0001.png"):
            check(not rules_of(by_name[name]) & {"FILENAME-KEBAB"}, f"{name} 命名通过")
        for name in ("Upper.png", "snake_case.png", "sp ace.png"):
            check(severity_of(by_name[name], "FILENAME-KEBAB") == "REJECT",
                  f"{name} 命名拒")
        check(report["rejected"] == 3, "命名汇总 3 拒")
    finally:
        shutil.rmtree(root, ignore_errors=True)


def test_structure_rules() -> None:
    root = tempfile.mkdtemp(prefix="av_struct_")
    try:
        write(root, "truncated.png", png_bytes(16, 16, 8, 2)[:30])   # 截断
        write(root, "bad-crc.png", png_bytes(16, 16, 8, 2, corrupt_crc=True))
        write(root, "not-png.png", b"JFIF-not-a-png-at-all" * 4)
        write(root, "interlaced.png", png_bytes(16, 16, 8, 2, interlace=1))
        report = av.run([root], av.DEFAULT_ALLOWED_SIZES, 64, False)
        by_name = {os.path.basename(r["path"]): r for r in report["results"]}
        for name in ("truncated.png", "bad-crc.png", "not-png.png"):
            check(severity_of(by_name[name], "PNG-STRUCTURE") == "REJECT",
                  f"{name} 结构拒")
        inter = by_name["interlaced.png"]
        check(severity_of(inter, "PNG-STRUCTURE") is None
              and inter["palette_colors"] is None
              and severity_of(inter, "PALETTE-COUNT") == "INFO",
              "隔行图：尺寸过、颜色统计 INFO 跳过不崩")
    finally:
        shutil.rmtree(root, ignore_errors=True)


def test_cinematics_exemption() -> None:
    """T-152 显式豁免（31 号 §三 C 层）：game/assets/cinematics/ 下 PNG 的
    尺寸/色板门禁降 INFO（豁免注明入消息）；结构/命名红线不豁免；豁免区外
    同规格 PNG 照常 REJECT（豁免不外溢）。"""
    root = tempfile.mkdtemp(prefix="av_cine_")
    try:
        cine_dir = os.path.join(root, "game", "assets", "cinematics", "strips")
        os.makedirs(cine_dir)
        # 1440x810 序列帧 Atlas 形态（非清单尺寸；宽:高=16:9 非横条帧）
        write(cine_dir, "boss-e1-death.png", png_bytes(1440, 810, 8, 6))
        # 豁免区内仍拒：结构损坏 / 非 kebab 命名
        write(cine_dir, "broken.png", png_bytes(16, 16, 8, 2)[:30])
        write(cine_dir, "BadName.PNG", png_bytes(16, 16, 8, 2))
        # 豁免区外同尺寸：照常 REJECT（豁免按路径界定，不外溢）
        outside = os.path.join(root, "game", "assets", "era1")
        os.makedirs(outside)
        write(outside, "big-banner.png", png_bytes(1440, 810, 8, 6))

        report = av.run([root], av.DEFAULT_ALLOWED_SIZES, 64, False)
        by_path = {
            os.path.relpath(r["path"], root).replace(os.sep, "/"): r
            for r in report["results"]
        }
        strip = by_path["game/assets/cinematics/strips/boss-e1-death.png"]
        check(severity_of(strip, "SIZE-ALLOWLIST") == "INFO"
              and "cinematics 序列帧豁免" in next(
                  f["message"] for f in strip["findings"]
                  if f["rule"] == "SIZE-ALLOWLIST"),
              "cinematics 豁免：尺寸门禁降 INFO 且消息带豁免注记")
        check(severity_of(by_path["game/assets/cinematics/strips/broken.png"],
                          "PNG-STRUCTURE") == "REJECT", "豁免区结构损坏仍拒")
        check(severity_of(by_path["game/assets/cinematics/strips/BadName.PNG"],
                          "FILENAME-KEBAB") == "REJECT", "豁免区命名不豁免")
        check(severity_of(by_path["game/assets/era1/big-banner.png"],
                          "SIZE-ALLOWLIST") == "REJECT", "豁免区外同尺寸照常拒")
        check(report["rejected"] == 3, "豁免用例汇总 3 拒（结构/命名/区外尺寸）")
    finally:
        shutil.rmtree(root, ignore_errors=True)

    # 仓内真实序列帧（8 张 1440x810）：全 INFO 豁免、零 REJECT
    strips_dir = os.path.join(REPO_ROOT, "game", "assets", "cinematics")
    if os.path.isdir(strips_dir):
        report = av.run([strips_dir], av.DEFAULT_ALLOWED_SIZES, 64, False)
        pngs = [r for r in report["results"] if r["path"].endswith(".png")]
        check(report["rejected"] == 0, f"仓内 cinematics 序列帧零拒（{len(pngs)} PNG）")
        check(all(r["width"] == 1440 and r["height"] == 810 for r in pngs),
              "仓内序列帧全部 1440x810（36 帧@240x135）")


def test_expand_illustration_exemption() -> None:
    """T-096-v02 显式豁免②（Q16A A 档直用分层红线）：expand-v1 插画聚合件
    （非 achievements 子树）尺寸/色板降 INFO；P4/achievements/ B 档严管
    子树不豁免；结构/命名红线不豁免；豁免区外照常拒。"""
    root = tempfile.mkdtemp(prefix="av_expand_")
    try:
        p1 = os.path.join(root, "game", "assets", "expand-v1", "P1", "banners")
        os.makedirs(p1)
        # 20x20 + >64 色：豁免面应双 INFO（尺寸+色板）
        rows = rgba_rows(20, 20, lambda x, y: ((y * 20 + x) % 256, 0, 0, 255))
        write(p1, "banner-e1-l1-x.png", png_bytes(20, 20, 8, 6, pixels=rows))
        # 严管子树：P4/achievements/ 同规格照常双 REJECT（豁免不外溢）
        achv = os.path.join(root, "game", "assets", "expand-v1", "P4", "achievements")
        os.makedirs(achv)
        write(achv, "achv-x.png", png_bytes(20, 20, 8, 6, pixels=rows))
        # 豁免区内仍拒：结构损坏 / 非 kebab 命名
        skins = os.path.join(root, "game", "assets", "expand-v1", "P4", "skins")
        os.makedirs(skins)
        write(skins, "broken.png", png_bytes(16, 16, 8, 2)[:30])
        write(skins, "BadName.PNG", png_bytes(16, 16, 8, 2))
        # 豁免区外同规格：照常拒
        outside = os.path.join(root, "game", "assets", "era2")
        os.makedirs(outside)
        write(outside, "big-art.png", png_bytes(20, 20, 8, 6, pixels=rows))

        report = av.run([root], av.DEFAULT_ALLOWED_SIZES, 64, False)
        by_path = {
            os.path.relpath(r["path"], root).replace(os.sep, "/"): r
            for r in report["results"]
        }
        banner = by_path["game/assets/expand-v1/P1/banners/banner-e1-l1-x.png"]
        check(severity_of(banner, "SIZE-ALLOWLIST") == "INFO"
              and severity_of(banner, "PALETTE-COUNT") == "INFO"
              and "expand-v1 插画聚合豁免" in next(
                  f["message"] for f in banner["findings"]
                  if f["rule"] == "SIZE-ALLOWLIST"),
              "expand-v1 插画豁免：尺寸/色板降 INFO 且消息带豁免注记")
        icon = by_path["game/assets/expand-v1/P4/achievements/achv-x.png"]
        check(severity_of(icon, "SIZE-ALLOWLIST") == "REJECT"
              and severity_of(icon, "PALETTE-COUNT") == "REJECT",
              "P4/achievements B 档严管子树不豁免（尺寸/色板照常拒）")
        check(severity_of(by_path["game/assets/expand-v1/P4/skins/broken.png"],
                          "PNG-STRUCTURE") == "REJECT", "豁免区结构损坏仍拒")
        check(severity_of(by_path["game/assets/expand-v1/P4/skins/BadName.PNG"],
                          "FILENAME-KEBAB") == "REJECT", "豁免区命名不豁免")
        check(severity_of(by_path["game/assets/era2/big-art.png"],
                          "SIZE-ALLOWLIST") == "REJECT", "豁免区外同尺寸照常拒")
        check(report["rejected"] == 4,
              "豁免用例汇总 4 拒（严管子树 1+结构 1+命名 1+区外 1）")
    finally:
        shutil.rmtree(root, ignore_errors=True)

    # 仓内真实聚合树（T-096-v02 聚合后）：49 插画+24 图标全零拒
    expand_dir = os.path.join(REPO_ROOT, "game", "assets", "expand-v1")
    if os.path.isdir(expand_dir):
        report = av.run([expand_dir], av.DEFAULT_ALLOWED_SIZES, 64, False)
        pngs = [r for r in report["results"] if r["path"].endswith(".png")]
        check(report["rejected"] == 0, f"仓内 expand-v1 聚合树零拒（{len(pngs)} PNG）")
        achv_pngs = [r for r in pngs if "/P4/achievements/" in r["path"]]
        check(len(achv_pngs) == 24 and all(
            r["width"] == 64 and r["height"] == 64 for r in achv_pngs),
            "仓内成就图标 24 枚全 64x64（严管面真过门禁）")


def test_cli_semantics() -> None:
    """CLI 端到端（子进程真实退出码）：正例 0、反例 1、--json 结构、
    非 PNG 目录（game/content/ 纯 JSON）0 文件通过。"""
    good = tempfile.mkdtemp(prefix="av_cli_ok_")
    bad = tempfile.mkdtemp(prefix="av_cli_bad_")
    report_path = os.path.join(good, "report.json")  # .json 不在扫描范围
    try:
        write(good, "tile-grass.png", png_bytes(16, 16, 8, 2))
        # game/content/ 全 JSON（T-049 内容文件）：扫描应 0 PNG、exit 0
        content_dir = os.path.join(REPO_ROOT, "game", "content").replace(os.sep, "/")

        r_ok = subprocess.run(
            [sys.executable, os.path.join(HERE, "asset_validator.py"),
             "--json", report_path, good, content_dir],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        check(r_ok.returncode == 0, "CLI 全过目录 exit 0")
        with open(report_path, encoding="utf-8") as fh:
            rep = json.load(fh)
        check(rep["files"] == 1 and rep["rejected"] == 0 and rep["warned"] == 0
              and rep["exit_code"] == 0, "--json 报告字段齐全（正例）")
        check(rep["results"][0]["width"] == 16 and rep["results"][0]["height"] == 16,
              "--json 报告含 IHDR 实测尺寸")

        write(bad, "TooBig.png", png_bytes(20, 20, 8, 2))
        r_bad = subprocess.run(
            [sys.executable, os.path.join(HERE, "asset_validator.py"), bad],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        check(r_bad.returncode == 1, "CLI 违规目录 exit 1")
        check("SIZE-ALLOWLIST" in r_bad.stdout and "FILENAME-KEBAB" in r_bad.stdout,
              "stdout 摘要含违规规则名")

        # CLI 旗标端到端：横条 sheet 32×16（2 帧）× --expect-frames 4 → REJECT；
        # --expect-frames 2 → exit 0；非法值（0）→ 参数错误 exit 2
        frames_dir = tempfile.mkdtemp(prefix="av_cli_frames_")
        try:
            strip_rows = rgba_rows(32, 16, lambda x, y: (10, 20, 30, 255))
            write(frames_dir, "walk-sheet.png", png_bytes(32, 16, 8, 6, pixels=strip_rows))
            r_mismatch = subprocess.run(
                [sys.executable, os.path.join(HERE, "asset_validator.py"),
                 "--expect-frames", "4", frames_dir],
                capture_output=True, text=True, encoding="utf-8", errors="replace")
            check(r_mismatch.returncode == 1 and "FRAME-STRIP" in r_mismatch.stdout,
                  "CLI --expect-frames 不匹配 → exit 1 且 stdout 含 FRAME-STRIP")
            r_match = subprocess.run(
                [sys.executable, os.path.join(HERE, "asset_validator.py"),
                 "--expect-frames", "2", frames_dir],
                capture_output=True, text=True, encoding="utf-8", errors="replace")
            check(r_match.returncode == 0, "CLI --expect-frames 匹配 → exit 0")
            r_badflag = subprocess.run(
                [sys.executable, os.path.join(HERE, "asset_validator.py"),
                 "--expect-frames", "0", frames_dir],
                capture_output=True, text=True, encoding="utf-8", errors="replace")
            check(r_badflag.returncode == 2, "CLI --expect-frames 0 → 参数错误 exit 2")
        finally:
            shutil.rmtree(frames_dir, ignore_errors=True)

        r_missing = subprocess.run(
            [sys.executable, os.path.join(HERE, "asset_validator.py"),
             os.path.join(bad, "no-such-dir")],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        check(r_missing.returncode == 1, "不存在目录按 REJECT 处理（exit 1）")
    finally:
        shutil.rmtree(good, ignore_errors=True)
        shutil.rmtree(bad, ignore_errors=True)


def test_store_exemption() -> None:
    """T-108 显式豁免区③（商店页物料）：game/assets/store/ 下 PNG 尺寸/色板
    降 INFO（消息带豁免注记）；结构/命名红线不豁免（版本号不带点的
    v020-alpha kebab 形态过、带点形态拒）；豁免区外同规格照常拒；仓内真实
    商店物料零拒。"""
    root = tempfile.mkdtemp(prefix="av_store_")
    try:
        store_dir = os.path.join(root, "game", "assets", "store")
        os.makedirs(store_dir)
        # 640x360 真实录屏截图形态（非清单尺寸+多色）
        write(store_dir, "shot-01-combat-v020-alpha.png", png_bytes(640, 360, 8, 6))
        # 豁免区内仍拒：版本号带点（非 kebab）/ 结构损坏
        write(store_dir, "shot-99-v0.2.0-alpha.png", png_bytes(640, 360, 8, 6))
        write(store_dir, "broken.png", png_bytes(16, 16, 8, 2)[:30])
        # 豁免区外同尺寸：照常 REJECT（豁免按路径界定，不外溢）
        outside = os.path.join(root, "game", "assets", "era2")
        os.makedirs(outside)
        write(outside, "big-shot.png", png_bytes(640, 360, 8, 6))

        report = av.run([root], av.DEFAULT_ALLOWED_SIZES, 64, False)
        by_path = {
            os.path.relpath(r["path"], root).replace(os.sep, "/"): r
            for r in report["results"]
        }
        shot = by_path["game/assets/store/shot-01-combat-v020-alpha.png"]
        check(severity_of(shot, "SIZE-ALLOWLIST") == "INFO"
              and "store 商店物料豁免" in next(
                  f["message"] for f in shot["findings"]
                  if f["rule"] == "SIZE-ALLOWLIST"),
              "store 豁免：尺寸门禁降 INFO 且消息带豁免注记")
        check(severity_of(by_path["game/assets/store/shot-99-v0.2.0-alpha.png"],
                          "FILENAME-KEBAB") == "REJECT", "豁免区版本号带点仍拒（kebab 红线）")
        check(severity_of(by_path["game/assets/store/broken.png"],
                          "PNG-STRUCTURE") == "REJECT", "豁免区结构损坏仍拒")
        check(severity_of(by_path["game/assets/era2/big-shot.png"],
                          "SIZE-ALLOWLIST") == "REJECT", "豁免区外同尺寸照常拒")
        check(report["rejected"] == 3, "豁免用例汇总 3 拒（带点命名/结构/区外尺寸）")
    finally:
        shutil.rmtree(root, ignore_errors=True)

    # 仓内真实商店物料（v020-alpha 批）：全过、零 REJECT
    repo_store = os.path.join(REPO_ROOT, "game", "assets", "store")
    if os.path.isdir(repo_store):
        report = av.run([repo_store], av.DEFAULT_ALLOWED_SIZES, 64, False)
        pngs = [r for r in report["results"] if r["path"].endswith(".png")]
        check(report["rejected"] == 0, f"仓内商店物料零拒（{len(pngs)} PNG）")


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass
    print(f"selftest: {os.path.join('tools', 'ci', 'asset_validator.py')}")
    print(f"python: {sys.version.split()[0]}")
    test_png_builder_and_parser()
    test_size_rules()
    test_palette_rules()
    test_frames_rules()
    test_filename_rules()
    test_structure_rules()
    test_cinematics_exemption()
    test_expand_illustration_exemption()
    test_store_exemption()
    test_cli_semantics()
    print("-" * 60)
    print(f"selftest summary: {PASS} passed, {FAIL} failed")
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
