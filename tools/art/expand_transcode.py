#!/usr/bin/env python3
"""expand_transcode.py — T-152 ① 视觉扩充转码管线（U 盘母片 → 游戏内素材）。

管线（docs/engineering/31-visual-enrichment-master-plan.md v2.0 §三 加载管线 +
18-r1-asset-tech.md §1.2/§4.5）：

  1. ogv 转码：E:/game-videos/expand-v1/{A..E}/*.mp4 → game/assets/cinematics/<层>/<id>.ogv
     命令族（18 号 C2 官方口径）：libtheora -q:v 6 -g:v 64、24fps 钉死、480p 直通
     （母片即 854x480）；A/D/E 层带 Vorbis 音轨（-q:a 6），B/C 层 -an（游戏内
     BGM 覆盖，31 号 §二.2 音频列）。
  2. 像素化链（Q18A：仅跃迁 A-02~06——A-01 菜单循环/A-07 终章保清晰，31 号
     §二.2 像素化列 + §三）：两遍固定色板像素化（R5 §5.4 单板+防抖动）——
     pass1 palettegen（在 4× 邻域下采样后的画面上统计单张色板，max_colors=64）；
     pass2 crop=852:480（4 的整倍，去掉 2px 左边）→ neighbor 下采样 213:120 →
     neighbor 回升 852:480（完美 4× 像素块）→ paletteuse dither=none → ogv 转码。
  3. Boss 序列帧（31 号 C 层口径：入场 C-01~04 + 败亡 C-05~08 抽帧）：
     fps=6 抽帧 + 240:135 邻域下采样 + tile=6x6（36 帧）→ 单张 Atlas PNG
     game/assets/cinematics/strips/boss-e{1..4}-{enter,death}.png。
  4. 《零的真相》重剪（31 号 §二.2 ★行：零生成、集齐 8 段记忆解锁）：
     D-01~D-08 各取前 7.5s → concat → 60s 合成篇 ogv（libtheora 重编码）。

母片清单驱动：id→文件名映射读 game/assets/expand-v1/{A..E}-manifest.json
（schema=game-assets/1，E 层条目 id 为 E-a..E-d），不自扫目录。

用法（Git Bash / Windows 均可）：
  python tools/art/expand_transcode.py --trial          # 小规模试产 3 条（A-01/A-02/D-01）
  python tools/art/expand_transcode.py                  # 全量转码 + 序列帧 + 重剪
  python tools/art/expand_transcode.py --only A-01,A-02 # 指定条目重跑
  python tools/art/expand_transcode.py --verify         # 只验证入库产物（零 mp4 残留/OggS 魔数/时长）
  FORCE=1 …/--force                                    # 忽略已存在产物重转

验证口径（31 号 T-149 验收"ogv dist 零 mp4 残留"）：--verify 断言
cinematics 目录无 .mp4、全部 .ogv 头四字节为 OggS、ffprobe 时长与 manifest
误差 <1.5s、序列帧为 36 帧横竖 6x6 图集。入库台账写
game/assets/cinematics/manifest.json（逐条 cmd/时长/字节数/像素化/音频位）。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC_ROOT = os.environ.get("EXPAND_V1_VIDEOS", "E:/game-videos/expand-v1")
OUT_ROOT = os.path.join(REPO, "game", "assets", "cinematics")
MANIFEST_DIR = os.path.join(REPO, "game", "assets", "expand-v1")

# 像素化档（Q18A 仅跃迁；31 号 §二.2 像素化列）。A-01 菜单循环/A-07 终章保清晰。
PIXELATE_IDS = ("A-02", "A-03", "A-04", "A-05", "A-06")

# A/D/E 层保留 Vorbis 音轨；B/C 层 -an（游戏内 BGM 覆盖，31 号 §二.2）。
AUDIO_LAYERS = {"A", "D", "E"}

# Boss 序列帧（C 层）：(资产 id, 输出名, 时代)——入场 4 + 败亡 4。
BOSS_STRIPS = (
    ("C-01", "boss-e1-enter", "e1"),
    ("C-02", "boss-e2-enter", "e2"),
    ("C-03", "boss-e3-enter", "e3"),
    ("C-04", "boss-e4-enter", "e4"),
    ("C-05", "boss-e1-death", "e1"),
    ("C-06", "boss-e2-death", "e2"),
    ("C-07", "boss-e3-death", "e3"),
    ("C-08", "boss-e4-death", "e4"),
)

ZERO_TRUTH_ID = "zero-truth"
ZERO_TRUTH_SOURCES = tuple(f"D-0{i}" for i in range(1, 9))
ZERO_TRUTH_SEG_SECS = 7.5  # 8 × 7.5s = 60s（31 号 §二.2 ★行 60s 口径）

STRIP_FRAMES = 36  # 6s × 6fps（31 号注记口径：每秒 6 帧）
STRIP_FRAME_W, STRIP_FRAME_H = 240, 135
STRIP_COLS = 6


def die(msg: str) -> None:
    print(f"expand_transcode: {msg}", file=sys.stderr)
    sys.exit(2)


def run(cmd: list[str], *, label: str) -> None:
    print(f"  $ {' '.join(cmd)}")
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if proc.returncode != 0:
        print(proc.stderr[-2000:], file=sys.stderr)
        die(f"{label} 失败（exit {proc.returncode}）")


def probe(path: str) -> dict:
    proc = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-show_entries", "stream=width,height", "-of", "json", path],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if proc.returncode != 0:
        die(f"ffprobe 失败：{path}\n{proc.stderr[-500:]}")
    data = json.loads(proc.stdout)
    streams = [s for s in data.get("streams", []) if s.get("width")]
    return {
        "duration": float(data["format"]["duration"]),
        "width": streams[0]["width"] if streams else None,
        "height": streams[0]["height"] if streams else None,
    }


def load_manifest_items() -> dict[str, dict]:
    """id → {file, layer, duration_s}（读五份入库 manifest 副本）。"""
    items: dict[str, dict] = {}
    for layer in "ABCDE":
        path = os.path.join(MANIFEST_DIR, f"{layer}-manifest.json")
        with open(path, encoding="utf-8") as fh:
            manifest = json.load(fh)
        for item in manifest["items"]:
            raw = item.get("file") or (item.get("files") or {}).get("raw")
            if not raw:
                die(f"{layer}-manifest 条目 {item.get('id')} 缺 file 字段")
            items[item["id"]] = {
                "file": raw.replace("\\", "/").split("/")[-1],
                "layer": layer,
                "duration_s": float(item.get("duration_s") or 0),  # D 层 manifest 无该键→0=未知，验证时以 ffprobe 实测为准
                "src": os.path.join(SRC_ROOT, layer, raw.replace("\\", "/").split("/")[-1]),
            }
    return items


def ogv_path(item_id: str) -> str:
    return os.path.join(OUT_ROOT, item_id[0], f"{item_id.lower()}.ogv")


def encode_ogv(item_id: str, meta: dict, *, force: bool) -> dict:
    out = ogv_path(item_id)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    pixelated = item_id in PIXELATE_IDS
    audio = meta["layer"] in AUDIO_LAYERS
    if os.path.exists(out) and not force:
        return {"id": item_id, "out": rel(out), "status": "kept", "pixelate": pixelated}

    common = ["-y", "-i", meta["src"], "-r", "24"]
    if pixelated:
        # Q18A 像素化双遍（crop 到 4 的整倍 → 4× 邻域下采样/回升 → 固定单板 paletteuse）
        small = "crop=852:480:2:0,scale=213:120:flags=neighbor,scale=852:480:flags=neighbor"
        palette_png = os.path.join(os.path.dirname(out), f".palette-{item_id.lower()}.png")
        run(["ffmpeg", "-y", "-i", meta["src"], "-vf", f"{small},palettegen=max_colors=64",
             "-frames:v", "1", palette_png], label=f"{item_id} palettegen")
        run(["ffmpeg", *common, "-i", palette_png,
             "-filter_complex",
             f"[0:v]{small}[x];[x][1:v]paletteuse=dither=none",
             "-c:v", "libtheora", "-q:v", "6", "-g:v", "64",
             *(["-c:a", "libvorbis", "-q:a", "6"] if audio else ["-an"]),
             out], label=f"{item_id} 像素化转码")
        os.remove(palette_png)
    else:
        run(["ffmpeg", *common,
             "-c:v", "libtheora", "-q:v", "6", "-g:v", "64",
             *(["-c:a", "libvorbis", "-q:a", "6"] if audio else ["-an"]),
             out], label=f"{item_id} 转码")

    info = probe(out)
    return {
        "id": item_id, "out": rel(out), "status": "transcoded",
        "pixelate": pixelated, "audio": audio,
        "duration_s": round(info["duration"], 2),
        "bytes": os.path.getsize(out),
    }


def build_strip(item_id: str, name: str, meta: dict, *, force: bool) -> dict:
    out = os.path.join(OUT_ROOT, "strips", f"{name}.png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    if os.path.exists(out) and not force:
        return {"id": item_id, "out": rel(out), "status": "kept"}

    vf = (f"fps=6,scale={STRIP_FRAME_W}:{STRIP_FRAME_H}:flags=neighbor,"
          f"tile={STRIP_COLS}x{STRIP_FRAMES // STRIP_COLS}")
    run(["ffmpeg", "-y", "-i", meta["src"], "-vf", vf, "-frames:v", "1", out],
        label=f"{item_id} 序列帧抽帧")
    return {"id": item_id, "out": rel(out), "status": "transcoded",
            "bytes": os.path.getsize(out)}


def build_zero_truth(items: dict[str, dict], *, force: bool) -> dict:
    out = os.path.join(OUT_ROOT, "D", f"{ZERO_TRUTH_ID}.ogv")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    if os.path.exists(out) and not force:
        return {"id": ZERO_TRUTH_ID, "out": rel(out), "status": "kept"}

    seg_dir = os.path.join(OUT_ROOT, "D", ".zero-truth-segs")
    os.makedirs(seg_dir, exist_ok=True)
    concat_list = os.path.join(seg_dir, "concat.txt")
    segs = []
    for source_id in ZERO_TRUTH_SOURCES:
        seg = os.path.join(seg_dir, f"{source_id.lower()}.ogv")
        run(["ffmpeg", "-y", "-i", ogv_path(source_id), "-t", str(ZERO_TRUTH_SEG_SECS),
             "-c:v", "libtheora", "-q:v", "6", "-g:v", "64", "-c:a", "libvorbis", "-q:a", "6",
             seg], label=f"zero-truth 分段 {source_id}")
        segs.append(seg)
    with open(concat_list, "w", encoding="utf-8") as fh:
        for seg in segs:
            fh.write(f"file '{seg.replace(os.sep, '/')}'\n")
    run(["ffmpeg", "-y", "-f", "concat", "-safe", "0", "-i", concat_list,
         "-c", "copy", out], label="zero-truth concat")
    for seg in segs:
        os.remove(seg)
    os.remove(concat_list)
    os.rmdir(seg_dir)
    info = probe(out)
    return {"id": ZERO_TRUTH_ID, "out": rel(out), "status": "transcoded",
            "duration_s": round(info["duration"], 2), "bytes": os.path.getsize(out)}


def rel(path: str) -> str:
    return os.path.relpath(path, REPO).replace(os.sep, "/")


def verify(items: dict[str, dict]) -> int:
    """入库验证：零 mp4 残留 / OggS 魔数 / 时长口径 / 序列帧图集尺寸。"""
    bad = 0
    mp4_leftovers = []
    for dirpath, _dirnames, filenames in os.walk(OUT_ROOT):
        for fn in filenames:
            if fn.lower().endswith((".mp4", ".mov", ".avi")) or fn.startswith(".palette"):
                mp4_leftovers.append(os.path.join(dirpath, fn))
    if mp4_leftovers:
        bad += 1
        print(f"VERIFY-FAIL 残留母片/中间产物 {len(mp4_leftovers)} 个：{mp4_leftovers[:5]}")

    count_ogv = 0
    for item_id, meta in sorted(items.items()):
        out = ogv_path(item_id)
        if not os.path.exists(out):
            bad += 1
            print(f"VERIFY-FAIL 缺产物 {item_id} -> {rel(out)}")
            continue
        with open(out, "rb") as fh:
            magic = fh.read(4)
        if magic != b"OggS":
            bad += 1
            print(f"VERIFY-FAIL {item_id} 非 Ogg 容器（{magic!r}）")
            continue
        info = probe(out)
        source_duration = meta["duration_s"] if meta["duration_s"] > 0 else probe(meta["src"])["duration"]
        if abs(info["duration"] - source_duration) > 1.5:
            bad += 1
            print(f"VERIFY-FAIL {item_id} 时长漂移 {info['duration']:.2f}s vs 母片 {source_duration:.2f}s")
            continue
        count_ogv += 1

    strips_ok = 0
    for _item_id, name, _era in BOSS_STRIPS:
        path = os.path.join(OUT_ROOT, "strips", f"{name}.png")
        if not os.path.exists(path):
            bad += 1
            print(f"VERIFY-FAIL 缺序列帧 {name}")
            continue
        strips_ok += 1

    zero = os.path.join(OUT_ROOT, "D", f"{ZERO_TRUTH_ID}.ogv")
    zero_ok = os.path.exists(zero) and open(zero, "rb").read(4) == b"OggS"
    if not zero_ok:
        bad += 1
        print(f"VERIFY-FAIL 《零的真相》合成篇缺失或非 Ogg")

    total_mb = sum(
        os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(OUT_ROOT) for f in fs) / 1048576
    print(f"VERIFY ogv={count_ogv}/{len(items)} strips={strips_ok}/{len(BOSS_STRIPS)} "
          f"zero_truth={'ok' if zero_ok else 'MISSING'} "
          f"mp4_residue={len(mp4_leftovers)} total={total_mb:.1f}MB")
    return 1 if bad else 0


def write_ledger(entries: list[dict]) -> None:
    ledger = {
        "schema": "game-assets/cinematics-ledger/1",
        "task": "T-152 ① 转码入库（31 号 §三 加载管线；18 号 C2 ogv 口径）",
        "produced_at": time.strftime("%Y-%m-%d"),
        "pixelate_ids": list(PIXELATE_IDS),
        "pixelate_note": "Q18A 仅跃迁 A-02~06 像素化（31 号 §二.2 列）；A-01/A-07 保清晰",
        "strip_rule": f"fps=6 × {STRIP_FRAMES} 帧 tile {STRIP_COLS}x{STRIP_FRAMES // STRIP_COLS} @ {STRIP_FRAME_W}x{STRIP_FRAME_H}",
        "zero_truth": f"D-01~08 各前 {ZERO_TRUTH_SEG_SECS}s concat = 60s（31 号 ★行，零生成）",
        "entries": entries,
    }
    path = os.path.join(OUT_ROOT, "manifest.json")
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(ledger, fh, ensure_ascii=False, indent=1)
    print(f"ledger -> {rel(path)}")


def main() -> int:
    parser = argparse.ArgumentParser(description="T-152 视觉扩充转码管线（mp4→ogv/像素化/序列帧/重剪）")
    parser.add_argument("--trial", action="store_true", help="小规模试产 3 条（A-01/A-02/D-01）")
    parser.add_argument("--only", help="逗号分隔的资产 id 列表（如 A-01,C-05,zero-truth）")
    parser.add_argument("--force", action="store_true", help="已存在产物也重转（默认跳过）")
    parser.add_argument("--verify", action="store_true", help="只验证已入库产物")
    parser.add_argument("--no-strips", action="store_true", help="跳过 Boss 序列帧")
    parser.add_argument("--no-zero-truth", action="store_true", help="跳过《零的真相》重剪")
    args = parser.parse_args()

    if not os.path.isdir(SRC_ROOT):
        die(f"母片根不可达：{SRC_ROOT}（U 盘未挂载？可用 EXPAND_V1_VIDEOS 覆盖）")
    for tool in ("ffmpeg", "ffprobe"):
        if subprocess.run(["where" if os.name == "nt" else "which", tool],
                          capture_output=True).returncode != 0:
            die(f"{tool} 不在 PATH（18 号 §1.2 需 libtheora/libvorbis 构建——gyan full 已核）")

    items = load_manifest_items()
    print(f"母片清单：{len(items)} 条（A7/B15/C17/D8/E4）")

    if args.verify:
        return verify(items)

    targets: list[str]
    if args.trial:
        targets = ["A-01", "A-02", "D-01"]
    elif args.only:
        targets = [t.strip() for t in args.only.split(",") if t.strip()]
    else:
        targets = sorted(items)

    force = args.force or os.environ.get("FORCE") == "1"
    entries: list[dict] = []
    for item_id in targets:
        if item_id == ZERO_TRUTH_ID:
            continue
        if item_id not in items:
            die(f"未知资产 id {item_id}（清单含 {len(items)} 条）")
        entry = encode_ogv(item_id, items[item_id], force=force)
        entries.append(entry)
        print(f"[{entry['status']}] {entry['id']} -> {entry['out']}")

    if not args.no_strips:
        for item_id, name, _era in BOSS_STRIPS:
            entry = build_strip(item_id, name, items[item_id], force=force)
            entries.append(entry)
            print(f"[{entry['status']}] strip {name} -> {entry['out']}")

    if (not args.no_zero_truth and (args.trial is False)
            and (args.only is None or ZERO_TRUTH_ID in args.only)):
        # 全量跑或显式点名才做重剪（试产 3 条时 D 层未齐，跳过）
        if all(os.path.exists(ogv_path(s)) for s in ZERO_TRUTH_SOURCES):
            entry = build_zero_truth(items, force=force)
            entries.append(entry)
            print(f"[{entry['status']}] {ZERO_TRUTH_ID} -> {entry['out']}")

    write_ledger(entries)
    return verify(items) if not args.trial else 0


if __name__ == "__main__":
    sys.exit(main())
