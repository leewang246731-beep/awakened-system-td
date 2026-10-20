#!/usr/bin/env python3
"""gen_ui_control_icons.py — W10/T-164 前置：HUD/卡片 UI 控件小图标组（vibeapi/gpt-image-2）。

替换 T-163 遗留文字符号（节奏/✕/把手）与 T-162 遗留 10px 字体星号。
产物：1024x1024 母带 → docs/screenshots/w10-icons-raw/（gitignored 存档）
      128x128 BOX 8x → game/presentation/EraTD/textures/ui/icons/ui/*.png
中性深底+浅色图形（控件件非时代件——交通灯语义域）。NO TEXT（倍速数字除外，
1x/2x/3x 为刻意的数字字形）。断点续跑。
用法：python tools/art/gen_ui_control_icons.py
"""
import base64
import json
import os
import urllib.request

from PIL import Image

REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
OUT_RAW = os.path.join(REPO, "docs", "screenshots", "w10-icons-raw")
OUT_DST = os.path.join(REPO, "game", "presentation", "EraTD", "textures", "ui", "icons", "ui")
CFG = json.load(open(os.path.join(REPO, "config", "secrets.local.json"), encoding="utf-8"))["vibeapi_image"]

ICONS = [
    ("ctrl-play", "a solid play triangle, universally readable game speed-control glyph"),
    ("ctrl-pause", "two solid pause bars, universally readable game pause glyph"),
    ("ctrl-speed-1x", "a speedometer dial glyph with a single large digit 1 beside it, the only text is the digit 1"),
    ("ctrl-speed-2x", "a speedometer dial glyph with double chevrons, suggesting double speed"),
    ("ctrl-speed-3x", "a speedometer dial glyph with triple chevrons, suggesting triple speed"),
    ("ctrl-close", "a bold X close glyph with rounded corners, universally readable"),
    ("ctrl-drawer-handle", "a horizontal tab handle with three grip ridges, like a drawer pull tab"),
    ("ctrl-star", "a solid five-pointed star, game rating glyph"),
    ("ctrl-star-empty", "a five-pointed star outline, empty rating slot glyph"),
    ("ctrl-lock", "a solid padlock, locked-content glyph"),
]

STYLE = ("Single square game UI control icon, high-detail 16-bit pixel art, one centered glyph "
         "with bold readable silhouette at tiny sizes, plain dark slate background, pale "
         "off-white glyph with a thin cool grey rim, chunky pixels. NO TEXT except where the "
         "subject explicitly includes one glyph digit. Subject: {subject}.")


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
    os.makedirs(OUT_RAW, exist_ok=True)
    os.makedirs(OUT_DST, exist_ok=True)
    ok = fail = skip = 0
    for name, subject in ICONS:
        dst = os.path.join(OUT_DST, f"{name}.png")
        if os.path.exists(dst):
            print(f"[skip] {name}")
            skip += 1
            continue
        try:
            png = fetch_one(STYLE.format(subject=subject))
            raw_path = os.path.join(OUT_RAW, f"{name}.png")
            open(raw_path, "wb").write(png)
            im = Image.open(raw_path).convert("RGB")
            im.resize((im.width // 8, im.height // 8), Image.Resampling.BOX).save(dst, optimize=True)
            print(f"[ok]   {name}.png", flush=True)
            ok += 1
        except Exception as e:
            print(f"[FAIL] {name}: {e}", flush=True)
            fail += 1
    print(f"DONE ok={ok} fail={fail} skip={skip}")
    raise SystemExit(1 if fail else 0)


if __name__ == "__main__":
    main()
