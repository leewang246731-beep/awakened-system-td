#!/usr/bin/env python3
"""gen_hud_icons.py — W10/T-163 前置：战斗 HUD 图标全集生成（vibeapi/gpt-image-2）。

清单单一数据源=game/content/era{1..6}/{era,towers,enemies}.json：
  towers    21 座（建造栏）
  resources 去重 17 种（顶部资源条，按 name_en 跨时代去重）
  enemies   54 种（波次预告，各时代 boss 身份按终关剧情特写）
产物：1024x1024 母带 → docs/screenshots/w10-icons-raw/（存档）
      128x128 BOX 8x 整数降采样 → game/presentation/EraTD/textures/ui/icons/{类}/{id}.png
文件名 = 内容 id 冒号转横线（era3-enemy-boss.png），T-163 按 id 确定性映射加载。
图内一律 NO TEXT。断点续跑：目标位已有即跳过。
用法：python tools/art/gen_hud_icons.py [towers|resources|enemies|all]
密钥：config/secrets.local.json → vibeapi_image（gitignored，R5）
"""
import base64
import json
import os
import sys
import urllib.request

from PIL import Image

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
CONTENT = os.path.join(REPO, "game", "content")
OUT_RAW = os.path.join(REPO, "docs", "screenshots", "w10-icons-raw")
OUT_ICONS = os.path.join(REPO, "game", "presentation", "EraTD", "textures", "ui", "icons")
CFG = json.load(open(os.path.join(REPO, "config", "secrets.local.json"), encoding="utf-8"))["vibeapi_image"]

ERA_PALETTE = {
    "era1": "stone-age ochre and moss green palette",
    "era2": "classical marble white and antique gold palette",
    "era3": "steampunk brass, copper and soot palette",
    "era4": "modern glass-blue and steel palette",
    "era5": "near-future neon cyan and violet palette",
    "era6": "cosmic deep-space violet and crystal teal palette",
}

# 各时代终关 boss 身份（对应 e{era}-final 关卡剧情）
BOSS_SUBJECT = {
    "era1:enemy-boss": "colossal beast-king with bone crown and moss fur, the Treader",
    "era2:enemy-boss": "iron-hoof warlord in crested helmet on an armored warhorse",
    "era3:enemy-boss": "rolling steam fortress land-battleship boss with glowing furnace",
    "era4:enemy-boss": "night-owl shaped stealth bomber boss descending with radar rings",
    "era5:enemy-boss": "the Dominator, giant mecha mothership core with thruster halo",
    "era6:enemy-boss": "the Watchkeeper, cosmic entity of crystal and starlight eye",
}

# 难词/易歧义词的人工英译覆写（默认用 name_en）
SUBJECT_OVERRIDE = {
    "era1:enemy-boomshrew": "explosive shrew rodent with a fuse tail",
    "era1:enemy-warband": "howling warband of tribal raiders",
    "era1:enemy-ram": "battering ram log carried by two brutes",
    "era2:enemy-ram": "armored battering ram on wheels",
    "era2:enemy-firearrow": "fire-arrow raider lobbing burning arrows",
    "era3:enemy-ram": "ironclad steam battering ram vehicle",
    "era3:enemy-sapper": "sapper engineer with dynamite satchel",
    "era4:enemy-ew-officer": "electronic-warfare officer with jammer antenna backpack",
    "era4:enemy-kamikaze-drone": "kamikaze drone with exposed warhead",
    "era5:enemy-nano-swarm": "swarming nano-machines forming a clawed cloud",
    "era5:enemy-nexus-core": "nexus core, floating armored octahedron",
    "era5:enemy-melt-drone": "melter drone with focused heat beam emitter",
    "era6:enemy-void-swarm": "void swarm of shadowy wisps",
    "era6:enemy-entropy-wraith": "entropy wraith, hooded void ghost trailing dust",
    "era6:enemy-choir-core": "choir core, ringed crystal node resonating with light",
    "era6:enemy-null-frame": "null-frame, matte-black inverted mech",
    "era2:res-horse": "horse, resource livestock",
}

STYLE = ("Single square game HUD icon, high-detail 16-bit pixel art. One centered "
         "subject with a clear readable silhouette at small size, plain dark slate "
         "background, subtle rim light, chunky visible pixels, {palette}. "
         "NO TEXT, no letters, no numbers, no border frame. Subject: {subject}.")


def load_inventory():
    jobs = []  # (category, full_id, subject, palette)
    seen_res = set()
    for i in range(1, 7):
        era = f"era{i}"
        towers = json.load(open(os.path.join(CONTENT, era, "towers.json"), encoding="utf-8"))["items"]
        for t in towers:
            ne = t["name_en"]
            suffix = "" if ne.lower().endswith(("tower", "wall", "trap", "campfire")) else " tower"
            jobs.append(("towers", t["id"], f"{ne}{suffix}", ERA_PALETTE[era]))
        enemies = json.load(open(os.path.join(CONTENT, era, "enemies.json"), encoding="utf-8"))["items"]
        for e in enemies:
            subject = BOSS_SUBJECT.get(e["id"]) or SUBJECT_OVERRIDE.get(e["id"]) or e["name_en"]
            jobs.append(("enemies", e["id"], subject, ERA_PALETTE[era]))
        era_items = json.load(open(os.path.join(CONTENT, era, "era.json"), encoding="utf-8"))["items"][0]
        for r in era_items.get("resources", []):
            key = (r["name_en"],)
            if key in seen_res:
                continue
            seen_res.add(key)
            subject = SUBJECT_OVERRIDE.get(r["id"]) or f"{r['name_en']} resource"
            jobs.append(("resources", r["id"], subject, ERA_PALETTE[era]))
    return jobs


def fetch_one(prompt: str) -> bytes:
    body = json.dumps({"model": "gpt-image-2", "prompt": prompt, "size": "1024x1024", "n": 1}).encode()
    req = urllib.request.Request(
        CFG["url"].rstrip("/") + "/v1/images/generations",
        data=body,
        headers={"Authorization": f"Bearer {CFG['key']}", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=300) as resp:
        data = json.loads(resp.read())
    item = data["data"][0]
    if item.get("b64_json"):
        return base64.b64decode(item["b64_json"])
    if item.get("url"):
        with urllib.request.urlopen(item["url"], timeout=300) as r:
            return r.read()
    raise RuntimeError(f"unexpected response keys={list(item.keys())}")


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else "all"
    jobs = load_inventory()
    if only != "all":
        jobs = [j for j in jobs if j[0] == only]
    os.makedirs(OUT_RAW, exist_ok=True)
    ok = fail = skip = 0
    for cat, full_id, subject, palette in jobs:
        fname = f"{full_id.replace(':', '-')}.png"
        dst = os.path.join(OUT_ICONS, cat, fname)
        if os.path.exists(dst):
            skip += 1
            continue
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        try:
            png = fetch_one(STYLE.format(palette=palette, subject=subject))
            raw_path = os.path.join(OUT_RAW, fname)
            open(raw_path, "wb").write(png)
            im = Image.open(raw_path).convert("RGB")
            im.resize((im.width // 8, im.height // 8), Image.Resampling.BOX).save(dst, optimize=True)
            print(f"[ok]   {cat}/{fname}", flush=True)
            ok += 1
        except Exception as e:
            print(f"[FAIL] {cat}/{fname}: {e}", flush=True)
            fail += 1
    print(f"DONE ok={ok} fail={fail} skip={skip} total={len(jobs)}")
    raise SystemExit(1 if fail else 0)


if __name__ == "__main__":
    main()
