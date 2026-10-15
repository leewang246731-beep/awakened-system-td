#!/usr/bin/env python3
"""build_visual_asset_catalog.py — T-152 ②/③ 素材目录内容生成器。

读 game/assets/expand-v1/{A..E,P1,P3,P4}-manifest.json（入库 manifest 副本）+
game/assets/cinematics/manifest.json（① 转码台账），生成
game/content/cinematics/visual-assets.json——演出引擎与表现层的唯一素材目录
（RL7：视频时长/像素化/音频位/用途位/色板全部入内容，产品代码零硬编码）。

口径来源：31 号 v2.0 §二.2 明细表（usage/像素化/音频列）、§1.2 六时代身份卡
（色板锚）、§二.1/2.3/2.4 图批清单（73 图）。幂等：重跑输出字节稳定（排序+固定序）。

用法：python tools/art/build_visual_asset_catalog.py
"""

from __future__ import annotations

import json
import os
import subprocess
import sys

VIDEO_SRC_ROOT = os.environ.get("EXPAND_V1_VIDEOS", "E:/game-videos/expand-v1")


def probe_duration(mp4_name: str, layer: str) -> float:
    proc = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0",
         os.path.join(VIDEO_SRC_ROOT, layer, mp4_name)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if proc.returncode != 0:
        sys.exit(f"build_visual_asset_catalog: ffprobe 失败 {mp4_name}（U 盘未挂载？）")
    return float(proc.stdout.strip())

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
EXPAND = os.path.join(REPO, "game", "assets", "expand-v1")
OUT = os.path.join(REPO, "game", "content", "cinematics", "visual-assets.json")

# 31 号 §二.2 用途位列（ID→usage）；E 层 manifest id=E-a..E-d（31 号表 E-01..04）
USAGE = {
    "A-01": "menu_loop", "A-02": "era_transition", "A-03": "era_transition",
    "A-04": "era_transition", "A-05": "era_transition", "A-06": "era_transition",
    "A-07": "finale",
    **{f"B-0{i}": "wonder" for i in range(1, 7)},
    **{f"B-0{i}": "advisor_milestone" for i in range(7, 10)},
    "B-10": "advisor_milestone", "B-11": "advisor_milestone",
    "B-12": "persona_unlock", "B-13": "persona_unlock",
    "B-14": "persona_unlock", "B-15": "persona_unlock",
    **{f"C-0{i}": "boss_enter" for i in range(1, 5)},
    **{f"C-0{i}": "boss_death" for i in range(5, 9)},
    **{f"C-{n:02d}": "level_opening" for n in range(9, 16)},
    "C-16": "first_clear", "C-17": "first_clear",
    **{f"D-0{i}": "memory" for i in range(1, 9)},
    "E-a": "easter_e09", "E-b": "easter_e05",
    "E-c": "hidden_finale_teaser", "E-d": "endless_milestone",
}

# 31 号 §1.2 六时代视觉身份卡（hex 去井号小写）
PALETTES = {
    "e1": ("晨曦赭金", "c9915c", ["6b7a5a", "e07b39"], "木望楼"),
    "e2": ("青铜苍", "4e8a6e", ["b3593a", "e8e2d0"], "石堡箭楼"),
    "e3": ("铆铁赭红", "a8503c", ["2e2a28", "c9c4bc"], "铆接铁塔"),
    "e4": ("电网蓝", "3d7ea6", ["9a9a94", "e3c14f"], "混凝土雷达塔"),
    "e5": ("聚变青", "3fc1c9", ["8a5fc9", "dce4e8"], "能量尖塔"),
    "e6": ("星门靛蓝", "3e55c9", ["1d1b33", "bfe8f2"], "星门"),
}

STRIPS = [  # (资产 id, 文件, 时代, 演出位)——31 号 C 层：入场 4+败亡 4
    ("C-01", "boss-e1-enter", "e1", "boss_enter"),
    ("C-02", "boss-e2-enter", "e2", "boss_enter"),
    ("C-03", "boss-e3-enter", "e3", "boss_enter"),
    ("C-04", "boss-e4-enter", "e4", "boss_enter"),
    ("C-05", "boss-e1-death", "e1", "boss_death"),
    ("C-06", "boss-e2-death", "e2", "boss_death"),
    ("C-07", "boss-e3-death", "e3", "boss_death"),
    ("C-08", "boss-e4-death", "e4", "boss_death"),
]

# 图批（31 号 §二.1/2.3/2.4；外部轨根=E:/game-assets/expand-v1，D21-5 双轨）
IMAGE_BATCHES = {
    "banners": ("P1", "banners"),     # 7：E1 四关+E2 三关题图（关卡装载/章节选择位）
    "story": ("P1", "story"),         # 15：叙事插画（DialogueLine.ImageKey 气泡配图位）
    "hooks": ("P1", "hooks"),         # 5：跨时代钩子视觉（章末卡点预告）
    "easter": ("P3", None),           # 6：T1 四件变体+T2 两封面帧（variants+exclusives）
    "skins": ("P4", "skins"),         # 6：UI 时代皮肤（HoloSkin 时代变体）
    "loading": ("P4", "loading"),     # 6：加载图（加载屏/章节选择背景）
    "personas": ("P4", "personas"),   # 4：军师人格立绘（人格切换头像位）
}


def load_manifest(name: str) -> dict:
    with open(os.path.join(EXPAND, f"{name}-manifest.json"), encoding="utf-8") as fh:
        return json.load(fh)


