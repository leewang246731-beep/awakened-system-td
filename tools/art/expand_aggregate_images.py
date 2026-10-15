#!/usr/bin/env python3
"""expand_aggregate_images.py — T-096-v02 发布期图片聚合（视觉扩充版 v0.2.0-alpha）。

管线（docs/engineering/31-visual-enrichment-master-plan.md §4.4/§五.5 + 21 号
§2.2 发布轨 + D21-5 双轨）：

  U 盘外部轨 E:/game-assets/expand-v1/{P1,P3,P4}/…（开发期直读轨）
    → 2×整数降采样（BOX 面积平均，像素风纪律=只允许整数倍缩放）
    → 入库 game/assets/expand-v1/{P1,P3,P4}/…（repo 内置轨——发布树随
       build_v020.sh 装配到 exe 旁，ExpandAssetPaths.ExternalImagePath 的
       内置轨回退在此命中；dev 机仍优先 U 盘轨，两轨字节不同属预期）

尺寸决策（本文件即决策记录，REV-20261016-T096-v02 §2 同步）：
  1536x1024 → 768x512   （P1 题图 7 + P4 加载图 6 + P3 独占彩蛋 2）
  1024x1024 → 512x512   （P1 插画/钩子 15+5 + P3 异变 4 + P4 皮肤 6）
  1024x1536 → 512x768   （P4 军师立绘 4）
  64x64     → 原样      （P4 成就图标 24——程序合成件早已入库，仅校验）
合计外部轨 49 张聚合 + 24 枚图标校验 = 目录 73 图口径（31 号 §二.5）。

降采样核=BOX（2×2 面积平均）：AI"像素风邻接"插画非真像素网格，最近邻会任意
丢 3/4 采样引入锯齿伪影；BOX 是整数倍降采样的规范核。像素化语义只用于跃迁
视频链（Q18A），静帧插画不做二重像素化。颜色数随平均插值增加——
asset_validator 的 expand-v1 插画显式豁免（Q16A A 档直用分层红线）承接，
B 档成就图标子树（P4/achievements/）维持严管不豁免。

确定性纪律：解码复用 asset_validator.parse_png（含 CRC/反滤波全链），编码
filter=0 + zlib 固定级别——同源字节二次聚合 sha256 全等（--verify 断言）。

用法（Git Bash / Windows 均可）：
  python tools/art/expand_aggregate_images.py             # 聚合（幂等：已存在且
                                                          #  sha 与台账一致则跳过）
  python tools/art/expand_aggregate_images.py --verify    # 只校验不入库
  FORCE=1 python tools/art/expand_aggregate_images.py     # 忽略已存在重聚合

台账：game/assets/expand-v1/aggregation-manifest.json（随包分发=Release notes
AI 披露的素材 manifest 链接落点之一）。
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import struct
import sys
import time
import zlib

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC_ROOT = os.environ.get("EXPAND_V1_IMAGES", "E:/game-assets/expand-v1")
DST_ROOT = os.path.join(REPO, "game", "assets", "expand-v1")
LEDGER = os.path.join(DST_ROOT, "aggregation-manifest.json")

# 复用资产门禁的 PNG 解码器（CRC/反滤波/Paeth 全链，零三方依赖）
_spec = importlib.util.spec_from_file_location(
    "asset_validator", os.path.join(REPO, "tools", "ci", "asset_validator.py"))
av = importlib.util.module_from_spec(_spec)
sys.modules["asset_validator"] = av
_spec.loader.exec_module(av)


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for block in iter(lambda: fh.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def png_crc(ctype: bytes, data: bytes) -> bytes:
    return struct.pack(">I", zlib.crc32(ctype + data) & 0xFFFFFFFF)


def encode_png(width: int, height: int, color_type: int, raw_rows: bytes) -> bytes:
    """最小确定性 PNG 编码（filter=0 行流 → IHDR/IDAT/IEND，zlib 固定级别）。"""
    depth = 8
    ihdr = struct.pack(">IIBBBBB", width, height, depth, color_type, 0, 0, 0)
    out = av.PNG_SIGNATURE + struct.pack(">I", 13) + b"IHDR" + ihdr + png_crc(b"IHDR", ihdr)
    idat = zlib.compress(bytes(raw_rows), 9)
    out += struct.pack(">I", len(idat)) + b"IDAT" + idat + png_crc(b"IDAT", idat)
    out += struct.pack(">I", 0) + b"IEND" + png_crc(b"IEND", b"")
    return out


def decode_rgba(data: bytes):
    """PNG 字节 → (width, height, bytearray RGBA)。仅支持 8 位 RGB/RGBA（生产
    管线 gpt-image-2 直出件口径）；索引色/16 位/隔行等形态直接报错拒绝聚合。"""
    info = av.parse_png(data)
    w, h, depth, ctype, interlaced = (
        info["width"], info["height"], info["bit_depth"], info["color_type"], info["interlaced"])
    if interlaced:
        raise ValueError("隔行 PNG 不支持（生产管线不应出现）")
    if depth != 8 or ctype not in (2, 6):
        raise ValueError(f"仅支持 8 位 RGB/RGBA（得到 depth={depth} color_type={ctype}）")
    channels = 3 if ctype == 2 else 4
    # 重新走一遍反滤波拿原始字节（parse_png 只回颜色集合，不回像素）
    pos, idat = 8, bytearray()
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        ctype_chunk = data[pos + 4:pos + 8]
        if ctype_chunk == b"IDAT":
            idat.extend(data[pos + 8:pos + 8 + length])
        pos += 8 + length + 4
        if ctype_chunk == b"IEND":
            break
    stride = w * channels
    px = av._unfilter(zlib.decompress(bytes(idat)), w, h, channels, stride)
    rgba = bytearray(w * h * 4)
    for i in range(w * h):
        rgba[i * 4:i * 4 + 3] = px[i * channels:i * channels + 3]
        rgba[i * 4 + 3] = px[i * channels + 3] if channels == 4 else 255
    return w, h, ctype, rgba


def downsample_2x_box(w: int, h: int, ctype: int, rgba: bytearray):
    """2×整数 BOX（面积平均）降采样；返回 (w2, h2, ctype, rgba2)。宽高非偶即错。"""
    if w % 2 or h % 2:
        raise ValueError(f"尺寸 {w}x{h} 非偶——2×整数降采样纪律下拒绝（需先修源）")
    w2, h2 = w // 2, h // 2
    out = bytearray(w2 * h2 * 4)
    for y2 in range(h2):
        y0, y1 = (y2 * 2) * w, (y2 * 2 + 1) * w
        row2 = y2 * w2
        for x2 in range(w2):
            x0, x1 = x2 * 2, x2 * 2 + 1
            acc = [0, 0, 0, 0]
            for off in ((y0 + x0), (y0 + x1), (y1 + x0), (y1 + x1)):
                b = off * 4
                acc[0] += rgba[b]
                acc[1] += rgba[b + 1]
                acc[2] += rgba[b + 2]
                acc[3] += rgba[b + 3]
            d = row2 + x2
            out[d * 4:d * 4 + 4] = bytes(a >> 2 for a in acc)  # /4 = >>2（定点无舍入偏差）
    return w2, h2, ctype, out


def encode_rgba_rows(w: int, h: int, ctype: int, rgba: bytearray) -> bytes:
    """RGBA 像素 → filter=0 行流（每行行首 1 字节滤波标志，分配时已置 0）。"""
    channels = 3 if ctype == 2 else 4
    stride = w * channels
    rows = bytearray((stride + 1) * h)
    pos = 1  # 行首 filter 字节占 1 字节（值 0=None，分配时已置 0）
    for i in range(w * h):
        b = i * 4
        if channels == 3:
            rows[pos:pos + 3] = rgba[b:b + 3]
        else:
            rows[pos:pos + 4] = rgba[b:b + 4]
        pos += channels
        if (i + 1) % w == 0:
            pos += 1  # 跳过下一行 filter 字节
    return encode_png(w, h, ctype, bytes(rows))


def batch_items(batch: str):
    m = json.load(open(os.path.join(DST_ROOT, f"{batch}-manifest.json"), encoding="utf-8"))
    for it in m["items"]:
        yield it


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="发布期图片聚合（T-096-v02）")
    parser.add_argument("--verify", action="store_true", help="只校验台账与产物，不入库")
    args = parser.parse_args(argv)
    force = os.environ.get("FORCE") == "1"
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

    if not os.path.isdir(SRC_ROOT):
        print(f"aggregate: 外部轨源目录不在位：{SRC_ROOT}（U 盘未挂载，聚合/校验均不可执行）",
              file=sys.stderr)
        return 2

    ledger = {
        "schema": "game-assets/expand-aggregation/1",
        "task": "T-096-v02 发布期图片聚合（31 号 §五.5 包体策略 + D21-5 双轨发布轨）",
        "produced_at": time.strftime("%Y-%m-%d"),
        # RL9：配置/数据禁绝对盘符路径——外部轨源的盘符事实只登记在
        # tools/art/expand_aggregate_images.py（EXPAND_V1_IMAGES 可覆盖）
        # 与 docs/engineering/07-decided-plan.md §三（附），台账只记相对布局。
        "source_root_env": "EXPAND_V1_IMAGES",
        "source_layout": "{P1|P3|P4}/<category>/<file>（与 visual-assets.json "
                         "images[].file 同构；源=U 盘外部轨归档）",
        "decision": {
            "kernel": "BOX 2x area average（整数倍降采样纪律；像素化语义仅跃迁视频链 Q18A）",
            "1536x1024->768x512": "P1 题图 7 + P4 加载图 6 + P3 独占彩蛋 2",
            "1024x1024->512x512": "P1 插画 15 + P1 钩子 5 + P3 异变 4 + P4 皮肤 6",
            "1024x1536->512x768": "P4 军师立绘 4",
            "64x64 passthrough": "P4 成就图标 24（程序合成件已入库，仅校验不下采样）",
            "skins_nine_slice": "皮肤以 StyleBoxTexture 九宫拉伸消费（单贴图+边距 1/8），"
                                "不切文件——切九块再拼回是同一语义的劣化实现",
        },
        "items": [],
    }
    copied = skipped = verified = 0
    total_src = total_dst = 0

    for batch in ("P1", "P3", "P4"):
        for it in batch_items(batch):
            rel = it["file"].replace("\\", "/")
            # manifest file 为批次内相对路径；repo 聚合落位=目录口径
            # game/assets/expand-v1/{P1|P3|P4}/<file>（与 visual-assets.json
            # images[].file 字段一致——ExpandAssetPaths 内置轨回退按该树命中）
            src = os.path.join(SRC_ROOT, batch, *rel.split("/"))
            dst = os.path.join(DST_ROOT, batch, *rel.split("/"))
            entry = {
                "id": it["id"], "batch": batch, "file": rel,
                "category": it.get("category", ""),
                "src_resolution": it.get("resolution", ""),
            }
            if not os.path.isfile(src):
                print(f"aggregate: 源缺失 {rel}（{src}）", file=sys.stderr)
                return 1
            src_bytes = open(src, "rb").read()
            entry["src_sha256"] = hashlib.sha256(src_bytes).hexdigest()
            entry["src_bytes"] = len(src_bytes)

            if it.get("resolution") == "64x64":
                # 成就图标：程序合成件已在仓，仅校验在位（发布树随 build_v020 装配）
                if not os.path.isfile(dst):
                    print(f"aggregate: 成就图标在仓缺失 {rel}", file=sys.stderr)
                    return 1
                entry["dst_resolution"] = "64x64"
                entry["dst_sha256"] = sha256_file(dst)
                entry["action"] = "verified-in-repo"
                verified += 1
                ledger["items"].append(entry)
                continue

            w, h, ctype, rgba = decode_rgba(src_bytes)
            if f"{w}x{h}" != it.get("resolution", ""):
                print(f"aggregate: {rel} 实测 {w}x{h} ≠ manifest 声明 {it.get('resolution')}",
                      file=sys.stderr)
                return 1
            w2, h2, _, rgba2 = downsample_2x_box(w, h, ctype, rgba)
            out_bytes = encode_rgba_rows(w2, h2, ctype, rgba2)
            entry["dst_resolution"] = f"{w2}x{h2}"
            entry["dst_bytes"] = len(out_bytes)
            entry["action"] = "2x-box-downsampled"

            if os.path.isfile(dst) and not force and not args.verify:
                if sha256_file(dst) == hashlib.sha256(out_bytes).hexdigest():
                    entry["dst_sha256"] = sha256_file(dst)
                    entry["action"] += " (skipped: identical)"
                    skipped += 1
                    total_src += entry["src_bytes"]
                    total_dst += entry["dst_bytes"]
                    ledger["items"].append(entry)
                    continue
            if args.verify:
                if not os.path.isfile(dst):
                    print(f"aggregate: --verify 产物缺失 {rel}", file=sys.stderr)
                    return 1
                if sha256_file(dst) != hashlib.sha256(out_bytes).hexdigest():
                    print(f"aggregate: --verify 产物与源降采样结果不一致 {rel}", file=sys.stderr)
                    return 1
                entry["dst_sha256"] = sha256_file(dst)
                entry["action"] += " (verified)"
                verified += 1
                total_src += entry["src_bytes"]
                total_dst += entry["dst_bytes"]
                ledger["items"].append(entry)
                continue

            os.makedirs(os.path.dirname(dst), exist_ok=True)
            with open(dst, "wb") as fh:
                fh.write(out_bytes)
            entry["dst_sha256"] = hashlib.sha256(out_bytes).hexdigest()
            copied += 1
            total_src += entry["src_bytes"]
            total_dst += entry["dst_bytes"]
            ledger["items"].append(entry)
            print(f"  {rel}  {w}x{h} -> {w2}x{h2}  {entry['src_bytes']} -> {len(out_bytes)} B")

    n = len(ledger["items"])
    ledger["counts"] = {
        "external_images_aggregated": n - verified,
        "achievement_icons_verified": verified,
        "total": n,
        "bytes_before": total_src,
        "bytes_after": total_dst,
    }
    if n != 73:
        print(f"aggregate: 目录口径 73 图（49 外部+24 图标），实得 {n}", file=sys.stderr)
        return 1

    if not args.verify:
        with open(LEDGER, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(ledger, fh, ensure_ascii=False, indent=1)
            fh.write("\n")
        print(f"ledger: {os.path.relpath(LEDGER, REPO).replace(os.sep, '/')}")
    print(f"summary: items={n} copied={copied} skipped={skipped} verified={verified} "
          f"bytes {total_src} -> {total_dst}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