def image_entries() -> dict:
    batches = {}
    for batch, (manifest_name, subdir) in IMAGE_BATCHES.items():
        manifest = load_manifest(manifest_name)
        entries = []
        for item in manifest["items"]:
            rel_file = (item.get("file") or "").replace("\\", "/")
            if subdir and not rel_file.startswith(subdir + "/"):
                continue  # P3 两目录 variants/exclusives 都要 → subdir=None 全收
            entries.append({
                "id": item["id"],
                # manifest 的 file 字段相对批次根（P1/P3/P4）——外部轨全路径=
                # external_image_root + 批次前缀 + file（U 盘目录实况）
                "file": f"{manifest_name}/{rel_file}",
                "era": item.get("era", infer_era(item["id"])),
            })
        if not entries:
            sys.exit(f"build_visual_asset_catalog: {batch} 批为空（manifest={manifest_name}）")
        batches[batch] = entries
    return batches


ERA_RE = {
    "e1": "e1", "e2": "e2", "e3": "e3", "e4": "e4", "e5": "e5",
    "e6": "e6", "e05": None, "e09": None,
}


def infer_era(asset_id: str) -> str:
    match = None
    for token in ("e1", "e2", "e3", "e4", "e5", "e6"):
        if re_search(token, asset_id):
            if match is None or len(token) > len(match):
                match = token
    return match or "e1"


def re_search(token: str, text: str) -> bool:
    import re
    return re.search(rf"(^|[^0-9]){token}([^0-9]|$)", text) is not None


def video_entries() -> list:
    entries = []
    for layer in "ABCDE":
        manifest = load_manifest(layer)
        for item in manifest["items"]:
            vid = item["id"]
            if vid not in USAGE:
                sys.exit(f"build_visual_asset_catalog: {vid} 缺 usage 映射（31 号 §二.2）")
            raw_name = (item.get("file") or "").replace("\\", "/").split("/")[-1]
            duration = float(item.get("duration_s") or 0)
            if duration <= 0:  # D 层 manifest 无 duration_s——ffprobe 实测母片
                duration = probe_duration(raw_name, layer)
            entries.append({
                "id": vid,
                "layer": layer,
                "file": f"{layer}/{vid.lower()}.ogv",
                "duration_s": round(duration, 2),
                "pixelate": vid in ("A-02", "A-03", "A-04", "A-05", "A-06"),
                "audio": layer in ("A", "D", "E"),
                "usage": USAGE[vid],
            })
    entries.sort(key=lambda e: e["id"])
    return entries


def achievement_entries() -> list:
    manifest = load_manifest("P4")
    entries = []
    for item in manifest["items"]:
        if item["id"].startswith("achv-"):
            entries.append({"id": item["id"], "file": f"P4/achievements/{item['id']}.png"})
    return entries


def main() -> None:
    catalog = {
        "$schema": "urn:eratd:content:visualassets:1",
        "content_type": "VisualAssetCatalog",
        "_annotation": (
            "T-152 素材目录（31 号 v2.0 §二/§三 插入点映射）。videos/strips/composite=repo 内置轨"
            "（game/assets/cinematics/）；images 各批=外部轨（D21-5 双轨：开发期 U 盘 "
            "E:/game-assets/expand-v1，发布期聚合拷入 res://）；achievements=repo 内置 "
            "game/assets/expand-v1/P4/achievements。时长/像素化/音频/用途/色板=31 号 §二.2/§1.2 口径。"
        ),
        "repo_video_root": "game/assets/cinematics",
        "external_image_root": "E:/game-assets/expand-v1",
        "repo_achievement_root": "game/assets/expand-v1/P4/achievements",
        "ticks_per_second": 30,
        "videos": video_entries(),
        "strips": [
            {"id": vid, "file": f"strips/{name}.png", "era": era, "kind": kind,
             "frames": 36, "frame_w": 240, "frame_h": 135, "cols": 6}
            for vid, name, era, kind in STRIPS
        ],
        "composite": {
            "id": "zero-truth",
            "file": "D/zero-truth.ogv",
            "duration_s": 60.0,
            "requires": [f"D-0{i}" for i in range(1, 9)],
        },
        "images": image_entries(),
        "achievements": achievement_entries(),
        "palettes": {
            era: {"label_zh": label, "primary": primary, "secondary": secondary,
                  "motif_zh": motif}
            for era, (label, primary, secondary, motif) in PALETTES.items()
        },
    }

    counts = {
        "videos": len(catalog["videos"]), "strips": len(catalog["strips"]),
        **{k: len(v) for k, v in catalog["images"].items()},
        "achievements": len(catalog["achievements"]),
    }
    total_images = sum(len(v) for v in catalog["images"].values()) + len(catalog["achievements"])
    expected = {"videos": 51, "strips": 8, "banners": 7, "story": 15, "hooks": 5,
                "easter": 6, "skins": 6, "loading": 6, "personas": 4,
                "achievements": 24}
    if counts != expected:
        sys.exit(f"build_visual_asset_catalog: 计数不符 {counts} vs 31 号口径 {expected}")
    expected_images = 73  # 31 号 §二.5：题图 7+插画 15+钩子 5+彩蛋 6+皮肤 6+加载 6+立绘 4+成就图标 24
    if total_images != expected_images:
        sys.exit(f"build_visual_asset_catalog: 图合计 {total_images} ≠ {expected_images}（31 号 §二.5）")

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as fh:
        json.dump(catalog, fh, ensure_ascii=False, indent=1)
        fh.write("\n")
    print(f"catalog -> game/content/cinematics/visual-assets.json "
          f"(videos={counts['videos']} images={total_images} strips={counts['strips']} "
          f"achievements={counts['achievements']})")


if __name__ == "__main__":
    main()
